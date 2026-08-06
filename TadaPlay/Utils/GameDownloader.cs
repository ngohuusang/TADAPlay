using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    public enum DownloadPhase { Downloading, Extracting, Done }

    /// <summary>Progress for the download+install flow. <see cref="Percent"/> is 0..100, or -1 when
    /// the total isn't known (server sent no Content-Length).</summary>
    public class DownloadProgress
    {
        public DownloadPhase Phase;
        public int Percent;
        public string Detail;
    }

    /// <summary>
    /// Downloads the bundled AoE2 game archive, extracts it, and locates the game root (the folder
    /// that contains <c>age2_x1</c>) so the app can point its game folder at it and be ready to launch.
    /// </summary>
    public static class GameDownloader
    {
        // http://aoe2.io.vn/... 301-redirects to https; use the https URL directly to skip the hop.
        public const string DownloadUrl = "https://aoe2.io.vn/games/AOE_2.zip";

        // Default install location: "\tada_games" on the same drive the app runs from.
        public static string DefaultTargetFolder()
        {
            string root = Path.GetPathRoot(System.AppContext.BaseDirectory);
            if (string.IsNullOrEmpty(root)) root = @"C:\";
            return Path.Combine(root, "tada_games");
        }

        /// <summary>
        /// Downloads the archive into <paramref name="targetFolder"/>, extracts it there, deletes the
        /// archive, and returns the detected game root (folder containing <c>age2_x1</c>). Throws on
        /// failure; honours <paramref name="ct"/> for cancellation.
        /// </summary>
        public static async Task<string> DownloadAndInstallAsync(
            string targetFolder, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(targetFolder);
            string zipPath = Path.Combine(targetFolder, "AOE_2.download.zip");

            try
            {
                await DownloadFileAsync(DownloadUrl, zipPath, progress, ct);
                ExtractZip(zipPath, targetFolder, progress, ct);

                string gameRoot = FindGameRoot(targetFolder)
                    ?? throw new InvalidOperationException(
                        "Đã giải nén nhưng không tìm thấy thư mục game (không có 'age2_x1').");

                progress?.Report(new DownloadProgress { Phase = DownloadPhase.Done, Percent = 100, Detail = "Hoàn tất." });
                DebugLogger.Info($"GameDownloader: installed game at '{gameRoot}'.");
                return gameRoot;
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); }
                catch (Exception ex) { DebugLogger.Warn($"GameDownloader: could not delete temp zip: {ex.Message}"); }
            }
        }

        private static async Task DownloadFileAsync(
            string url, string destPath, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            using var source = await response.Content.ReadAsStreamAsync(ct);
            using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);

            var buffer = new byte[1 << 20]; // 1 MB
            long readTotal = 0;
            int read;
            int lastPercent = -2;
            var lastReport = DateTime.MinValue;

            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;

                int percent = total.HasValue && total.Value > 0 ? (int)(readTotal * 100 / total.Value) : -1;
                // Throttle UI updates: on each whole-percent change, and at most a few times a second.
                if (percent != lastPercent || (DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                {
                    lastPercent = percent;
                    lastReport = DateTime.UtcNow;
                    progress?.Report(new DownloadProgress
                    {
                        Phase = DownloadPhase.Downloading,
                        Percent = percent,
                        Detail = total.HasValue
                            ? $"Đang tải: {FormatBytes(readTotal)} / {FormatBytes(total.Value)}"
                            : $"Đang tải: {FormatBytes(readTotal)}",
                    });
                }
            }
        }

        private static void ExtractZip(
            string zipPath, string targetFolder, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            int total = archive.Entries.Count;
            int done = 0;
            string destRoot = Path.GetFullPath(targetFolder);

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                string destPath = Path.GetFullPath(Path.Combine(destRoot, entry.FullName));

                // Guard against zip-slip (entries escaping the target folder via ..\).
                if (!destPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                {
                    DebugLogger.Warn($"GameDownloader: skipping unsafe zip entry '{entry.FullName}'.");
                    continue;
                }

                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\") || string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                    entry.ExtractToFile(destPath, overwrite: true);
                }

                done++;
                if (done % 25 == 0 || done == total)
                {
                    progress?.Report(new DownloadProgress
                    {
                        Phase = DownloadPhase.Extracting,
                        Percent = total > 0 ? (int)((long)done * 100 / total) : -1,
                        Detail = $"Đang giải nén: {done} / {total} tệp",
                    });
                }
            }
        }

        // The archive may extract with an arbitrary top-level folder, so find the game root by the
        // presence of an "age2_x1" directory rather than assuming a fixed layout.
        private static string FindGameRoot(string targetFolder)
        {
            if (Directory.Exists(Path.Combine(targetFolder, "age2_x1"))) return targetFolder;
            try
            {
                return Directory.EnumerateDirectories(targetFolder, "age2_x1", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameDownloader: searching for age2_x1 failed: {ex.Message}");
                return null;
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int u = 0;
            while (value >= 1024 && u < units.Length - 1) { value /= 1024; u++; }
            return $"{value:0.0} {units[u]}";
        }
    }
}
