using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Plays the short alert used when someone comes online.
    ///
    /// Uses MCI (winmm) rather than System.Media.SoundPlayer because SoundPlayer only handles
    /// WAV, and the sound is an MP3. MCI is part of Windows, so this needs no NuGet package and
    /// nothing extra to ship.
    ///
    /// The file travels as a manifest resource, following the convention documented in
    /// TadaPlay.csproj: .resx System.Byte[] entries throw MissingMethodException in the
    /// self-contained Release publish, which is exactly the build players run. MCI needs a real
    /// path, so it is written to temp once per run and reused.
    /// </summary>
    public static class NotificationSound
    {
        private const string ResourceName = "TadaPlay.Resources.yahoo_knock.mp3";

        // Named handle for the open MCI device. Reused so repeated alerts cannot leak devices -
        // a lobby of thirty people coming online would otherwise open thirty of them.
        private const string Alias = "tadaplay_online";

        private static readonly object Gate = new();
        private static string _extractedPath;
        private static bool _extractFailed;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder returnValue,
                                                int returnLength, IntPtr callback);

        /// <summary>
        /// Plays the alert. Never throws: a notification sound failing is not worth interrupting
        /// anything for, and on a machine with no audio device it would fail every time.
        /// </summary>
        public static void PlayUserOnline()
        {
            try
            {
                string path = EnsureExtracted();
                if (path == null) return;

                lock (Gate)
                {
                    // Close first: playing again while the previous alert is still open would
                    // otherwise fail with "device already open", so two people arriving in quick
                    // succession would silence the second.
                    Send($"close {Alias}");

                    // "mpegvideo" is the MCI driver that reads MP3.
                    if (Send($"open \"{path}\" type mpegvideo alias {Alias}") != 0)
                    {
                        DebugLogger.Warn("NotificationSound: MCI could not open the alert sound.");
                        return;
                    }
                    Send($"play {Alias} from 0");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"NotificationSound: could not play the alert: {ex.Message}");
            }
        }

        private static int Send(string command) => mciSendString(command, null, 0, IntPtr.Zero);

        /// <summary>
        /// Writes the embedded sound to temp once. Returns null if that is not possible, after
        /// which it is not retried - a machine that cannot write here will not start being able
        /// to mid-session, and retrying on every notification would just log noise.
        /// </summary>
        private static string EnsureExtracted()
        {
            lock (Gate)
            {
                if (_extractedPath != null && File.Exists(_extractedPath)) return _extractedPath;
                if (_extractFailed) return null;

                try
                {
                    string target = Path.Combine(Path.GetTempPath(), "tadaplay-online.mp3");
                    using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                    {
                        if (source == null)
                        {
                            DebugLogger.Warn($"NotificationSound: resource {ResourceName} is missing from the build.");
                            _extractFailed = true;
                            return null;
                        }

                        using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
                        source.CopyTo(file);
                    }

                    _extractedPath = target;
                    return _extractedPath;
                }
                catch (Exception ex)
                {
                    DebugLogger.Warn($"NotificationSound: could not write the alert sound to temp: {ex.Message}");
                    _extractFailed = true;
                    return null;
                }
            }
        }
    }
}
