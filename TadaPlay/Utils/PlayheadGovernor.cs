using System;
using System.IO;
using System.Threading;
using TadaPlay.Connections;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Keeps a spectator's replay in step with the match it is watching.
    ///
    /// The viewer watches a growing .mgz. Two things can go wrong, and this handles exactly those
    /// two - both driven by what the HOST reports about itself, never by inference from the file:
    ///
    /// - The host pauses. Their record stops growing, so a viewer who keeps playing eats the
    ///   remaining bytes and hits the end of a match that is not over. The replay is paused and
    ///   released when the host plays on.
    /// - The viewer fast-forwards and catches up. Once playback is within
    ///   <see cref="CatchUpWithin"/> of the host's own match clock there is nothing left to watch,
    ///   so the speed goes back to normal.
    ///
    /// An earlier version also policed the playhead's distance from end-of-file using measured
    /// byte rates, and re-pressed whenever that estimate said the replay was gaining. That is
    /// gone. It fired on a noisy estimate rather than on a fact, and every firing ran the full
    /// floor-and-raise sequence, so the viewer saw the playback speed ramping up and down for no
    /// reason they could see. The catch-up rule above covers the same danger by comparing two
    /// clocks directly, which is both cheaper to understand and impossible to get wrong by 30x.
    ///
    /// Pausing uses the game's real pause key, not a speed floor - see
    /// <see cref="GameInput.PressPauseKey"/>. The binding is read from the player's own .hki
    /// (hotkey command 19323, F3 by default) so a rebound key still works.
    ///
    /// Nothing here trusts that a keypress landed. Injection is global and the viewer can take
    /// the foreground away mid-sequence, so every action is proved afterwards by watching the
    /// playhead - frozen after a pause, moving after a resume - and retried or escalated if the
    /// proof fails. That matters most for the resume: nothing else in the app ever presses
    /// "faster", so a resume that silently failed would leave the replay stopped for the rest of
    /// the match.
    /// </summary>
    public sealed class PlayheadGovernor : IDisposable
    {
        private const int TickMs = 600;

        /// <summary>Hotkey command id for "Pause Game" in AoE2's string table.</summary>
        private const int PauseGameCommandId = 19323;

        /// <summary>
        /// How close playback may get to the host's match clock before the speed is put back to
        /// normal. Generous on purpose: the playhead is the file-READ position, which the game has
        /// buffered but not yet drawn, so this triggers a little earlier than it reads.
        /// </summary>
        private static readonly TimeSpan CatchUpWithin = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How far playback must fall back behind the host before "caught up" can fire again.
        /// Well clear of <see cref="CatchUpWithin"/> so a viewer sitting near the live edge - the
        /// normal state after catching up - does not get pressed at every tick.
        /// </summary>
        private static readonly TimeSpan CatchUpRelease = TimeSpan.FromSeconds(90);

        /// <summary>Timing the playhead costs a read of everything played so far, so it is throttled.</summary>
        private static readonly TimeSpan MeasurePlaybackEvery = TimeSpan.FromMilliseconds(1500);

        /// <summary>Minimum gap between injected sequences. Presses fired as fast as the tick
        /// allowed stopped registering with the game altogether.</summary>
        private static readonly TimeSpan InjectEvery = TimeSpan.FromMilliseconds(2000);

        /// <summary>How long to wait before judging whether an action took.</summary>
        private static readonly TimeSpan ProofAfter = TimeSpan.FromSeconds(6);

        /// <summary>
        /// Playhead movement that still counts as "held". Not zero: the game reads ahead of what
        /// it draws, so the read position keeps creeping for a moment after playback stops.
        /// </summary>
        private const long HoldSlackBytes = 16384;

        /// <summary>Playhead movement that proves playback really restarted. At normal speed the
        /// record is consumed at roughly 790 B/s, so a running replay clears this easily.</summary>
        private const long MovingProofBytes = 1024;

        private const int MaxRetries = 2;

        private static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(15);

        /// <summary>How the replay is currently being held stopped.</summary>
        private enum HoldMethod { None, PauseKey, SpeedFloor }

        private readonly string _replayPath;
        private readonly string _gameFolder;
        private readonly Action<string> _log;
        /// <summary>The host's own broadcast status, or null when they are not reporting one.</summary>
        private readonly Func<LiveShareClient.HostStatus> _hostStatus;

        private System.Threading.Timer _timer;
        private ushort _pauseKey = GameInput.VkPauseDefault;

        private HoldMethod _hold = HoldMethod.None;
        private bool _warnedElevation;
        private bool _gaveUp;
        private volatile bool _injecting;
        private DateTime _lastInjectUtc = DateTime.MinValue;
        private DateTime _lastStatusUtc = DateTime.MinValue;
        private int _attempts;

        // The outstanding proof: what the playhead has to do, and from where.
        private DateTime _proofSentUtc = DateTime.MinValue;
        private long _proofPos;
        private int _proofRetries;
        private bool _proofExpectsMovement;

        private bool _caughtUp;
        private long _playbackMs;
        private DateTime _lastPlaybackUtc = DateTime.MinValue;

        public PlayheadGovernor(string replayPath, Action<string> log = null,
                                Func<LiveShareClient.HostStatus> hostStatus = null,
                                string gameFolder = null)
        {
            _replayPath = replayPath;
            _log = log ?? (_ => { });
            _hostStatus = hostStatus;
            _gameFolder = gameFolder;
        }

        public void Start()
        {
            if (string.IsNullOrWhiteSpace(_replayPath)) return;
            _pauseKey = ResolvePauseKey(_gameFolder);
            _timer = new System.Threading.Timer(_ => Tick(), null, TickMs, TickMs);
            DebugLogger.Info($"PlayheadGovernor: watching '{_replayPath}'. Pause key VK 0x{_pauseKey:X2}; " +
                             $"speed returns to normal within {CatchUpWithin.TotalSeconds:F0}s of the host's clock.");
        }

        /// <summary>
        /// The player's own "Pause Game" binding, read from the .hki the game actually loads.
        /// Falls back to the F3 default when there is no readable hotkey file.
        /// </summary>
        private static ushort ResolvePauseKey(string gameFolder)
        {
            if (string.IsNullOrWhiteSpace(gameFolder)) return GameInput.VkPauseDefault;
            try
            {
                string dataMods = Path.Combine(gameFolder, "Voobly Mods", "AOC", "Data Mods");
                if (!Directory.Exists(dataMods)) return GameInput.VkPauseDefault;

                foreach (string dir in Directory.EnumerateDirectories(dataMods, "*Game Data"))
                {
                    foreach (string file in Directory.EnumerateFiles(dir, "player*.hki", SearchOption.TopDirectoryOnly))
                    {
                        HotkeyFile hk = HotkeyFile.Load(file);
                        foreach (HotkeyGroup group in hk.Groups)
                        {
                            foreach (HotkeyBinding b in group.Bindings)
                            {
                                if (b.StringId == PauseGameCommandId && b.KeyCode > 0)
                                {
                                    DebugLogger.Info($"PlayheadGovernor: pause key from '{Path.GetFileName(file)}' " +
                                                     $"= VK 0x{b.KeyCode:X2}.");
                                    return (ushort)b.KeyCode;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"PlayheadGovernor: could not read the pause binding: {ex.Message}");
            }
            return GameInput.VkPauseDefault;
        }

        private void Tick()
        {
            try
            {
                if (!LiveRecordReader.TryGetReplayReadPosition(out long pos, out long total, out string path))
                    return; // no game reading a record right now

                // Only govern OUR spectator replay, never another recorded game the player opened.
                if (!string.IsNullOrEmpty(path) && !SamePath(path, _replayPath)) return;

                LiveShareClient.HostStatus host = ReadHostStatus();

                // Paused counts only while the host is still IN a match. A finished match has a
                // stopped clock too, and holding the viewer's replay for that would strand them
                // just before the ending they are trying to watch.
                bool hostPaused = host != null && host.InGame && host.Paused;

                ReportStatus(host, pos, total, hostPaused);

                if (hostPaused)
                {
                    if (_hold == HoldMethod.None) BeginHold(pos);
                    else ProveHold(pos);
                    return;
                }

                if (_hold != HoldMethod.None)
                {
                    BeginRelease(pos);
                    return;
                }

                // Still settling a resume: leave the speed alone until the playhead proves it moved.
                if (!ProveRelease(pos)) return;

                CheckCaughtUp(host, pos);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"PlayheadGovernor: tick failed: {ex.Message}");
            }
        }

        private void ReportStatus(LiveShareClient.HostStatus host, long pos, long total, bool hostPaused)
        {
            if (DateTime.UtcNow - _lastStatusUtc < StatusEvery) return;
            _lastStatusUtc = DateTime.UtcNow;

            string hostClock = host == null ? "no broadcast"
                             : host.GameMs > 0 ? $"{host.GameMs / 1000}s" + (hostPaused ? " PAUSED" : "")
                             : "no clock";
            string mine = _playbackMs > 0 ? $"{_playbackMs / 1000}s" : "unknown";
            DebugLogger.Info($"PlayheadGovernor: status - playback {mine}, host {hostClock}, " +
                             $"hold {_hold}, {total - pos} bytes behind the file end.");
        }

        /// <summary>
        /// The host's status, or null if it cannot be had.
        ///
        /// Deliberately whatever the caller can answer cheaply and synchronously - in practice a
        /// lookup in the lobby's already-broadcast user list. This runs on a 600ms timer, so it
        /// must not become a network probe to the host; a host on a build too old to broadcast
        /// simply yields null and nothing here acts at all.
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

        // ---------------------------------------------------------------- pause

        private void BeginHold(long pos)
        {
            if (_gaveUp) return;
            if (!TryInject(() => GameInput.PressPauseKey(_pauseKey))) return;

            _hold = HoldMethod.PauseKey;
            ArmProof(pos, expectsMovement: false);
            _log("[Xem] ⏸ Chủ nhà đã TẠM DỪNG - đã tự động dừng replay của bạn.");
            DebugLogger.Info($"PlayheadGovernor: host paused - pressed the pause key (VK 0x{_pauseKey:X2}).");
        }

        private void ProveHold(long pos)
        {
            if (_proofSentUtc == DateTime.MinValue) return;                  // already proved
            if (_injecting || DateTime.UtcNow - _proofSentUtc < ProofAfter) return;

            if (pos - _proofPos <= HoldSlackBytes)
            {
                DebugLogger.Info($"PlayheadGovernor: hold confirmed - playhead moved only {pos - _proofPos} bytes.");
                _proofSentUtc = DateTime.MinValue;
                return;
            }

            // Still eating the record, so the replay is not actually paused.
            if (_hold == HoldMethod.PauseKey)
            {
                // NOT another pause-key press: it is a toggle, and if the first press did land
                // late, a second would resume the replay. Escalate to the speed floor instead,
                // which is idempotent and known to stop playback.
                DebugLogger.Warn($"PlayheadGovernor: the pause key did not stop the replay " +
                                 $"({pos - _proofPos} bytes consumed) - falling back to speed 0.");
                _hold = HoldMethod.SpeedFloor;
                _proofRetries = 0;
                if (TryInject(GameInput.FloorReplaySpeed)) ArmProof(pos, expectsMovement: false);
                return;
            }

            if (_proofRetries >= MaxRetries)
            {
                _proofSentUtc = DateTime.MinValue;
                _gaveUp = true;
                _log("[Xem] ⚠️ Không tự dừng được replay - hãy tự tạm dừng trong game để không xem vượt.");
                DebugLogger.Warn("PlayheadGovernor: gave up holding the replay.");
                return;
            }

            _proofRetries++;
            if (TryInject(GameInput.FloorReplaySpeed)) ArmProof(pos, expectsMovement: false);
        }

        // ---------------------------------------------------------------- resume

        private void BeginRelease(long pos)
        {
            HoldMethod held = _hold;
            Action press = held == HoldMethod.PauseKey
                ? () => GameInput.PressPauseKey(_pauseKey)   // toggle back off
                : GameInput.SetReplaySpeedNormal;            // floored: raise to 50

            if (!TryInject(press)) return;

            _hold = HoldMethod.None;
            _gaveUp = false;
            ArmProof(pos, expectsMovement: true);
            _log("[Xem] ▶ Chủ nhà đã chơi tiếp - đã cho replay chạy lại.");
            DebugLogger.Info($"PlayheadGovernor: host resumed - released via {held}.");
        }

        /// <summary>
        /// Confirms a resume actually restarted playback. Returns true when there is nothing
        /// outstanding, so the caller may go on to consider the speed.
        /// </summary>
        private bool ProveRelease(long pos)
        {
            if (_proofSentUtc == DateTime.MinValue || !_proofExpectsMovement) return true;
            if (_injecting || DateTime.UtcNow - _proofSentUtc < ProofAfter) return false;

            if (pos - _proofPos >= MovingProofBytes)
            {
                DebugLogger.Info($"PlayheadGovernor: resume confirmed - playhead moved {pos - _proofPos} bytes.");
                _proofSentUtc = DateTime.MinValue;
                return true;
            }

            if (_proofRetries >= MaxRetries)
            {
                _proofSentUtc = DateTime.MinValue;
                _log("[Xem] ⚠️ Replay vẫn đang dừng - hãy nhấn phím tạm dừng trong game để xem tiếp.");
                DebugLogger.Warn("PlayheadGovernor: gave up resuming the replay - it is still frozen.");
                return true;
            }

            _proofRetries++;
            // First retry repeats the pause key (it is still stopped, so toggling is the right
            // direction). The second raises the speed instead, in case it was the floor holding it.
            Action press = _proofRetries == 1
                ? (Action)(() => GameInput.PressPauseKey(_pauseKey))
                : GameInput.SetReplaySpeedNormal;
            DebugLogger.Warn($"PlayheadGovernor: resume did not take (playhead moved {pos - _proofPos} " +
                             $"bytes) - retry {_proofRetries}.");
            if (TryInject(press)) ArmProof(pos, expectsMovement: true, keepRetries: true);
            return false;
        }

        // ---------------------------------------------------------------- caught up

        private void CheckCaughtUp(LiveShareClient.HostStatus host, long pos)
        {
            if (host == null || !host.InGame || host.GameMs <= 0) return;

            if (DateTime.UtcNow - _lastPlaybackUtc >= MeasurePlaybackEvery)
            {
                _lastPlaybackUtc = DateTime.UtcNow;
                if (LiveRecordReader.TryGetPlaybackDurationMs(_replayPath, pos, out long ms)) _playbackMs = ms;
            }
            if (_playbackMs <= 0) return;

            long behindMs = host.GameMs - _playbackMs;

            if (behindMs > CatchUpRelease.TotalMilliseconds)
            {
                _caughtUp = false;   // genuinely behind again - re-arm
                return;
            }
            if (_caughtUp || behindMs > CatchUpWithin.TotalMilliseconds) return;

            if (!TryInject(GameInput.SetReplaySpeedNormal)) return;
            _caughtUp = true;
            _log($"[Xem] ⏩ Đã gần đuổi kịp chủ nhà (còn {Math.Max(0, behindMs) / 1000}s) - " +
                 "đã hạ tốc độ về bình thường.");
            DebugLogger.Info($"PlayheadGovernor: caught up - playback {_playbackMs}ms vs host {host.GameMs}ms " +
                             $"({behindMs}ms behind); speed back to normal.");
        }

        // ---------------------------------------------------------------- plumbing

        private void ArmProof(long pos, bool expectsMovement, bool keepRetries = false)
        {
            _proofSentUtc = DateTime.UtcNow;
            _proofPos = pos;
            _proofExpectsMovement = expectsMovement;
            if (!keepRetries) _proofRetries = 0;
        }

        /// <summary>
        /// Runs one key sequence off the timer thread, respecting the pacing gap and the
        /// foreground rule. Returns false when the press was not made, in which case the caller
        /// simply tries again on a later tick.
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
                _log("[Xem] ⚠️ TadaPlay không chạy quyền Admin nên KHÔNG điều khiển được replay. " +
                     "Hãy chạy TadaPlay bằng Administrator.");
            }

            _injecting = true;
            _lastInjectUtc = DateTime.UtcNow;
            _attempts++;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { press(); }
                catch (Exception ex) { DebugLogger.Warn($"PlayheadGovernor: injection failed: {ex.Message}"); }
                finally { _injecting = false; }
            });
            return true;
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
