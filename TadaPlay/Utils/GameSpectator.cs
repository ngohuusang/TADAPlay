using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Live spectating, using UserPatch 1.5's own spectator stream.
    ///
    /// The game already does all the hard parts: while a match is running it listens on
    /// TCP <see cref="SpectatorPort"/> and streams the game to spectators, applying a join
    /// delay so a spectator cannot be used to ghost for a player. The companion viewer is
    /// <c>age2_x1\spectate.exe</c> ("Conquerors Spectator"), which ships with the game.
    ///
    /// Launching it with the host address as its single argument makes it connect straight
    /// away - no clicking - and it writes that address back to the "Spectate IP" registry
    /// value. Without an argument it only pre-fills the box from that value and waits for
    /// the user to press Spectate.
    ///
    /// Everyone here is on the same WireGuard subnet, so the host address is simply the
    /// other player's VPN IP and no port forwarding or UPnP is involved.
    /// </summary>
    public static class GameSpectator
    {
        /// <summary>TCP port the game itself listens on while a match is in progress.</summary>
        public const int SpectatorPort = 53754;

        /// <summary>Name of the inbound rule that lets other players reach this host's game.</summary>
        private const string SpectatorFirewallRuleName = "TadaPlay spectator";

        private const string SpectatorExeRelativePath = @"age2_x1\spectate.exe";

        /// <summary>
        /// The executable spectate.exe starts once the stream arrives. It is hardcoded - the
        /// fully resolved path "...\age2_x1\age2_x1.5.exe" is visible in the viewer's memory,
        /// and there is no setting or argument that changes it.
        ///
        /// That is why spectating "does not work with the WololoKingdoms version": the player
        /// runs age2-WK.exe (or the centre variant), but the viewer always starts stock
        /// age2_x1.5.exe. Those two are the same UserPatch 1.5 binary differing in only ~5 KB
        /// of patches, so pointing this name at the exe the player actually uses gives the
        /// spectator the same game they play.
        /// </summary>
        private const string SpectatorLaunchTargetName = "age2_x1.5.exe";

        /// <summary>Suffix for the stock exe we move aside while a spectator session runs.</summary>
        private const string BackupSuffix = ".tada-original";

        private static readonly object SwapLock = new();
        private static int _activeSessions;

        private const string GameRegistryKey =
            @"Software\Microsoft\Microsoft Games\Age of Empires II: The Conquerors Expansion\1.0";

        /// <summary>Full path of the bundled spectator viewer, or null if it isn't there.</summary>
        public static string FindSpectatorExe(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder)) return null;
            string path = Path.Combine(gameFolder, SpectatorExeRelativePath);
            return File.Exists(path) ? path : null;
        }

        // --- Server-side spectator stream defaults --------------------------------------
        // spectate.exe's "Server stream settings" persist under the game key, so filling them
        // in by hand every game is only necessary because nothing writes them first. These are
        // the values a working install shows in that dialog. Units, verified against a live
        // install: "Spec Join Delay" is milliseconds (5000 = the 5s the dialog shows), "Max
        // Connections" is a plain count, and "Spec Late Join" is the raw unit the game stores
        // for the "Late join limit" box (19200 is what it holds for the 10 shown there) - kept
        // as captured rather than converted, so it round-trips to exactly what the dialog had.
        private const int DefaultMaxConnections = 32;
        private const int DefaultJoinDelayMs = 5000;    // "Join delay time (seconds)" = 5

        // "Late join limit" is how far into a match a spectator may still join. The game stores
        // it in 1/32-second units (verified: 19200 = the 10 minutes the dialog showed), i.e.
        // 32*60 = 1920 units per minute. Ten minutes is far too short for the "click a player
        // who is already in a game and watch" flow - a 4v4 routinely runs well past it, and a
        // spectator arriving after the limit is refused with "Disconnected from host". So the
        // default is a long window that effectively means "join any time during the match".
        private const int LateJoinUnitsPerMinute = 1920;
        private const int DefaultLateJoinMinutes = 180;
        private const int DefaultLateJoinLimit = DefaultLateJoinMinutes * LateJoinUnitsPerMinute;

        /// <summary>
        /// Writes sensible defaults for the game's own spectator stream so the "Spectator
        /// Stream" dialog never has to be configured by hand - as a host (Allow Spectators, max
        /// connections, join delay, late-join limit) or between sessions.
        ///
        /// Idempotent: each value is written only when it differs, so it neither fights a manual
        /// change unnecessarily nor rewrites on every call. Returns true if anything changed.
        /// </summary>
        public static bool EnsureSpectatorStreamDefaults()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(GameRegistryKey);
                if (key == null) return false;

                bool changed = false;
                changed |= SetDwordIfDifferent(key, "Spec Default", 1); // Allow Spectators ticked
                changed |= SetDwordIfDifferent(key, "Max Connections", DefaultMaxConnections);
                changed |= SetDwordIfDifferent(key, "Spec Join Delay", DefaultJoinDelayMs);
                changed |= SetDwordIfDifferent(key, "Spec Late Join", DefaultLateJoinLimit);

                if (changed)
                {
                    DebugLogger.Info("GameSpectator: applied default spectator stream settings " +
                                     $"(Spec Default=1, Max Connections={DefaultMaxConnections}, " +
                                     $"Spec Join Delay={DefaultJoinDelayMs}ms, " +
                                     $"Spec Late Join={DefaultLateJoinLimit}).");
                }
                return changed;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameSpectator: cannot apply spectator stream defaults: {ex.Message}");
                return false;
            }
        }

        /// <summary>Writes a DWORD only when it is missing or different; reports whether it changed.</summary>
        private static bool SetDwordIfDifferent(RegistryKey key, string name, int value)
        {
            try
            {
                object current = key.GetValue(name);
                if (current is int existing && existing == value) return false;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameSpectator: cannot read '{name}': {ex.Message}");
            }
            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }

        /// <summary>
        /// Opens inbound TCP <see cref="SpectatorPort"/> - the game's OWN spectator port - so
        /// other players can actually reach this host's live game.
        ///
        /// UserPatch listens on it for the whole match, but Windows Firewall blocks the inbound
        /// connection by default, and that block looks exactly like "spectating is broken" from
        /// the viewer's side: spectate.exe connects, is refused, gets nothing, and closes after
        /// a couple of seconds. TadaPlay already opens the match-share port (53755); this is its
        /// live-spectator counterpart and was the missing half. Idempotent, and non-fatal on
        /// failure - a host whose firewall already permits the game is reachable regardless.
        /// </summary>
        public static void EnsureSpectatorPortOpen()
        {
            try
            {
                if (RunNetsh($"advfirewall firewall show rule name=\"{SpectatorFirewallRuleName}\"") == 0)
                {
                    return; // already present
                }

                int code = RunNetsh($"advfirewall firewall add rule name=\"{SpectatorFirewallRuleName}\" " +
                                    $"dir=in action=allow protocol=TCP localport={SpectatorPort} " +
                                    "profile=any description=\"Cho phep nguoi choi khac xem tran truc tiep\"");
                if (code == 0)
                {
                    DebugLogger.Info($"GameSpectator: added firewall rule for spectator TCP {SpectatorPort}.");
                }
                else
                {
                    DebugLogger.Warn($"GameSpectator: netsh returned {code} adding the spectator " +
                                     "firewall rule; other players may not be able to watch this host.");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameSpectator: cannot open the spectator port: {ex.Message}");
            }
        }

        private static int RunNetsh(string arguments)
        {
            var startInfo = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process process = Process.Start(startInfo);
            if (process == null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return process.HasExited ? process.ExitCode : -1;
        }

        public enum LaunchStatus { Success, NotConfigured, ViewerMissing, LaunchFailed }

        /// <summary>
        /// Opens the spectator viewer already connected to <paramref name="hostIp"/>.
        ///
        /// The host is not probed first, deliberately. The game announces spectators to the
        /// host in chat, so probing would be a visible side effect - and a probe cannot tell
        /// "not in a game" apart from "has not allowed spectators" anyway. Launching straight
        /// away is also more useful: the viewer sits on "Waiting for game..." and connects by
        /// itself once the host starts, so you can queue up to watch before a match begins.
        /// </summary>
        private static string LaunchTargetPath(string gameFolder) =>
            Path.Combine(gameFolder, "age2_x1", SpectatorLaunchTargetName);

        /// <summary>
        /// Points <see cref="SpectatorLaunchTargetName"/> at <paramref name="playExePath"/> so
        /// the spectator gets the same build the player uses. Returns true if a swap happened.
        /// </summary>
        private static bool SwapInPlayExe(string gameFolder, string playExePath)
        {
            if (string.IsNullOrWhiteSpace(playExePath) || !File.Exists(playExePath)) return false;

            string target = LaunchTargetPath(gameFolder);
            if (!File.Exists(target)) return false;
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(playExePath),
                              StringComparison.OrdinalIgnoreCase))
            {
                return false; // already the exe the player uses
            }

            try
            {
                string backup = target + BackupSuffix;
                if (!File.Exists(backup)) File.Copy(target, backup);
                File.Copy(playExePath, target, overwrite: true);
                DebugLogger.Info($"GameSpectator: pointed '{SpectatorLaunchTargetName}' at " +
                                 $"'{Path.GetFileName(playExePath)}' for this spectator session.");
                return true;
            }
            catch (Exception ex)
            {
                // Most likely the game is running from that exe and holds it open. Spectating
                // still works, just with whatever build is already in place.
                DebugLogger.Warn($"GameSpectator: cannot swap in '{playExePath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Puts the stock executable back. Safe to call at any time - including at startup, to
        /// clean up after a crash that skipped the restore.
        /// </summary>
        public static void RestoreLaunchTarget(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder)) return;
            lock (SwapLock)
            {
                if (_activeSessions > 0) return; // another viewer is still open
                string target = LaunchTargetPath(gameFolder);
                string backup = target + BackupSuffix;
                if (!File.Exists(backup)) return;
                try
                {
                    File.Copy(backup, target, overwrite: true);
                    File.Delete(backup);
                    DebugLogger.Info($"GameSpectator: restored the stock '{SpectatorLaunchTargetName}'.");
                }
                catch (Exception ex)
                {
                    DebugLogger.Warn($"GameSpectator: cannot restore '{target}': {ex.Message}");
                }
            }
        }

        /// <param name="playExePath">
        /// The build the player actually launches (age2-WK.exe / age2-WK-center.exe). Passed so
        /// the spectator sees the same game - see <see cref="SpectatorLaunchTargetName"/>.
        /// </param>
        public static (LaunchStatus Status, string Message) Spectate(string gameFolder, string hostIp,
                                                                     string playExePath = null)
        {
            if (string.IsNullOrWhiteSpace(hostIp))
            {
                return (LaunchStatus.NotConfigured, "Chưa chọn người chơi để xem.");
            }

            string viewer = FindSpectatorExe(gameFolder);
            if (viewer == null)
            {
                return (LaunchStatus.ViewerMissing,
                    "Không tìm thấy 'age2_x1\\spectate.exe' trong thư mục game. " +
                    "Kiểm tra lại thư mục game trong Cài đặt.");
            }

            try
            {
                // The viewer stores the last address itself, but write it first so the box is
                // still correct if the user closes and reopens it without an argument.
                RememberLastHost(hostIp);

                bool swapped;
                lock (SwapLock)
                {
                    swapped = SwapInPlayExe(gameFolder, playExePath);
                    if (swapped) _activeSessions++;
                }

                var startInfo = new ProcessStartInfo(viewer)
                {
                    Arguments = hostIp,          // single argument = connect immediately
                    UseShellExecute = true,
                    WorkingDirectory = gameFolder // the game refers to it as age2_x1\spectate.exe
                };
                Process process = Process.Start(startInfo);

                if (process != null && swapped)
                {
                    // Put the stock exe back when the viewer closes. If TadaPlay dies first the
                    // backup survives and is cleaned up on next startup.
                    process.EnableRaisingEvents = true;
                    process.Exited += (s, e) =>
                    {
                        lock (SwapLock) { _activeSessions = Math.Max(0, _activeSessions - 1); }
                        RestoreLaunchTarget(gameFolder);
                        process.Dispose();
                    };
                }
                else
                {
                    process?.Dispose();
                }

                DebugLogger.Info($"GameSpectator: launched '{viewer}' for host {hostIp}.");
                return (LaunchStatus.Success,
                    $"Đã mở trình xem trực tiếp cho {hostIp}. Nếu người đó chưa vào trận, " +
                    "cửa sổ sẽ chờ và tự kết nối khi trận bắt đầu.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameSpectator: cannot launch viewer for {hostIp}: {ex.Message}");
                return (LaunchStatus.LaunchFailed, $"Không mở được trình xem trực tiếp: {ex.Message}");
            }
        }

        /// <summary>
        /// Points the game's install entries at the folder actually being used.
        ///
        /// The spectator viewer reports "Could not locate game expansion." when it cannot
        /// resolve the installation, and it looks that up under
        /// ...\Microsoft Games\Age of Empires II: The Conquerors Expansion\1.0 - captured from
        /// a failing run, which referenced that key under REGISTRY\USER\&lt;SID&gt;, i.e.
        /// HKEY_CURRENT_USER.
        ///
        /// On a machine that has held several copies of AoE2 these entries drift badly. Where
        /// this was diagnosed, HKCU had the key but no install path at all, the HKLM 32-bit
        /// view pointed at an unrelated older copy, and the 64-bit view and CDPath pointed at
        /// folders that no longer existed - so nothing named the folder being played.
        ///
        /// Launching the game by absolute path (what TadaPlay does) does not consult any of
        /// this, which is exactly why playing works while spectating cannot find the game.
        ///
        /// All three locations are written: HKCU because the viewer reads it, and both HKLM
        /// views because the game is 32-bit (so reads WOW6432Node) while other tooling -
        /// Voobly's launch.dll among it - reads the native one.
        /// </summary>
        /// <returns>True if anything was corrected.</returns>
        public static bool EnsureGameRegistered(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder)) return false;

            string exeDir = Path.Combine(gameFolder, "age2_x1");
            if (!Directory.Exists(exeDir)) return false;
            string value = exeDir + Path.DirectorySeparatorChar;   // the game stores it with a trailing slash

            bool changed = false;
            foreach ((RegistryHive hive, RegistryView view) in new[]
                     {
                         (RegistryHive.CurrentUser, RegistryView.Default),
                         (RegistryHive.LocalMachine, RegistryView.Registry32),
                         (RegistryHive.LocalMachine, RegistryView.Registry64),
                     })
            {
                try
                {
                    using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey key = root.CreateSubKey(GameRegistryKey);
                    if (key == null) continue;

                    foreach (string name in new[] { "EXE Path", "CDPath" })
                    {
                        string current = key.GetValue(name) as string;
                        if (string.Equals(current?.TrimEnd(Path.DirectorySeparatorChar),
                                          exeDir, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        key.SetValue(name, value, RegistryValueKind.String);
                        DebugLogger.Info($"GameSpectator: {hive}/{view} '{name}': " +
                                         $"'{current ?? "<absent>"}' -> '{value}'.");
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    // HKLM needs administrator; TadaPlay's manifest requests it, so this is unexpected.
                    DebugLogger.Warn($"GameSpectator: cannot register the install in {hive}/{view}: {ex.Message}");
                }
            }
            return changed;
        }

        /// <summary>Last host address the viewer connected to, as the game stores it.</summary>
        public static string GetLastHost()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(GameRegistryKey);
                return key?.GetValue("Spectate IP") as string;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameSpectator: cannot read 'Spectate IP': {ex.Message}");
                return null;
            }
        }

        private static void RememberLastHost(string hostIp)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(GameRegistryKey);
                key?.SetValue("Spectate IP", hostIp, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"GameSpectator: cannot store 'Spectate IP': {ex.Message}");
            }
        }

    }
}
