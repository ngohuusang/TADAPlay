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
        private static DateTime? _clockLastAdvancedUtc;
        private static DateTime? _pausedSinceUtc;

        /// <summary>
        /// How long the match clock must stand still before it counts as a pause.
        ///
        /// Measured as a stall duration rather than as the gap between two consecutive samples,
        /// because the clock is now reported from two places at very different rates: the
        /// capture loop (10s while watched, 90s idle) and SpectatorStreamSource (every 3s while
        /// streaming). A per-gap rule silently never fires on the 3s path, since no single gap
        /// ever reaches the threshold. Accumulating the stall works at any sampling rate.
        /// </summary>
        private const int PauseAfterStallMs = 8000;

        /// <summary>
        /// Game time advancing by less than this across such a gap counts as not advancing.
        /// Not zero: the clock is recovered from the record's sync increments, and the last
        /// partial operation can shift the total slightly between captures even mid-play.
        /// </summary>
        private const int PauseGameToleranceMs = 1000;

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
        }

        /// <summary>
        /// True while the host's game appears to be paused.
        ///
        /// Derived rather than observed: the match clock above comes from the record's own sync
        /// increments, so it only advances when the game simulates. If it stands still while
        /// wall-clock time moves on, the game is not simulating - which is what a pause is. No
        /// memory reading, no screen scraping, no extra I/O; it reuses a number every capture
        /// already computes.
        ///
        /// The honest caveat: this is only as responsive as the capture interval. With a viewer
        /// watching, captures are 10s apart, so a pause shows up within roughly 10-20s. With
        /// nobody watching it is 90s - but then nobody is looking at the overlay either.
        ///
        /// A total network/game stall would also read as paused. That is arguably correct for
        /// the overlay's purpose: either way the match is not progressing and the viewer's
        /// replay is about to catch up to nothing.
        /// </summary>
        public static bool Paused
        {
            get { lock (Gate) return _pausedSinceUtc.HasValue; }
        }

        /// <summary>How long the game has been paused, or null when it is running.</summary>
        public static TimeSpan? PausedFor
        {
            get
            {
                lock (Gate)
                {
                    return _pausedSinceUtc.HasValue ? DateTime.UtcNow - _pausedSinceUtc.Value : null;
                }
            }
        }

        /// <summary>
        /// Records the match clock read from the latest capture, and re-evaluates whether the
        /// game is paused by comparing it against the previous sample.
        ///
        /// This replaced a plain DurationMs setter: the pause verdict has to be computed at the
        /// moment a new sample arrives, because it depends on the gap since the previous one.
        /// </summary>
        public static void ReportDuration(long durationMs)
        {
            lock (Gate)
            {
                DateTime now = DateTime.UtcNow;

                if (_clockLastAdvancedUtc == null)
                {
                    // First sample of the match: nothing to compare against yet.
                    _clockLastAdvancedUtc = now;
                }
                else if (durationMs - _durationMs > PauseGameToleranceMs)
                {
                    // The game moved: definitively not paused, whatever we thought before.
                    _clockLastAdvancedUtc = now;
                    _pausedSinceUtc = null;
                }
                else if ((now - _clockLastAdvancedUtc.Value).TotalMilliseconds >= PauseAfterStallMs
                         && _pausedSinceUtc == null)
                {
                    // Dated to when the clock was last seen moving, not to now, so "paused for"
                    // does not under-report by however long the detection took.
                    _pausedSinceUtc = _clockLastAdvancedUtc;
                }

                _durationMs = durationMs;
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
                _clockLastAdvancedUtc = null;
                _pausedSinceUtc = null;
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
                // A finished match is not a paused one - without this the overlay would keep
                // showing "tạm dừng" over a game that simply ended.
                _clockLastAdvancedUtc = null;
                _pausedSinceUtc = null;
                // Cleared last: from here the finished match is served from the snapshot
                // folder like any other, which is what PublishFinished is about to write.
                _recordPath = null;
            }
        }
    }
}
