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
        /// </summary>
        public static (LaunchStatus Status, string Message) Launch(string exePath, string recordFilePath = null)
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return (LaunchStatus.NotConfigured,
                    "Chưa cấu hình file khởi chạy game. Vào Cài đặt để chọn file (vd: age2_x1-WK.exe).");
            }
            if (!File.Exists(exePath))
            {
                return (LaunchStatus.FileMissing, $"Không tìm thấy file khởi chạy game: {exePath}");
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
                Process.Start(startInfo);

                string message = string.IsNullOrWhiteSpace(recordFilePath)
                    ? $"Đã mở '{Path.GetFileName(exePath)}'."
                    : $"Đã mở '{Path.GetFileName(exePath)}' để phát lại '{Path.GetFileName(recordFilePath)}'.";
                return (LaunchStatus.Success, message);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"GameLauncher: failed to launch '{exePath}': {ex.Message}");
                return (LaunchStatus.LaunchFailed, $"Không thể mở game: {ex.Message}");
            }
        }
    }
}
