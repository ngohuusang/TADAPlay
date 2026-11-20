using System;
using System.IO;
using System.Threading;
using TadaPlay.Connections;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Makes a spectator's replay follow the match it is watching.
    ///
    /// Two rules, and deliberately nothing else. This is not a control loop and regulates
    /// nothing; it mirrors the host's reported state onto the viewer's replay and otherwise
    /// stays out of the way. Fast-forwarding is the viewer's to do by hand.
    ///
    /// The viewer watches a growing .mgz. Two things can go wrong, and this handles exactly those
    /// two - both driven by what the HOST reports about itself, never by inference from the file:
    ///
    /// - The host pauses. Their record stops growing, so a viewer who keeps playing eats the
    ///   remaining bytes and hits the end of a match that is not over. The replay is paused and
    ///   released when the host plays on.
    /// - The viewer fast-forwards and catches up. Once playback is within
    ///   <see cref="CatchUpWithin"/> of the end of what has been DOWNLOADED, the speed goes back
    ///   to normal.
    /// - The download runs dry. The host's record reaches the viewer in lumps, one per capture
    ///   (every 30 seconds), and a replay that reaches the end of its file ends - the viewer has
    ///   to start spectating all over again. So when less than <see cref="StarveBelow"/> of game
    ///   time is left in the file, the replay is held exactly like a host pause, and released
    ///   once <see cref="RefillTo"/> has arrived.
    ///
    /// Both of those compare the playhead with the viewer's OWN file - game time summed from the
    /// record, a fact rather than an estimate. They used to compare it with the host's match
    /// clock, which was fine while that clock was the clock of the latest capture. Since 3.37.6
    /// the host carries its clock forward between captures, so it runs up to a whole capture
    /// interval ahead of anything a viewer can have, and "30 seconds behind the host" could mean
    /// the replay was already at the end of its file.
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
    public sealed class ReplayFollower : IDisposable
    {
        private const int TickMs = 600;

        /// <summary>Hotkey command id for "Pause Game" in AoE2's string table.</summary>
        private const int PauseGameCommandId = 19323;

        /// <summary>
        /// How close playback may get to the host's match clock before the speed is put back to
        /// normal. Generous on purpose: the playhead is the file-READ position, which the game has
        /// buffered but not yet drawn, so this triggers a little earlier than it reads.
        /// </summary>
        private static readonly TimeSpan CatchUpWithin = TimeSpan.FromSeconds(75);

        /// <summary>
        /// How much downloaded game time must be ahead of playback again before "caught up" can
        /// fire again. Well clear of <see cref="CatchUpWithin"/> so a viewer sitting near the
        /// live edge - the normal state after catching up - does not get pressed at every tick.
        /// </summary>
        private static readonly TimeSpan CatchUpRelease = TimeSpan.FromSeconds(150);

        /// <summary>
        /// Game time left in the file below which the replay is held so it cannot reach the end.
        /// The playhead is the game's READ position, which runs a little ahead of what is drawn,
        /// so the real margin is a bit larger than this reads.
        /// </summary>
        private static readonly TimeSpan StarveBelow = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Game time that must be in hand again before a held-for-data replay is let go. About
        /// one capture's worth, so a release is not followed by another hold moments later.
        /// </summary>
        private static readonly TimeSpan RefillTo = TimeSpan.FromSeconds(40);

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
        /// <summary>Why the replay is held: the host paused, or the download ran low.</summary>
        private bool _holdForData;
        private bool _warnedElevation;
        private bool _gaveUp;
        private volatile bool _injecting;
        private DateTime _lastInjectUtc = DateTime.MinValue;
        private DateTime _lastStatusUtc = DateTime.MinValue;

        // The outstanding proof: what the playhead has to do, and from where.
        private DateTime _proofSentUtc = DateTime.MinValue;
        private long _proofPos;
        private int _proofRetries;
        private bool _proofExpectsMovement;

        private bool _caughtUp;
        private long _playbackMs;
        /// <summary>Game time in the whole downloaded file - the most the replay can show.</summary>
        private long _bufferedMs;
        private DateTime _lastPlaybackUtc = DateTime.MinValue;

        public ReplayFollower(string replayPath, Action<string> log = null,
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
            DebugLogger.Info($"ReplayFollower: watching '{_replayPath}'. Pause key VK 0x{_pauseKey:X2}; " +
                             $"speed returns to normal within {CatchUpWithin.TotalSeconds:F0}s of the downloaded " +
                             $"end; held below {StarveBelow.TotalSeconds:F0}s left until {RefillTo.TotalSeconds:F0}s arrive.");
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
                                    DebugLogger.Info($"ReplayFollower: pause key from '{Path.GetFileName(file)}' " +
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
                DebugLogger.Warn($"ReplayFollower: could not read the pause binding: {ex.Message}");
            }
            return GameInput.VkPauseDefault;
        }

        private void Tick()
        {
            try
            {
                // The file size is discarded: only the playhead matters now. How far it sat from
                // end-of-file was the runway rule's input, and that rule is gone.
                if (!LiveRecordReader.TryGetReplayReadPosition(out long pos, out _, out string path))
                    return; // no game reading a record right now

                // Only govern OUR spectator replay, never another recorded game the player opened.
                if (!string.IsNullOrEmpty(path) && !SamePath(path, _replayPath)) return;

                LiveShareClient.HostStatus host = ReadHostStatus();
                MeasureRunway(pos);

                // Both holds count only while the host is still IN a match. A finished match has
                // a stopped clock and a file that will not grow, and holding the viewer's replay
                // for either would strand them just before the ending they are trying to watch.
                bool hostInGame = host != null && host.InGame;
                bool hostPaused = hostInGame && host.Paused;
                bool starving = hostInGame && IsStarving();

                ReportStatus(host, hostPaused);

                if (hostPaused || starving)
                {
                    if (_hold == HoldMethod.None) BeginHold(pos, forData: !hostPaused);
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

                CheckCaughtUp(host);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"ReplayFollower: tick failed: {ex.Message}");
            }
        }

        private void ReportStatus(LiveShareClient.HostStatus host, bool hostPaused)
        {
            if (DateTime.UtcNow - _lastStatusUtc < StatusEvery) return;
            _lastStatusUtc = DateTime.UtcNow;

            string hostClock = host == null ? "no broadcast"
                             : host.GameMs > 0 ? $"{host.GameMs / 1000}s" + (hostPaused ? " PAUSED" : "")
                             : "no clock";
            string mine = _playbackMs > 0 ? $"{_playbackMs / 1000}s" : "unknown";
            string have = _bufferedMs > 0 ? $"{_bufferedMs / 1000}s" : "unknown";
            DebugLogger.Info($"ReplayFollower: status - playback {mine} of {have} downloaded, host {hostClock}, " +
                             $"hold {_hold}{(_holdForData ? " (waiting for data)" : "")}.");
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
                DebugLogger.Warn($"ReplayFollower: host status unavailable: {ex.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------- pause

        private void BeginHold(long pos, bool forData)
        {
            if (_gaveUp) return;
            if (!TryInject(() => GameInput.PressPauseKey(_pauseKey))) return;

            _hold = HoldMethod.PauseKey;
            _holdForData = forData;
            ArmProof(pos, expectsMovement: false);
            if (forData)
            {
                _log("[Xem] ⏸ Replay đã chạy tới phần mới nhất - tạm dừng chờ dữ liệu từ chủ nhà " +
                     "(sẽ tự chạy tiếp).");
                DebugLogger.Info($"ReplayFollower: only {(_bufferedMs - _playbackMs) / 1000}s left in the file " +
                                 $"(playback {_playbackMs}ms of {_bufferedMs}ms) - pressed the pause key.");
            }
            else
            {
                _log("[Xem] ⏸ Chủ nhà đã TẠM DỪNG - đã tự động dừng replay của bạn.");
                DebugLogger.Info($"ReplayFollower: host paused - pressed the pause key (VK 0x{_pauseKey:X2}).");
            }
        }

        private void ProveHold(long pos)
        {
            if (_proofSentUtc == DateTime.MinValue) return;                  // already proved
            if (_injecting || DateTime.UtcNow - _proofSentUtc < ProofAfter) return;

            if (pos - _proofPos <= HoldSlackBytes)
            {
                DebugLogger.Info($"ReplayFollower: hold confirmed - playhead moved only {pos - _proofPos} bytes.");
                _proofSentUtc = DateTime.MinValue;
                return;
            }

            // Still eating the record, so the replay is not actually paused.
            if (_hold == HoldMethod.PauseKey)
            {
                // NOT another pause-key press: it is a toggle, and if the first press did land
                // late, a second would resume the replay. Escalate to the speed floor instead,
                // which is idempotent and known to stop playback.
                DebugLogger.Warn($"ReplayFollower: the pause key did not stop the replay " +
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
                DebugLogger.Warn("ReplayFollower: gave up holding the replay.");
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

            bool forData = _holdForData;
            _hold = HoldMethod.None;
            _holdForData = false;
            _gaveUp = false;
            ArmProof(pos, expectsMovement: true);
            if (forData)
            {
                // Quietly: a line every capture would bury the log during a close-to-live watch.
                DebugLogger.Info($"ReplayFollower: {(_bufferedMs - _playbackMs) / 1000}s in hand again - " +
                                 $"released via {held}.");
            }
            else
            {
                _log("[Xem] ▶ Chủ nhà đã chơi tiếp - đã cho replay chạy lại.");
                DebugLogger.Info($"ReplayFollower: host resumed - released via {held}.");
            }
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
                DebugLogger.Info($"ReplayFollower: resume confirmed - playhead moved {pos - _proofPos} bytes.");
                _proofSentUtc = DateTime.MinValue;
                return true;
            }

            if (_proofRetries >= MaxRetries)
            {
                _proofSentUtc = DateTime.MinValue;
                _log("[Xem] ⚠️ Replay vẫn đang dừng - hãy nhấn phím tạm dừng trong game để xem tiếp.");
                DebugLogger.Warn("ReplayFollower: gave up resuming the replay - it is still frozen.");
                return true;
            }

            _proofRetries++;
            // First retry repeats the pause key (it is still stopped, so toggling is the right
            // direction). The second raises the speed instead, in case it was the floor holding it.
            Action press = _proofRetries == 1
                ? (Action)(() => GameInput.PressPauseKey(_pauseKey))
                : GameInput.SetReplaySpeedNormal;
            DebugLogger.Warn($"ReplayFollower: resume did not take (playhead moved {pos - _proofPos} " +
                             $"bytes) - retry {_proofRetries}.");
            if (TryInject(press)) ArmProof(pos, expectsMovement: true, keepRetries: true);
            return false;
        }

        // ---------------------------------------------------------------- caught up

        /// <summary>
        /// Game time at the playhead and in the whole file. Each costs a read of the record, so
        /// both are refreshed together and throttled.
        /// </summary>
        private void MeasureRunway(long pos)
        {
            if (DateTime.UtcNow - _lastPlaybackUtc < MeasurePlaybackEvery) return;
            _lastPlaybackUtc = DateTime.UtcNow;
            if (LiveRecordReader.TryGetPlaybackDurationMs(_replayPath, pos, out long played)) _playbackMs = played;
            if (LiveRecordReader.TryGetPlaybackDurationMs(_replayPath, long.MaxValue, out long buffered))
                _bufferedMs = buffered;
        }

        /// <summary>
        /// True when the replay is about to run out of file. Once held for that, stays true until
        /// a good margin has arrived, so the replay is not stopped and started on every capture.
        /// </summary>
        private bool IsStarving()
        {
            if (_playbackMs <= 0 || _bufferedMs <= 0) return false;
            long left = _bufferedMs - _playbackMs;
            if (_hold != HoldMethod.None && _holdForData) return left < RefillTo.TotalMilliseconds;
            return left < StarveBelow.TotalMilliseconds;
        }

        private void CheckCaughtUp(LiveShareClient.HostStatus host)
        {
            if (host == null || !host.InGame) return;
            if (_playbackMs <= 0 || _bufferedMs <= 0) return;

            long leftMs = _bufferedMs - _playbackMs;

            if (leftMs > CatchUpRelease.TotalMilliseconds)
            {
                _caughtUp = false;   // genuinely behind again - re-arm
                return;
            }
            if (_caughtUp || leftMs > CatchUpWithin.TotalMilliseconds) return;

            if (!TryInject(GameInput.SetReplaySpeedNormal)) return;
            _caughtUp = true;
            _log($"[Xem] ⏩ Đã gần đuổi kịp chủ nhà (còn {Math.Max(0, leftMs) / 1000}s đã tải) - " +
                 "đã hạ tốc độ về bình thường.");
            DebugLogger.Info($"ReplayFollower: caught up - playback {_playbackMs}ms of {_bufferedMs}ms downloaded " +
                             $"({leftMs}ms left, host clock {host.GameMs}ms); speed back to normal.");
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
            System.Threading.Tasks.Task.Run(() =>
            {
                try { press(); }
                catch (Exception ex) { DebugLogger.Warn($"ReplayFollower: injection failed: {ex.Message}"); }
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
            catch (Exception ex) { DebugLogger.Warn($"ReplayFollower: dispose failed: {ex.Message}"); }
        }
    }
}
