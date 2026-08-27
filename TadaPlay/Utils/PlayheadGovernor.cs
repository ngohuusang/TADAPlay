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
    /// playing. There is no way to make the game wait at end-of-file, so instead this watches
    /// how close the playhead is to the end and, once it comes within a safety window of it,
    /// presses "Slow Down Game" (numpad minus) enough to floor the replay back to normal speed
    /// (50, from 100 at full fast-forward) - so it simply cannot be sped up into the end.
    ///
    /// Two things are necessarily approximate and are tuned toward safety:
    /// - The playhead is read from the game's file-read position, which runs a little ahead of
    ///   what is on screen, so the slow-down triggers slightly early rather than too late.
    /// - "Within the safety window of the end" is estimated from the record's average bytes-per-second, since
    ///   converting a byte offset to an exact game-time on every tick would be too costly.
    ///
    /// Keys are only injected while the GAME is the foreground window, so a press never lands in
    /// TadaPlay or anywhere else if the viewer has alt-tabbed away.
    /// </summary>
    public sealed class PlayheadGovernor : IDisposable
    {
        private const int TickMs = 1500;
        private const int SafetySeconds = 120;         // start flooring within this of the end
        // How much faster than 1x the playhead must move to count as fast-forwarding. Tunable.
        private const double FastForwardFactor = 1.8;
        private static readonly TimeSpan ReanalyseEvery = TimeSpan.FromSeconds(6);

        private readonly string _replayPath;
        private readonly Action<string> _log;

        private System.Threading.Timer _timer;
        private DateTime _lastAnalyseUtc = DateTime.MinValue;
        private double _bodyBytesPerMs;   // record's average density, to turn the window into bytes
        private bool _pinning;
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
                             $"{SafetySeconds}s of the end.");
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

                RefreshRate();

                double elapsedMs = _prevTickUtc == DateTime.MinValue ? 0 : (DateTime.UtcNow - _prevTickUtc).TotalMilliseconds;
                long posDelta = pos - _prevPos;
                long totalDelta = total - _prevTotal;
                _prevPos = pos; _prevTotal = total; _prevTickUtc = DateTime.UtcNow;

                if (_bodyBytesPerMs <= 0 || elapsedMs <= 0) return;

                long remainingBytes = total - pos;
                double safetyBytes = _bodyBytesPerMs * SafetySeconds * 1000.0;
                if (remainingBytes > safetyBytes)
                {
                    _pinning = false;   // far from the end: fast-forward freely
                    return;
                }

                // Near the end. Only step in if the playhead is actually being fast-forwarded:
                // it is advancing markedly faster than 1x. The 1x reference is the larger of how
                // fast the file itself is growing (the host's real-time rate) and the record's
                // average density - so watching the tail at normal speed does NOT get pinned,
                // only genuine fast-forwarding does.
                double oneXBytes = Math.Max(totalDelta, _bodyBytesPerMs * elapsedMs);
                bool fastForwarding = posDelta > oneXBytes * FastForwardFactor;

                if (!fastForwarding)
                {
                    _pinning = false;   // at normal speed near the end - leave it alone (no jitter)
                    return;
                }

                if (!GameInput.IsGameForeground()) return;
                // Try both delivery paths - SendInput (works windowed/non-exclusive) and a
                // PostMessage straight to the game window (a chance for fullscreen-exclusive).
                GameInput.SetReplaySpeedNormal();
                GameInput.SetReplaySpeedNormalViaPost();
                if (!_pinning)
                {
                    _pinning = true;
                    _log("[Xem] Gần hết trận - đã đưa tốc độ về 50 (không tua nữa) để tránh kết thúc sớm.");
                }
                DebugLogger.Info($"PlayheadGovernor: fast-forward near EOF (posDelta {posDelta} > " +
                                 $"~{oneXBytes * FastForwardFactor:F0}, {remainingBytes} to end) -> set speed 50.");
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"PlayheadGovernor: tick failed: {ex.Message}");
            }
        }

        private void RefreshRate()
        {
            if (_bodyBytesPerMs > 0 && DateTime.UtcNow - _lastAnalyseUtc < ReanalyseEvery) return;
            _lastAnalyseUtc = DateTime.UtcNow;
            LiveRecordReader.RecordAnalysis a = LiveRecordReader.AnalyzeFile(_replayPath);
            if (a != null && a.DurationMs > 0 && a.BodyBytes > 0)
            {
                _bodyBytesPerMs = (double)a.BodyBytes / a.DurationMs;
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
