using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TadaPlay.Logger;

namespace TadaPlay.Services
{
    /// <summary>
    /// Downloads and installs a newer client.
    ///
    /// Until this existed, "update" meant telling the player to visit a web page and re-run an
    /// installer by hand. That made every breaking change a rollout problem: the version gate
    /// could refuse an old build but not fix it, and a server-side change that invalidated old
    /// clients had to wait on people noticing a message.
    ///
    /// The flow is deliberately boring: ask the server what the newest build is, download it to
    /// a temp file, check it hashes to what the server said, run the installer silently with
    /// /RELAUNCH, and exit so the installer can replace files that are currently in use. The app
    /// comes back on its own.
    /// </summary>
    public sealed class UpdateService
    {
        private const string UpdateEndpoint = "https://openvpn.aoe2.io.vn/api.php?action=client_update";

        // Generous: this is a ~55 MB download over a Vietnamese residential link, sometimes
        // while a game is running.
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

        public sealed class UpdateInfo
        {
            [JsonProperty("latest_version")] public string LatestVersion { get; set; }
            [JsonProperty("min_version")] public string MinVersion { get; set; }
            [JsonProperty("download_url")] public string DownloadUrl { get; set; }
            [JsonProperty("sha256")] public string Sha256 { get; set; }
        }

        /// <summary>
        /// This build's version with any "+&lt;git sha&gt;" suffix stripped, e.g. "3.28.4".
        /// </summary>
        public static string CurrentVersion
        {
            get
            {
                string v = (Application.ProductVersion ?? string.Empty).Trim();
                int plus = v.IndexOf('+');
                return plus >= 0 ? v.Substring(0, plus) : v;
            }
        }

