using System;
using System.IO;
using System.Threading;
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
    /// real time, injects the viewer's Ctrl+Left "slower" control one step per tick until the
    /// replay is back to normal speed (50).
    ///
    /// It is a closed loop on purpose. Injected keys reach the game only intermittently, so a
    /// one-shot "set the speed to 50" run (floor to 0, then two steps back up) turned dropped
    /// taps into a speed INCREASE and measurably flapped between 50 and 100. Stepping down once
    /// per tick can only ever slow the replay, needs no knowledge of the current speed, and stops
    /// by itself as soon as the playhead is back to ~1x - so it never runs on down into pause.
    ///
    /// The trigger is "how many REAL seconds before the playhead would catch the live edge",
    /// which needs two measurements rather than one: how fast the playhead consumes the record,
    /// and how fast the HOST is appending to it. The host plays in real time, so its append rate
    /// IS 1x, and the gap only closes at the difference between the two. Fast-forwarded playback
    /// was measured at 15-30x the host's rate, so a window expressed in game time collapsed to
    /// about three real seconds - far too little to get a keypress in.
    ///
    /// The playhead is read from the game's file-read position, which runs a little ahead of what
    /// is on screen, so it triggers slightly early rather than too late.
    ///
    /// Keys are only injected while the GAME is the foreground window, so a press never lands in
    /// TadaPlay or anywhere else if the viewer has alt-tabbed away.
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

        private readonly string _replayPath;
        private readonly Action<string> _log;

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
        private long _prevPos;
        private long _prevTotal;
        private DateTime _prevTickUtc = DateTime.MinValue;

        public PlayheadGovernor(string replayPath, Action<string> log = null)
        {
            _replayPath = replayPath;
            _log = log ?? (_ => { });
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

                if (!GameInput.IsGameForeground()) return;

                // Key injection needs elevation (UIPI): a Medium-integrity TadaPlay cannot send
                // input to the game. Warn once so this failure mode is obvious rather than silent.
                if (!GameInput.IsElevated() && !_warnedElevation)
                {
                    _warnedElevation = true;
                    _log("[Xem] ⚠️ TadaPlay không chạy quyền Admin nên KHÔNG điều khiển được tốc độ " +
                         "replay. Hãy chạy TadaPlay bằng Administrator để tính năng ghìm tốc độ hoạt động.");
                }

                // A burst holds keys for a few hundred ms, longer than one tick, so it runs
                // off-thread; ticks during a burst, and within the pacing gap after it, are
                // skipped. The pacing matters: presses fired as fast as the tick allows stopped
                // registering with the game altogether.
                if (_injecting || DateTime.UtcNow - _lastInjectUtc < InjectEvery) return;
                _injecting = true;
                _lastInjectUtc = DateTime.UtcNow;
                _attempts++;
                System.Threading.Tasks.Task.Run(() =>
                {
                    // One deterministic call sets the speed to exactly 50 (floor to 0, then two
                    // steps up), so there is nothing to converge on and no way to overshoot into
                    // a pause - see GameInput.SetReplaySpeedNormal.
                    try { GameInput.SetReplaySpeedNormal(); }
                    finally
                    {
                        // Forget the pre-press rate. The fast-attack average would otherwise keep
                        // reporting the old fast-forward speed for several ticks after the replay
                        // had already slowed, and each of those ticks would press again - which is
                        // how a single overshoot turns into a pause.
                        _playheadRate = 0;
                        _gainingTicks = 0;
                        _injecting = false;
                    }
                });
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
