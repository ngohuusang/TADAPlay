using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Services.Interface;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Downloads a stored match and opens the game on it.
    ///
    /// Lifted out of Matches so the ranking detail can replay a match too. Two copies of this
    /// would have drifted: the folder fallback, the file-name prefix and the launcher thread are
    /// each here for a specific reason, and a second copy would have kept the shape while losing
    /// the reasons.
    /// </summary>
    public static class ReplayLauncher
    {
        public sealed class Result
        {
            public bool Downloaded { get; init; }
            public bool Launched { get; init; }
            public string Message { get; init; }
            public string Path { get; init; }
        }

        /// <summary>
        /// Fetches record <paramref name="recordId"/> into the game's SaveGame folder and starts
        /// the game on it. <paramref name="report"/> is called with progress suitable for a status
        /// line. Never throws; failures come back on the Result.
        /// </summary>
        public static async Task<Result> ReplayAsync(IAccountService accounts, IAppContext appContext,
                                                     long recordId, string originalFileName,
                                                     IWin32Window owner, Action<string> report = null)
        {
            if (accounts == null || appContext == null)
            {
                return new Result { Message = "Không thể phát lại: thiếu dịch vụ." };
            }

            // The game only browses records from its own SaveGame folder - a file dropped
            // anywhere else does not appear in its Replays list even if the game is launched
            // pointing straight at it.
            string saveDir = RecordedGameFinder.FindSaveGameDirectory(appContext.GetGameFolder());
            if (string.IsNullOrEmpty(saveDir))
            {
                using var picker = new FolderBrowserDialog
                {
                    Description = "Không tìm thấy thư mục SaveGame của game. Chọn nơi lưu file record để phát lại."
                };
                if (owner == null ? picker.ShowDialog() != DialogResult.OK
                                  : picker.ShowDialog(owner) != DialogResult.OK)
                {
                    return new Result { Message = "Đã huỷ." };
                }
                saveDir = picker.SelectedPath;
            }

            // The prefix is what stops the record watcher treating this download as a match this
            // player just finished and uploading it back under their name.
            string baseName = string.IsNullOrWhiteSpace(originalFileName)
                ? $"match_{recordId}.mgz"
                : Path.GetFileName(originalFileName);
            string destPath = Path.Combine(saveDir, RecordedGameFinder.ReplayPrefix + baseName);

            try
            {
                report?.Invoke($"Đang tải record của trận #{recordId}...");
                await accounts.DownloadRecordAsync(recordId, destPath);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"ReplayLauncher: download of record {recordId} failed: {ex.Message}");
                return new Result { Message = $"Không tải được record: {ex.Message}" };
            }

            try
            {
                // A dedicated LongRunning thread, not the shared pool: preparing and starting the
                // launcher is synchronous blocking I/O (antivirus scanning a freshly written exe,
                // ShellExecute reputation checks) and it was starving the WebSocket ping timer,
                // which is also pool-scheduled, badly enough to spike reported ping.
                var (status, message, _, _) = await Task.Factory.StartNew(() =>
                {
                    string exePath = GameExecutablePreparer.PrepareAndGetExePath(
                        appContext.GetGameFolder(), appContext.GetGameLaunchMode());
                    return GameLauncher.Launch(exePath, destPath);
                }, System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                report?.Invoke(message);
                return new Result
                {
                    Downloaded = true,
                    Launched = status == GameLauncher.LaunchStatus.Success,
                    Message = message,
                    Path = destPath,
                };
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"ReplayLauncher: launch failed for record {recordId}: {ex.Message}");
                return new Result { Downloaded = true, Message = ex.Message, Path = destPath };
            }
        }
    }
}