        /// <summary>
        /// Compares two dotted versions numerically. A plain string compare is wrong here and
        /// quietly so: "3.9.0" sorts after "3.28.0" lexically, which would tell a player on an
        /// old build that they are up to date.
        /// </summary>
        public static bool IsNewer(string candidate, string current)
        {
            int[] Parse(string v) => (v ?? string.Empty)
                .Split('+')[0].Split('-')[0].Split('.')
                .Select(part => int.TryParse(part.Trim(), out int n) ? n : -1)
                .ToArray();

            int[] a = Parse(candidate), b = Parse(current);
            if (a.Length == 0 || a.Any(n => n < 0)) return false;   // unparseable: never offer it
            if (b.Length == 0 || b.Any(n => n < 0)) return true;    // unknown current: offer

            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        /// <summary>
        /// Asks the server what the newest build is. Returns null when the server cannot be
        /// reached or says nothing useful - the caller then simply carries on, because failing
        /// to check for an update must never stop the app starting.
        /// </summary>
        public static async Task<UpdateInfo> CheckAsync(CancellationToken token = default)
        {
            try
            {
                using var http = new HttpClient { Timeout = CheckTimeout };
                http.DefaultRequestHeaders.Add("X-Client-Version", CurrentVersion);
                string json = await http.GetStringAsync(UpdateEndpoint);
                var info = JsonConvert.DeserializeObject<UpdateInfo>(json);

                if (info == null || string.IsNullOrWhiteSpace(info.LatestVersion)
                                 || string.IsNullOrWhiteSpace(info.DownloadUrl))
                {
                    return null;
                }
                return info;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"UpdateService: update check failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>True when the server's newest build is ahead of this one.</summary>
        public static bool UpdateAvailable(UpdateInfo info) =>
            info != null && IsNewer(info.LatestVersion, CurrentVersion);

        /// <summary>
        /// Downloads the installer and verifies it. Returns the path, or null with the reason
        /// reported through <paramref name="onProgress"/>.
        ///
        /// The hash check is the point at which this stops being "run whatever arrived". Without
        /// it the app would elevate and execute a 55 MB binary purely because a URL returned
        /// 200 - a captive portal or a corrupted transfer would be enough. When the server
        /// publishes no hash the download still proceeds, but that is a deliberate, logged
        /// decision rather than an absent check.
        /// </summary>
        public static async Task<string> DownloadAsync(UpdateInfo info,
                                                       Action<string> onProgress = null,
                                                       CancellationToken token = default)
        {
            string target = Path.Combine(Path.GetTempPath(),
                                         $"TadaPlay-Setup-{info.LatestVersion}.exe");
            try
            {
                onProgress?.Invoke($"Đang tải bản {info.LatestVersion}...");

                using (var http = new HttpClient { Timeout = DownloadTimeout })
                using (var response = await http.GetAsync(info.DownloadUrl,
                                                          HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();

                    // Straight to a temp file rather than into memory: this is ~55 MB, and the
                    // installer has to exist on disk to be run anyway.
                    using var source = await response.Content.ReadAsStreamAsync();
                    using var file = new FileStream(target, FileMode.Create, FileAccess.Write,
                                                    FileShare.None, 81920, useAsync: true);
                    await source.CopyToAsync(file, 81920, token);
                }

                if (!string.IsNullOrWhiteSpace(info.Sha256))
                {
                    string actual = HashFile(target);
                    if (!string.Equals(actual, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        DebugLogger.Error($"UpdateService: checksum mismatch. expected={info.Sha256} actual={actual}");
                        onProgress?.Invoke("Tệp cài đặt tải về không hợp lệ - đã huỷ cập nhật.");
                        TryDelete(target);
                        return null;
                    }
                    DebugLogger.Info("UpdateService: installer checksum verified.");
                }
                else
                {
                    DebugLogger.Warn("UpdateService: server published no sha256 - installing without verification.");
                }

                return target;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"UpdateService: download failed: {ex.Message}");
                onProgress?.Invoke($"Không tải được bản cập nhật: {ex.Message}");
                TryDelete(target);
                return null;
            }
        }

        /// <summary>
        /// Runs the downloaded installer and returns true if it started.
        ///
        /// The caller must exit immediately afterwards. The installer overwrites files this
        /// process is running from, so staying alive would make it fail on a locked file - and
        /// /RELAUNCH is what brings the app back once that is done.
        /// </summary>
        public static bool StartInstaller(string installerPath)
        {
            try
            {
                var startInfo = new ProcessStartInfo(installerPath)
                {
                    // /VERYSILENT: no wizard, the app already asked. /NORESTART: never reboot a
                    // player's machine. /RELAUNCH: our own flag, see the .iss [Code] section.
                    Arguments = "/VERYSILENT /NORESTART /RELAUNCH",
                    UseShellExecute = true,
                };
                Process.Start(startInfo);
                DebugLogger.Info("UpdateService: installer started; exiting so it can replace files in use.");
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"UpdateService: could not start the installer: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes installers left in %TEMP% by previous updates.
        ///
        /// The updater cannot tidy up after itself: it has to exit so the installer can replace
        /// files this process holds open, which means the ~55 MB download is still there when it
        /// dies. Left alone, every update a player ever takes adds another copy. The next start
        /// is the first moment it is safe to remove, so it happens here.
        ///
        /// The file currently being installed may still be locked if this runs while the
        /// installer is finishing; a failed delete is ignored and retried next start.
        /// </summary>
        public static void CleanupDownloadedInstallers()
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(Path.GetTempPath(), "TadaPlay-Setup-*.exe"))
                {
                    try
                    {
                        File.Delete(path);
                        DebugLogger.Info($"UpdateService: removed leftover installer {Path.GetFileName(path)}.");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Info($"UpdateService: leftover installer still in use, will retry next start ({ex.GetType().Name}).");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"UpdateService: could not scan temp for old installers: {ex.Message}");
            }
        }

        private static string HashFile(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { DebugLogger.Warn($"UpdateService: could not delete {path}: {ex.Message}"); }
        }
    }
}
