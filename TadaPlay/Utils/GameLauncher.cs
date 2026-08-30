using System;
using System.Diagnostics;
using System.IO;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Launches the AoE2 executable configured in Settings. Shared by the "Start Game"
    /// button (Home) and "Phát lại" (Matches), so both use the same configured exe.
    /// </summary>
    public static class GameLauncher
    {
        public enum LaunchStatus { Success, NotConfigured, FileMissing, LaunchFailed }

        /// <summary>
        /// Launches the game exe. When <paramref name="recordFilePath"/> is given, it's passed
        /// as the command-line argument - the same way Windows launches the game when you
        /// double-click a .mgz/.mgx file via its file association - so the game opens straight
        /// into that replay instead of the user having to find it in the in-game Replays list.
        ///
        /// <paramref name="exePath"/> is always a fresh copy dropped into age2_x1 by
        /// GameExecutablePreparer right before this call, never a permanent user-owned file - so
        /// once the game process exits, the copy is deleted again automatically.
        /// </summary>
        public static (LaunchStatus Status, string Message, int? Pid, DateTime? StartedUtc)
            Launch(string exePath, string recordFilePath = null)
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return (LaunchStatus.NotConfigured,
                    "Không tìm thấy file khởi chạy game. Kiểm tra lại thư mục game trong Cài đặt " +
                    "(phải chứa thư mục con age2_x1).", null, null);
            }
            if (!File.Exists(exePath))
            {
                return (LaunchStatus.FileMissing, $"Không tìm thấy file khởi chạy game: {exePath}", null, null);
            }

            try
            {
                var startInfo = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)
                };
                if (!string.IsNullOrWhiteSpace(recordFilePath))
                {
                    startInfo.Arguments = $"\"{recordFilePath}\"";
                }
                Process process = Process.Start(startInfo);

                // Identity of what we just started, so a caller that later wants to close THIS
                // game can tell it apart from any other copy the player has open. PID alone is
                // not enough - Windows hands numbers back out - so the start time comes too.
                int? pid = null;
                DateTime? startedUtc = null;
                if (process != null)
                {
                    try { pid = process.Id; startedUtc = process.StartTime.ToUniversalTime(); }
                    catch (Exception ex)
                    {
                        DebugLogger.Warn($"GameLauncher: cannot identify the launched game: {ex.Message}");
                    }
                }

                if (process != null)
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += (s, e) =>
                    {
                        // TEMPORARY: deleting the launcher copy on exit is disabled - the launched exe
                        // is now the game's own WK exe (GameExecutablePreparer copy is disabled), so it
                        // must NOT be deleted. Re-enable this together with the copy block.
                        // DeleteLauncherCopy(exePath);
                        process.Dispose();
                    };
                }

                string message = string.IsNullOrWhiteSpace(recordFilePath)
                    ? $"Đã mở '{Path.GetFileName(exePath)}'."
                    : $"Đã mở '{Path.GetFileName(exePath)}' để phát lại '{Path.GetFileName(recordFilePath)}'.";
                return (LaunchStatus.Success, message, pid, startedUtc);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameLauncher: failed to launch '{exePath}': {ex.Message}");
                return (LaunchStatus.LaunchFailed, $"Không thể mở game: {ex.Message}", null, null);
            }
        }

        private static void DeleteLauncherCopy(string exePath)
        {
            try
            {
                if (File.Exists(exePath))
                {
                    File.Delete(exePath);
                    DebugLogger.Info($"GameLauncher: deleted launcher copy '{exePath}' after game exit.");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameLauncher: failed to delete launcher copy '{exePath}': {ex.Message}");
            }
        }
    }
}
