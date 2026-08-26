using System;

namespace TadaPlay.Utils
{
    /// <summary>
    /// What this client can currently tell other players about its match.
    ///
    /// A viewer needs to distinguish three situations that otherwise all look like "nothing
    /// to watch": no match at all, a match that has just started but has not been captured
    /// yet, and a match that is ready to watch. The middle one is a countdown rather than a
    /// failure, so it is reported as one.
    ///
    /// Written by the record watcher and the capture timer, read by the share server on a
    /// request thread, so every field is volatile-by-lock rather than a plain field.
    /// </summary>
    public static class MatchShareState
    {
        private static readonly object Gate = new();

        private static bool _inGame;
        private static DateTime? _startedUtc;
        private static DateTime? _nextCaptureUtc;
        private static string _recordPath;
        private static long _durationMs;
        private static DateTime? _durationAtUtc;

        /// <summary>
        /// How often an in-progress match is captured. Home overwrites this from
        /// LiveShareIntervalMs at startup; the default only matters if it never gets that far.
        /// </summary>
        public static TimeSpan CaptureInterval { get; set; } = TimeSpan.FromSeconds(90);

        /// <summary>True while a match is being played on this machine.</summary>
        public static bool InGame
        {
            get { lock (Gate) return _inGame; }
        }

        /// <summary>When the current match was first seen, or null if none.</summary>
        public static DateTime? StartedUtc
        {
            get { lock (Gate) return _startedUtc; }
        }

        /// <summary>
        /// Seconds until the next capture makes (more of) the match watchable. 0 when a match
        /// is already available or no match is running.
        /// </summary>
        public static int WaitSeconds
        {
            get
            {
                lock (Gate)
                {
                    if (!_inGame || _nextCaptureUtc == null) return 0;
                    double seconds = (_nextCaptureUtc.Value - DateTime.UtcNow).TotalSeconds;
                    return seconds <= 0 ? 0 : (int)Math.Ceiling(seconds);
                }
            }
        }

        /// <summary>
        /// The record the running match is being written to, or null when no match is running.
        ///
        /// This is what stops the previous match being offered as though it were the current
        /// one. A player who finishes a game and starts another still has the old match
        /// published, so "is anything shareable?" answered from the snapshot folder alone says
        /// yes from the moment the new game starts - and a viewer would be handed last game's
        /// record, watch it for a minute and a half, then be thrown off when the first real
        /// capture replaced it. While a match is running, only THIS match may be served.
        /// </summary>
        public static string CurrentRecordPath
        {
            get { lock (Gate) return _recordPath; }
        }

        /// <summary>
        /// Game time contained in the latest capture, in milliseconds - the match clock a
        /// viewer is told about. Comes from the record itself (summed sync increments) rather
        /// than from wall-clock time since the match started, so a paused or slow game reports
        /// what actually happened in it.
        /// </summary>
        public static long DurationMs
        {
            get { lock (Gate) return _durationMs; }
            set { lock (Gate) { _durationMs = value; _durationAtUtc = DateTime.UtcNow; } }
        }

        /// <summary>
        /// How old <see cref="DurationMs"/> is, in milliseconds - the wall-clock time since the
        /// capture it came from.
        ///
        /// A match clock only moves when a capture is taken: every 10 seconds while somebody is
        /// watching, every 90 when nobody is. Handed over on its own, the number therefore reads
        /// as the match being up to a minute and a half earlier than it is, and a viewer whose
        /// replay is running normally watches their own game clock overtake it - which cannot
        /// happen, since they are behind the host by construction.
        ///
        /// Sent alongside the clock so the far end can add the elapsed time back on. Timing the
        /// gap here rather than sending a timestamp keeps two machines' clocks out of it.
        /// </summary>
        public static long DurationAgeMs
        {
            get
            {
                lock (Gate)
                {
                    if (_durationAtUtc == null) return 0;
                    double ms = (DateTime.UtcNow - _durationAtUtc.Value).TotalMilliseconds;
                    return ms <= 0 ? 0 : (long)ms;
                }
            }
        }

        /// <summary>Called when the watcher first sees a match being recorded.</summary>
        public static void MatchStarted(string recordPath = null)
        {
            lock (Gate)
            {
                _inGame = true;
                _startedUtc = DateTime.UtcNow;
                _nextCaptureUtc = DateTime.UtcNow + CaptureInterval;
                _recordPath = recordPath;
                _durationMs = 0;
                _durationAtUtc = null;
            }
        }

        /// <summary>Called after each capture, successful or not, to restart the countdown.</summary>
        public static void Captured()
        {
            lock (Gate)
            {
                _nextCaptureUtc = DateTime.UtcNow + CaptureInterval;
            }
        }

        /// <summary>Called when the match ends; the published copy stays watchable.</summary>
        public static void MatchEnded()
        {
            lock (Gate)
            {
                _inGame = false;
                _startedUtc = null;
                _nextCaptureUtc = null;
                // Cleared last: from here the finished match is served from the snapshot
                // folder like any other, which is what PublishFinished is about to write.
                _recordPath = null;
            }
        }
    }
}
