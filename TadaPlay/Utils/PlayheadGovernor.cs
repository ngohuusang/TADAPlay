using System;
using System.IO;
using System.Threading;
using TadaPlay.Connections;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Keeps a spectator's replay from running off its own end.
    ///
    /// The viewer watches a growing .mgz: fine at normal speed, but fast-forwarding races the
    /// playhead to the last byte, where AoE2 ends the replay even though the host is still
    /// playing. Nothing can make the game wait at end-of-file, so instead this watches how close
    /// the playhead is to the end and, while it is inside a safety window AND still outrunning
    /// real time, sets the replay back to normal speed (50) via
    /// <see cref="GameInput.SetReplaySpeedNormal"/>.
    ///
    /// That call is deterministic from any starting speed - it floors to 0, which is a hard
    /// stop, then steps up twice to 50 - so this does not need to know the current speed and has
    /// nothing to converge on. An earlier design stepped down one notch per tick and re-measured
    /// instead; it could not stop cleanly, because the measurement lags the change, so it kept
    /// pressing after the replay had already slowed and walked 75 - 50 - 25 - 0, leaving the
    /// replay paused. The rate average is reset after each attempt for the same reason.
    ///
    /// The trigger is "how many REAL seconds before the playhead would catch the live edge",
    /// which needs two measurements rather than one: how fast the playhead consumes the record,
    /// and how fast the HOST is appending to it. The host plays in real time, so its append rate
    /// IS 1x, and the gap only closes at the difference between the two. Fast-forwarded playback
    /// was measured at 15-30x the host's rate, so a window expressed in game time collapsed to
    /// about three real seconds - far too little to get a keypress in.
    ///
    /// Two further rules come from the HOST's own broadcast rather than from the file, and both
    /// are direct statements rather than estimates - which is why they are trusted ahead of the
    /// rate maths above:
    ///
    /// - The host paused, so their record has stopped growing. A viewer who keeps playing eats
    ///   the remaining bytes and hits the end of a match that is not over. The replay is stopped
    ///   too (speed 0) and released when the host resumes. This is the manual step the overlay
    ///   used to just ask the viewer to take.
    /// - The replay has PLAYED past the host's own match clock. There is nothing left to watch,
    ///   so any fast-forwarding is spending runway on data that does not exist yet, and the
    ///   speed goes back to normal.
    ///
    /// Both are latched. Acting once per crossing is the point: these fire while the condition
    /// holds, not while it changes, so an unlatched version would re-press every couple of
    /// seconds for as long as the host stayed paused.
    ///
    /// The playhead is read from the game's file-read position, which runs a little ahead of what
    /// is on screen, so it triggers slightly early rather than too late.
    ///
    /// Keys are only injected while the GAME is the foreground window, and that is re-checked
    /// before every individual press rather than once per attempt - SendInput is global, and the
    /// full sequence takes around 2.4 seconds, which is ample time to alt-tab into the middle of
    /// it.
    /// </summary>
    public sealed class PlayheadGovernor : IDisposable
    {
        private const int TickMs = 600;
        // How much REAL time we want in hand before the playhead would hit the end. This is the
        // control variable, and it has to be real seconds rather than game seconds: measured on a
        // live match, a fast-forwarded replay consumed the record at ~25,600 bytes/s against a 1x
        // rate of ~790 bytes/s, i.e. about 32x. A "120 seconds of game time" window is therefore
        // only ~3.6 REAL seconds at that speed - too little to press anything - while the same
        // window is a comfortable stretch at 2x. Working in real time adapts to whatever speed
        // the viewer actually chose.
        private const double ReactSeconds = 30;
        // How much faster than 1x the playhead must move to count as fast-forwarding.
        // Kept low on purpose: the playback steps are 0/25/50/75/99/100 with 50 normal, so speed
        // 75 only moves the playhead at 1.5x. At the old 1.8 threshold that read as "not fast
        // forwarding", so the governor stopped while the replay still outran the incoming data
        // and reached the end anyway. 1.25 catches 75 (1.5x) and 100 (2x) but not normal (1.0x).
        // Stop point. Normal speed (50) replays the record at exactly the rate the host writes
        // it, so a playhead running at ~1x the host's rate IS normal and must be left alone; the
        // next step up (75) runs at ~1.5x. Sitting the threshold between them is what makes the
        // loop settle on 50 instead of walking on down to 25 and 0.
        private const double FastForwardFactor = 1.35;
        // Minimum gap between two injected presses. Ticking fast keeps the measurement current,
        // but hammering the game with presses is what stopped them registering at all: ~1.6
        // presses a second produced 99 consecutive no-ops, while presses ~3s apart did slow the
        // replay. This paces the presses without slowing down the detection.
        private static readonly TimeSpan InjectEvery = TimeSpan.FromMilliseconds(2000);
        // The governor used to log only when it acted, which left every "why did it not act?"
        // question unanswerable. This is how often it reports what it is seeing instead.
        private static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(10);

        // How far the replay may play PAST the host's clock before the speed is put back to
        // normal. Slightly negative in effect - it acts just before the crossing - because the
        // playhead is the file-READ position, which the game has buffered but not yet shown.
        private static readonly TimeSpan CatchUpLead = TimeSpan.FromSeconds(5);
        // How far the replay must fall back behind the host before "caught up" can fire again.
        // Without a gap this would re-arm on the noise of a single measurement and press every
        // couple of seconds while the viewer sat at the live edge, which is the normal state.
        private static readonly TimeSpan CatchUpRelease = TimeSpan.FromSeconds(20);
        // Reading the file prefix to time the playhead costs a read of everything played so far,
        // so it is not done on every 600ms tick.
        private static readonly TimeSpan MeasurePlaybackEvery = TimeSpan.FromMilliseconds(1500);
        // How far the playhead may drift while supposedly held before the hold is treated as
        // having failed. Not zero: the game reads ahead of what it draws, so the read position
        // keeps moving briefly after playback actually stops.
        private const long HoldSlackBytes = 16384;
        // A resume is proved by the playhead MOVING again. The wait covers the 2.4s sequence plus
        // a few seconds of playback: at normal speed the record is consumed at roughly 790 B/s, so
        // a resumed replay clears the byte threshold comfortably, while one still sitting at speed
        // 0 cannot move at all.
        private static readonly TimeSpan ResumeProofAfter = TimeSpan.FromSeconds(6);
        private const long ResumeProofBytes = 1024;
        // Re-presses are bounded. A spurious retry is harmless - the sequence is idempotent and
        // lands on normal speed either way - but an unbounded loop would inject keys into the
        // viewer's game every few seconds forever if the playhead were unreadable for some other
        // reason.
        private const int MaxResumeRetries = 2;

        private readonly string _replayPath;
        private readonly Action<string> _log;
        /// <summary>The host's own broadcast status, or null when they are not reporting one.</summary>
        private readonly Func<LiveShareClient.HostStatus> _hostStatus;

        private System.Threading.Timer _timer;
        private double _appendRate;       // smoothed host write rate, bytes per second = 1x
        private double _playheadRate;     // playhead consumption, bytes per second
        private bool _pinning;
        private bool _warnedElevation;
        private volatile bool _injecting;
        private DateTime _lastInjectUtc = DateTime.MinValue;
        private DateTime _lastStatusUtc = DateTime.MinValue;
        private int _gainingTicks;
        private int _attempts;
        /// <summary>The replay was stopped BY US because the host paused - so we owe it a resume.</summary>
        private bool _pausedForHost;
        /// <summary>Playhead position when the hold was applied, to prove the hold actually took.</summary>
        private long _heldAtPos;
        /// <summary>A resume has been sent and is waiting to be proved by a moving playhead.</summary>
        private DateTime _resumeSentUtc = DateTime.MinValue;
        private long _resumeAtPos;
        private int _resumeRetries;
        /// <summary>Already normalised for having caught up; cleared once genuinely behind again.</summary>
        private bool _caughtUp;
        private long _playbackMs;
        private DateTime _lastPlaybackUtc = DateTime.MinValue;
        private long _prevPos;
        private long _prevTotal;
        private DateTime _prevTickUtc = DateTime.MinValue;

        public PlayheadGovernor(string replayPath, Action<string> log = null,
                                Func<LiveShareClient.HostStatus> hostStatus = null)
        {
            _replayPath = replayPath;
            _log = log ?? (_ => { });
            _hostStatus = hostStatus;
        }

        /// <summary>
        /// The host's status, or null if it cannot be had.
        ///
        /// Deliberately whatever the caller can answer cheaply and synchronously - in practice a
        /// lookup in the lobby's already-broadcast user list. This runs on a 600ms timer, so it
        /// must not become a network probe to the host; a host on a build too old to broadcast
        /// simply yields null, and the byte-runway protection below carries on alone.
        /// </summary>
        private LiveShareClient.HostStatus ReadHostStatus()
        {
            try { return _hostStatus?.Invoke(); }
            catch (Exception ex)
            {
                DebugLogger.Warn($"PlayheadGovernor: host status unavailable: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Mirrors the host's pause onto the viewer's replay. Returns true when the replay is
        /// being held stopped, so the caller skips the rate maths - the playhead is not moving
        /// and there is nothing to measure.
        /// </summary>
        private bool HandleHostPause(LiveShareClient.HostStatus host, long pos)
        {
            // Paused only counts while the host is still IN a match. A finished match has a
            // stopped clock too, and stopping the viewer's replay for that would strand them
            // just before the ending they are trying to watch.
            bool hostPaused = host != null && host.InGame && host.Paused;

            if (hostPaused && !_pausedForHost)
            {
                if (!TryInject(GameInput.PauseReplay)) return true;   // held off; still "paused" as far as we are concerned
                _pausedForHost = true;
                _heldAtPos = pos;
                _log("[Xem] Chủ nhà đã tạm dừng - đã tự động dừng replay của bạn để không xem vượt.");
                DebugLogger.Info("PlayheadGovernor: host paused - replay stopped (speed 0).");
                return true;
            }

            if (_pausedForHost)
            {
                if (hostPaused)
                {
                    // Dispatching the sequence is not the same as it landing: the viewer can take
                    // the foreground away part-way through, which aborts it at an unknown speed.
                    // A playhead that is still eating the record proves the hold did not take, and
                    // believing it had is the one failure that would let the replay run off the end
                    // during a pause - the exact thing this is here to prevent. Checked only
                    // between sequences, and against a threshold, because the game reads ahead of
                    // what it displays.
                    if (!_injecting && pos - _heldAtPos > HoldSlackBytes)
                    {
                        DebugLogger.Warn($"PlayheadGovernor: hold did not take - playhead moved " +
                                         $"{pos - _heldAtPos} bytes while the host was paused; re-pressing.");
                        if (TryInject(GameInput.PauseReplay)) _heldAtPos = pos;
                    }
                    return true;   // still paused - hold
                }
                if (!TryInject(GameInput.SetReplaySpeedNormal)) return true;
                _pausedForHost = false;
                // Arm the proof. Clearing _pausedForHost is a claim, not a fact, and this is the
                // more dangerous of the two directions: a resume that silently failed leaves the
                // replay stopped at speed 0 with nothing left to restart it, because neither the
                // catch-up nor the runway rule ever presses "faster". The viewer would sit in
                // front of a frozen replay for the rest of the match.
                _resumeSentUtc = DateTime.UtcNow;
                _resumeAtPos = pos;
                _resumeRetries = 0;
                _log("[Xem] Chủ nhà đã chơi tiếp - đã cho replay chạy lại ở tốc độ thường.");
                DebugLogger.Info("PlayheadGovernor: host resumed - replay back to normal speed.");
                return true;
            }

            return !VerifyResumeTook(pos);
        }

        /// <summary>
        /// Confirms a resume actually restarted the replay, re-pressing if it did not.
        /// Returns true once there is nothing left to prove.
        /// </summary>
        private bool VerifyResumeTook(long pos)
        {
            if (_resumeSentUtc == DateTime.MinValue) return true;   // nothing pending
            if (_injecting || DateTime.UtcNow - _resumeSentUtc < ResumeProofAfter) return false;

            if (pos - _resumeAtPos >= ResumeProofBytes)
            {
                DebugLogger.Info($"PlayheadGovernor: resume confirmed - playhead moved " +
                                 $"{pos - _resumeAtPos} bytes since the press.");
                _resumeSentUtc = DateTime.MinValue;
                return true;
            }

            if (_resumeRetries >= MaxResumeRetries)
            {
                _resumeSentUtc = DateTime.MinValue;
                _log("[Xem] ⚠️ Không cho replay chạy lại được - hãy nhấn Ctrl+Mũi tên phải trong game để tăng tốc độ trở lại.");
                DebugLogger.Warn("PlayheadGovernor: gave up resuming the replay after " +
                                 $"{_resumeRetries} retries - the playhead is still not moving.");
                return true;
            }

            _resumeRetries++;
            DebugLogger.Warn($"PlayheadGovernor: resume did not take (playhead moved only " +
                             $"{pos - _resumeAtPos} bytes) - re-pressing, attempt {_resumeRetries}.");
            if (TryInject(GameInput.SetReplaySpeedNormal))
            {
                _resumeSentUtc = DateTime.UtcNow;
                _resumeAtPos = pos;
            }
            return false;
        }

        /// <summary>
        /// Puts the replay back to normal speed once it has played past the host's own match
        /// clock. Returns true when it acted.
        /// </summary>
        private bool HandleCaughtUp(LiveShareClient.HostStatus host, long pos)
        {
            if (host == null || !host.InGame || host.GameMs <= 0) return false;

            // The measurement is throttled, and the cached value is reused between measurements -
            // it only moves in one direction, so a slightly stale one is never an over-estimate.
            if (DateTime.UtcNow - _lastPlaybackUtc >= MeasurePlaybackEvery)
            {
                _lastPlaybackUtc = DateTime.UtcNow;
                if (LiveRecordReader.TryGetPlaybackDurationMs(_replayPath, pos, out long ms)) _playbackMs = ms;
            }
            if (_playbackMs <= 0) return false;

            long behindMs = host.GameMs - _playbackMs;

            if (behindMs > CatchUpRelease.TotalMilliseconds)
            {
                _caughtUp = false;   // genuinely behind again - re-arm
                return false;
            }
            if (_caughtUp || behindMs > CatchUpLead.TotalMilliseconds) return false;

            if (!TryInject(GameInput.SetReplaySpeedNormal)) return false;
            _caughtUp = true;
            _log("[Xem] Đã đuổi kịp chủ nhà - đã hạ tốc độ về bình thường.");
            DebugLogger.Info($"PlayheadGovernor: caught up - playback {_playbackMs}ms vs host " +
                             $"{host.GameMs}ms ({behindMs}ms behind); speed back to normal.");
            return true;
        }

        /// <summary>
        /// Runs one key sequence off the timer thread, respecting the pacing gap and the
        /// foreground rule. Returns false when the press was not made.
        ///
        /// A sequence holds keys for a couple of seconds, far longer than one tick, so ticks
        /// during it - and within the pacing gap after it - are skipped. The pacing matters:
        /// presses fired as fast as the tick allowed stopped registering with the game at all.
        /// </summary>
        private bool TryInject(Action press)
        {
            if (_injecting || DateTime.UtcNow - _lastInjectUtc < InjectEvery) return false;
            if (!GameInput.IsGameForeground()) return false;

            // Key injection needs elevation (UIPI): a Medium-integrity TadaPlay cannot send input
            // to the game. Warn once so this failure mode is obvious rather than silent.
            if (!GameInput.IsElevated() && !_warnedElevation)
            {
                _warnedElevation = true;
                _log("[Xem] ⚠️ TadaPlay không chạy quyền Admin nên KHÔNG điều khiển được tốc độ " +
                     "replay. Hãy chạy TadaPlay bằng Administrator để tính năng ghìm tốc độ hoạt động.");
            }

            _injecting = true;
            _lastInjectUtc = DateTime.UtcNow;
            _attempts++;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { press(); }
                finally
                {
                    // Forget the pre-press rate. The fast-attack average would otherwise keep
                    // reporting the old fast-forward speed for several ticks after the replay had
                    // already slowed, and each of those ticks would press again - which is how a
                    // single overshoot turns into a pause.
                    _playheadRate = 0;
                    _gainingTicks = 0;
                    _injecting = false;
                }
            });
            return true;
        }

        public void Start()
        {
            if (string.IsNullOrWhiteSpace(_replayPath)) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, TickMs, TickMs);
            DebugLogger.Info($"PlayheadGovernor: watching '{_replayPath}', will floor speed within " +
                             $"{ReactSeconds}s (real time) of the end.");
        }

        private void Tick()
        {
            try
            {
                if (!LiveRecordReader.TryGetReplayReadPosition(out long pos, out long total, out string path))
                {
                    return; // no game reading a record right now
                }

                // Only govern OUR spectator replay, never another recorded game the player opened.
                if (!string.IsNullOrEmpty(path) && !SamePath(path, _replayPath)) return;

                // What the host SAYS beats what the file suggests, so it is consulted first.
                LiveShareClient.HostStatus host = ReadHostStatus();

                // A held replay has a frozen playhead. Letting the rate maths run over that would
                // poison the averages with zeroes and, worse, reset _prevTickUtc bookkeeping into
                // reporting a huge apparent jump on the tick after the resume.
                if (HandleHostPause(host, pos))
                {
                    _prevPos = pos; _prevTotal = total; _prevTickUtc = DateTime.UtcNow;
                    _playheadRate = 0; _gainingTicks = 0;
                    return;
                }

                if (HandleCaughtUp(host, pos)) return;

                double elapsedMs = _prevTickUtc == DateTime.MinValue ? 0 : (DateTime.UtcNow - _prevTickUtc).TotalMilliseconds;
                long posDelta = pos - _prevPos;
                long totalDelta = total - _prevTotal;
                bool first = _prevTickUtc == DateTime.MinValue;
                _prevPos = pos; _prevTotal = total; _prevTickUtc = DateTime.UtcNow;
                if (first || elapsedMs <= 0) return;

                long remainingBytes = total - pos;

                // 1x is the rate the HOST is writing at - it is playing in real time, so its
                // append rate is the definition of real time for this record. The record's
                // average bytes-per-second is NOT a usable reference: density varies several-fold
                // between quiet and busy stretches of a match, which both hid real fast-forwarding
                // and invented it where there was none.
                double appendNow = totalDelta / (elapsedMs / 1000.0);
                double playheadNow = posDelta / (elapsedMs / 1000.0);
                _appendRate = _appendRate <= 0 ? appendNow : (_appendRate * 0.7) + (appendNow * 0.3);

                // Fast attack, slow release. Pressing fast-forward multiplies the playhead rate
                // ~20x in one step; a symmetric average needed several ticks to believe it, and by
                // then the runway it was protecting had already been spent - measured going from
                // "5.1s left" to "0.0s left" in a single second. Rising rates are therefore taken
                // immediately and only falling ones are smoothed.
                _playheadRate = playheadNow > _playheadRate
                    ? playheadNow
                    : (_playheadRate * 0.6) + (playheadNow * 0.4);

                // The playhead only reaches the end if it gains on the live edge; what matters is
                // the rate the gap CLOSES at, not the playback rate on its own.
                double closingRate = _playheadRate - _appendRate;
                // Compared as a RATIO to the host's rate, not as an absolute gap: the ratio is
                // what identifies the speed setting (1x = 50, 1.5x = 75), so this stops at 50.
                // The absolute floor covers a host that has gone quiet, where any playback gains.
                bool gaining = _playheadRate > Math.Max(_appendRate * FastForwardFactor, 400);

                if (DateTime.UtcNow - _lastStatusUtc >= StatusEvery)
                {
                    _lastStatusUtc = DateTime.UtcNow;
                    string runway = gaining && closingRate > 0
                        ? $"{remainingBytes / closingRate:F1}s"
                        : "not gaining";
                    DebugLogger.Info($"PlayheadGovernor: status - playhead {_playheadRate:F0} B/s, " +
                                     $"host {_appendRate:F0} B/s ({_playheadRate / Math.Max(_appendRate, 1):F1}x), " +
                                     $"closing {closingRate:F0} B/s, {remainingBytes} bytes behind live, " +
                                     $"runway {runway}.");
                }

                if (!gaining)
                {
                    _gainingTicks = 0;
                    _pinning = false;   // keeping pace with the host - nothing to protect against
                    return;
                }

                // Two consecutive ticks before acting. A single tick is noisy enough to read as
                // fast-forwarding when the replay is already at normal speed, and one spurious
                // press is one step too far down.
                if (++_gainingTicks < 2) return;

                double secondsToEof = remainingBytes / closingRate;
                if (secondsToEof > ReactSeconds)
                {
                    _pinning = false;   // plenty of runway: fast-forward freely
                    return;
                }

                // One deterministic call sets the speed to exactly 50 (floor to 0, then two steps
                // up), so there is nothing to converge on and no way to overshoot into a pause -
                // see GameInput.SetReplaySpeedNormal.
                if (!TryInject(GameInput.SetReplaySpeedNormal)) return;
                if (!_pinning)
                {
                    _pinning = true;
                    _log("[Xem] Gần hết trận - đang hạ tốc độ về 50 (không tua nữa) để tránh kết thúc sớm.");
                }
                DebugLogger.Info($"PlayheadGovernor: step slower #{_attempts} - playhead " +
                                 $"{_playheadRate:F0} B/s vs host {_appendRate:F0} B/s " +
                                 $"({_playheadRate / Math.Max(_appendRate, 1):F1}x), closing {closingRate:F0} B/s, " +
                                 $"{remainingBytes} bytes = {secondsToEof:F1}s to the end.");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"PlayheadGovernor: tick failed: {ex.Message}");
            }
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        public void Dispose()
        {
            try { _timer?.Dispose(); _timer = null; }
            catch (Exception ex) { DebugLogger.Warn($"PlayheadGovernor: dispose failed: {ex.Message}"); }
        }
    }
}
