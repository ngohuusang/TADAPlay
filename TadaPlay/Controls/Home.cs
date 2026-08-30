using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AntdUI.Chat;
using Newtonsoft.Json.Linq;
using TadaPlay;
using TadaPlay.Common.Models;
using TadaPlay.Connections;
using TadaPlay.Connections.Interface;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Services.Interface;
using TadaPlay.Services;
using TadaPlay.Utils;
using TadaPlay.Websockets.Interface;
using TadaPlay.Websockets.Models;

namespace TadaPlay.Controls
{
    public partial class Home : UserControl
    {
        private readonly MainForm mainForm;
        private readonly IWebSocketService webSocketService;
        private readonly IAppContext appContext;
        private readonly IWireGuardVpnService wireGuardVpnService;
        private readonly IAccountService accountService;

        // Hover colour for the clickable name above the user list, and the tooltip both it and
        // the avatar share. One ToolTip instance serves every control it is attached to.
        private static readonly Color AccountLinkHover = Color.FromArgb(0x15, 0x77, 0xD4);
        private readonly ToolTip _accountLinkTip = new ToolTip();

        private bool _uploadInProgress = false;

        // Set when an external WireGuard tunnel (e.g. the official WireGuard app) is already
        // up at load time - in that case we skip starting TadaPlay's own adapter and just use
        // that tunnel's IP, since bringing up a second adapter on top of it is unnecessary
        // (and can fight over routes).
        private string _externalVpnIp;

        // "Observe the record file" detection: runs continuously from app load (not gated
        // behind clicking Start), so a match started outside the app - or one where Start
        // was simply forgotten - still gets picked up and uploaded automatically. Poll for
        // a new record newer than _watchCutoffUtc, then watch it until it stops changing.
        private System.Windows.Forms.Timer _recordWatchTimer;
        private DateTime _watchCutoffUtc; // ignore records older than this
        private string _watchedRecordPath;
        private DateTime? _watchedRecordStartedUtc; // when we started tracking _watchedRecordPath
        private long _lastKnownLength;
        private DateTime _lastKnownWriteTimeUtc;
        private int _stableTicks;
        private const int RecordWatchIntervalMs = 5000;
        private const int StableTicksToFinish = 3; // ~15s with no further writes
        private const int GiveUpWatchingFileAfterMinutes = 240; // give up on this one file, not the whole watcher

        // Match sharing: publishes this player's match over the VPN so others can watch it -
        // periodically while it is being played, and again once it finishes (see
        // LiveShareServer). The in-progress capture goes through a volume shadow copy, never
        // through the game's own file handle; reading through that handle corrupts the record,
        // which was measured rather than guessed (see VolumeSnapshotReader).
        private LiveShareServer _liveShareServer;


        // Profile-name watcher: polls player.nfz's name and reports it to the server whenever it
        // changes (the player renamed their in-game profile). Only re-reports on an actual change,
        // so the server isn't hit on every poll. See ReportInGameNameAsync.
        private System.Windows.Forms.Timer _profileWatchTimer;
        private string _lastSyncedInGameName; // last name we successfully synced OR warned about
        private const int ProfileWatchIntervalMs = 8000;


        public event EventHandler LogoutRequested;

        public Home(MainForm _mainForm, IWebSocketService _webSocketService, IAppContext _appContext,
            IWireGuardVpnService _wireGuardVpnService, IAccountService _accountService)
        {
            InitializeComponent();
            mainForm = _mainForm;
            webSocketService = _webSocketService;
            appContext = _appContext;
            wireGuardVpnService = _wireGuardVpnService;
            accountService = _accountService;

            userList.ItemSelected += userList_ItemSelected;

            // The account editor opens from the identity block above the user list rather than
            // from three cards down in Cài đặt. A label is not obviously clickable, so both the
            // avatar and the name get a hand cursor, a hover colour and a tooltip - without
            // those this is a feature only someone who already knew about it would find.
            MakeAccountLink(userAvatar);
            MakeAccountLink(usernameLabel);
            StyleUserList();

            // BẮT ĐẦU sat visibly inset from the log box below it. Measured against a window
            // capture: 25px of gutter each side and 14px top/bottom, against a Padding of
            // (16,8,16,8) - AntdUI paints the button inside its Padding, and this display scales
            // it by 150%. Margin is NOT involved (zeroing it changed the rendering by 0px), so
            // the horizontal padding is what has to go. The vertical 8 stays: it is what gives
            // the button its height presence, and removing it only makes the label crowd the
            // edge. Set here rather than in the designer file, which Visual Studio regenerates.
            startGameButton.Padding = new Padding(0, 8, 0, 8);

            // The live stream keeps appending to a file on a background loop; without this it
            // would outlive the control it reports to and go on writing to a replay nobody
            // has open. Hooked here rather than in Dispose(bool) because that lives in the
            // designer file, which Visual Studio rewrites.
            // WatcherChanged is a STATIC event, so leaving it subscribed would keep this
            // control (and its whole object graph) alive for the life of the process.
            Disposed += (s, e) =>
            {
                LiveShareServer.WatcherChanged -= LiveShare_WatcherChanged;
                StopLiveStream();
                _spectatorStream?.Dispose();
                _liveShareServer?.Dispose();
                // A ToolTip is a Component, not a child control, so it is not disposed along
                // with the control it decorates.
                _accountLinkTip.Dispose();
            };
        }

        #region AppContext events
        private void AppContext_OnCurrentUserUpdated(object sender, EventArgs e)
        {
            UiUtils.InvokeOnUiThread(this, UpdateUiBasedOnLobbyState, "HOME_CURRENT_USER");
        }

        private void AppContext_OnOnlineUsersUpdated(object sender, EventArgs e)
        {
            UiUtils.InvokeOnUiThread(this, () =>
            {
                UpdateOnlineUsersListView(appContext.AllOnlineUsers);
                DebugLogger.Info($"Home: Online users list updated by AppContext. Count: {appContext.AllOnlineUsers.Count}");
            }, "HOME_ONLINE_USERS");
        }

        /// <summary>
        /// Notes arrivals in the log panel.
        ///
        /// This used to be a tray balloon with a sound alert, which is the wrong weight for the
        /// event: people come and go all evening, every one of them interrupted whatever the
        /// player was doing, and in a busy lobby the alert fired over and over. A log line says
        /// the same thing, stays readable as history, and costs no attention - the online list
        /// beside it is still the live picture.
        /// </summary>
        private void AppContext_OnUserCameOnline(object sender, IReadOnlyList<User> newlyOnlineUsers)
        {
            if (newlyOnlineUsers == null || newlyOnlineUsers.Count == 0) return;

            string names = string.Join(", ", newlyOnlineUsers.Select(u => u.FullName ?? u.Username));
            printLog(newlyOnlineUsers.Count == 1
                        ? $"[Online] {names} vừa online."
                        : $"[Online] {newlyOnlineUsers.Count} người vừa online: {names}",
                     Color.DarkGreen);
        }
        #endregion

        private void Home_Load(object sender, EventArgs e)
        {
            webSocketService.OnConnected += WebSocketService_OnConnected!;
            webSocketService.OnDisconnected += WebSocketService_OnDisconnected!;
            webSocketService.OnErrorOccurred += WebSocketService_OnErrorOccurred!;

            appContext.OnCurrentUserUpdated += AppContext_OnCurrentUserUpdated;
            appContext.OnOnlineUsersUpdated += AppContext_OnOnlineUsersUpdated;
            appContext.OnUserCameOnline += AppContext_OnUserCameOnline;

            wireGuardVpnService.OnConnected += WireGuardVpnService_OnConnected;
            wireGuardVpnService.OnDisconnected += WireGuardVpnService_OnDisconnected;
            wireGuardVpnService.OnErrorOccurred += WireGuardVpnService_OnErrorOccurred;
            wireGuardVpnService.OnIpAddressChanged += WireGuardVpnService_OnIpAddressChanged;

            UpdateUiBasedOnLobbyState();
            _ = OfferUpdateIfAvailableAsync();
            StartRecordWatcher();
            BuildSpectateToggle();
            ApplyMatchSharingSetting();
            // A spectator session swaps the stock age2_x1.5.exe aside; if TadaPlay died before
            // putting it back, do it now rather than leaving the game folder modified.
            GameSpectator.RestoreLaunchTarget(appContext.GetGameFolder());
            // Same idea: older builds opened an inbound rule for the game's spectator port.
            // Nothing listens there now, so take our own leftover back off the player's firewall.
            GameSpectator.RemoveSpectatorPortRule();
            // Let other players measure their latency to this machine. Windows blocks inbound
            // ping by default, and half the connected peers were silent when checked - so
            // without this the new "Đo ping" button reports failure for about half the lobby.
            VpnFirewall.EnsureVpnPingAllowed();
            // TadaPlay no longer WRITES the in-game profile at all. The old ProfileTemplateEnforcer
            // rewrote player.nfz every 10s, which wiped the game's profile<->hotkey link and reset
            // the player's hotkeys. Instead we WATCH player.nfz and, whenever the name changes, READ
            // it and report it to the server as the account's locked identity for replay/ELO matching
            // (see the server's set_in_game_name endpoint + matchPlayersToAccounts).
            StartProfileWatcher();

            webSocketService.Connect();

            // If a WireGuard tunnel is already up outside this app (e.g. the official
            // WireGuard client) and it's already carrying this account's own pinned VPN
            // profile IP - not just any WireGuard-looking tunnel - use it instead of also
            // bringing up our own adapter.
            string pinnedIp = appContext.GetVpnProfile()?.IpAddress;
            _externalVpnIp = ExternalVpnDetector.TryGetExternalWireGuardIp(pinnedIp);
            if (_externalVpnIp != null)
            {
                printLog($"[VPN] Đã phát hiện WireGuard đang chạy sẵn (IP: {_externalVpnIp}) - không cần kết nối VPN từ TadaPlay.", Color.DarkGreen);
                UpdateVpnUi();
                _ = ReportIpToServerAsync(_externalVpnIp);
            }
            else
            {
                UpdateVpnUi();
                _ = wireGuardVpnService.ConnectAsync();
            }

            EnsureGameFolderConfigured();
        }

        // The game folder drives name-sync, record watching, and upload - without it none of
        // that can work, so nudge the user to set it up right away instead of only failing
        // silently later when Start Game or the background watcher needs it.
        private void EnsureGameFolderConfigured()
        {
            if (!string.IsNullOrWhiteSpace(appContext.GetGameFolder())) return;

            AntdUI.Modal.open(new AntdUI.Modal.Config(mainForm, "Chưa cấu hình thư mục game",
                "Bạn cần chọn thư mục cài đặt AoE2 trong Cài đặt để TadaPlay có thể đồng bộ tên, theo dõi và tải lên record trận đấu.",
                AntdUI.TType.Warn)
            {
                OkText = "Mở cài đặt",
                CancelText = "Để sau",
                OnOk = config =>
                {
                    OpenSettingsDialog();
                    return true;
                }
            });
        }

        private void OpenSettingsDialog()
        {
            var setting = new Setting(mainForm, appContext, accountService);
            AntdUI.Modal.open(mainForm, AntdUI.Localization.Get("Setting", "Cài đặt"), setting);
            // The game folder may have been set/changed in Settings - refresh the primary button
            // (it toggles between "Tải game" and "Bắt đầu").
            UpdateStartButtonState();
        }

        // Opens the game downloader; on completion the game folder is configured, so refresh the
        // primary button (it flips from "Tải game" to "Bắt đầu").
        private void OpenGameDownload()
        {
            using var downloader = new GameDownloadForm(appContext);
            downloader.ShowDialog(mainForm);
            UpdateStartButtonState();
        }

        // Runs for the lifetime of the app, independent of whether Start Game was clicked.
        private void StartRecordWatcher()
        {
            _watchCutoffUtc = DateTime.UtcNow;

            if (_recordWatchTimer == null)
            {
                _recordWatchTimer = new System.Windows.Forms.Timer { Interval = RecordWatchIntervalMs };
                _recordWatchTimer.Tick += RecordWatchTimer_Tick;
            }
            _recordWatchTimer.Start();
            printLog("[Theo dõi] Đang theo dõi thư mục record để tự động tải lên khi có trận đấu mới.", Color.RoyalBlue);
        }

        /// <summary>
        /// Makes a finished match available to other players. Called once the record watcher
        /// sees the game release the file - never while a match is in progress, because
        /// reading a record the game is still writing corrupts it.
        /// </summary>
        private void PublishFinishedMatch(string recordPath)
        {
            string published = LiveRecordSnapshotStore.PublishFinished(recordPath, out string note);
            if (published != null)
            {
                printLog("[Xem] Trận đấu đã sẵn sàng để người khác xem lại.", Color.RoyalBlue);
            }
            else if (!string.IsNullOrEmpty(note))
            {
                printLog($"[Xem] Không chia sẻ được trận đấu: {note}", Color.Orange);
            }
        }

        // Live sharing: while a match is running, capture it periodically so others can watch
        // before it ends. The capture goes through a volume shadow copy, which never touches
        // the game's file handle - reading through a duplicate of that handle is what
        // corrupted a real match (see VolumeSnapshotReader).
        //
        // Captures are driven by two things: the game having actually written more of the
        // record, and whether anyone is watching.
        //
        // The first capture waits 90 seconds so the match has real content behind it; that is
        // also the countdown a viewer is shown. After that a capture happens only while
        // somebody is actually watching, every 30 seconds - an unwatched match takes exactly
        // one snapshot, not one every 90 seconds for the length of the game.
        //
        // 30 seconds is a floor, not a promise: a shadow copy takes a couple of seconds, and
        // while one is running the next tick is skipped rather than queued, so the real gap is
        // 30 seconds plus however long the capture took. The capture duration is logged.
        //
        // Either way a capture only happens if the record has GROWN since the last one. A
        // shadow copy costs a couple of seconds and briefly quiesces volume writes, and taking
        // one to produce a byte-identical file is pure cost - during a pause, a lobby, or a
        // stretch where the game has not flushed, this skips entirely.
        private System.Windows.Forms.Timer _liveShareTimer;
        // The tick has to be comfortably finer than the shortest interval below, because a
        // capture only happens on a tick: at a 5s tick a 10s interval would actually fire at
        // 10s or 15s depending on where the two fell relative to each other. A tick that
        // decides not to capture costs a file-length check, so it is cheap to run often.
        private const int LiveShareTickMs = 2 * 1000;              // how often the decision is made
        private const int FirstCaptureDelayMs = 90 * 1000;         // before a match is watchable
        // Somebody is watching. Was 10s, which is the cadence players reported the game
        // stuttering at: a shadow copy quiesces writes across the whole volume for a moment,
        // and doing that six times a minute is felt in a game that is streaming its own assets
        // off the same disk. A viewer is watching a replay from the start and is held behind
        // the host anyway (see ReplayFollower), so arriving in 30s steps costs them nothing
        // they can perceive - and this path only runs at all when the game is NOT serving its
        // own real-time stream, which is the preferred source and needs no snapshot.
        private const int WatchedCaptureIntervalMs = 30 * 1000;
        private bool _liveCaptureBusy;
        private string _liveCaptureMatch;   // record the current live captures belong to
        private int _liveCaptureCount;      // captures of the current match, for the log
        private long _liveCaptureBytes;     // snapshot size at the previous capture, to report growth
        private long _liveCaptureSourceLength; // record size at the previous capture, to detect growth
        private DateTime _lastCaptureUtc = DateTime.MinValue;

        // Real-time snapshot source: the game's own spectator stream on loopback 53754, which
        // supersedes the shadow-copy capture whenever it is serving. See SpectatorStreamSource.
        private SpectatorStreamSource _spectatorStream;

        private void LiveShareTimer_Tick(object sender, EventArgs e)
        {
            if (IsDisposed) return;

            // Before the capture, and outside the busy check: the event worth reporting - the
            // last viewer leaving - happens when requests STOP, so nothing else would notice.
            LiveShareServer.SweepWatchers();

            // Prefer the game's own real-time stream (loopback 53754) as the snapshot source.
            // While it is feeding the snapshot the shadow-copy capture below is redundant, so
            // it is skipped; the shadow copy stays as the fallback for a game that is not
            // serving the stream (Record Game/Allow Spectators off, or an older build).
            if (EnsureSpectatorStreamStarted())
            {
                MatchStatusPublisher.Publish(webSocketService, appContext.GetAllowSpectateSetting());

                // The stream source owns the clock while it is serving - but only if it is
                // actually producing one. Observed on a live host: the stream connected and
                // served the match (has_match true, 3s cadence) while game_ms stayed 0 for the
                // whole game, so every viewer saw 00:00. The clock is worth a periodic read of
                // the snapshot on its own rather than being a side effect of that path.
                if (MatchShareState.InGame && MatchShareState.DurationMs == 0
                    && DateTime.UtcNow - _lastClockFallbackUtc > ClockFallbackInterval)
                {
                    _lastClockFallbackUtc = DateTime.UtcNow;
                    _ = System.Threading.Tasks.Task.Run(RefreshClockOnly);
                }
                return;
            }

            if (_liveCaptureBusy || !ShouldCaptureNow()) return;

            // Sharing off: still refresh the match clock so other players see "đang chơi 25:30 ·
            // không spec" rather than a stuck 00:00. Reading the record for its duration is a
            // local file walk - none of the shadow copy, snapshot or serving that sharing does.
            if (!appContext.GetAllowSpectateSetting())
            {
                // Paced with _lastCaptureUtc only - deliberately NOT MatchShareState.Captured(),
                // which also re-arms the countdown viewers are shown. Doing that here published
                // "chờ 90 giây" to everyone while sharing was off, i.e. a countdown to a capture
                // that is never coming, which is the exact thing this change set out to remove.
                _lastCaptureUtc = DateTime.UtcNow;
                _ = System.Threading.Tasks.Task.Run(RefreshClockOnly);
                return;
            }

            _liveCaptureBusy = true;
            _ = System.Threading.Tasks.Task.Run(CaptureLiveMatch);
        }

        /// <summary>
        /// Updates only the match clock, for a player who has opted out of being watched.
        ///
        /// They are still playing and the list should say so with a running time; what they
        /// declined is being watched. This walks the record for its duration and nothing else -
        /// no shadow copy, no snapshot, no listening port, no bytes on the VPN.
        /// </summary>
        private void RefreshClockOnly()
        {
            try
            {
                string recordPath = MatchShareState.CurrentRecordPath ?? _watchedRecordPath;
                if (recordPath == null) return;

                // Read the SNAPSHOT, never the live record. The game holds its own record open
                // (LiveRecordReader.IsLockedByGame) so File.ReadAllBytes on it just throws -
                // that is the entire reason the shadow-copy capture exists. Analyzing the live
                // path here is what made the first version of this backstop do nothing at all:
                // it failed silently on every call and the clock stayed at 00:00.
                string snapshotPath = LiveRecordSnapshotStore.FindFor(recordPath)
                                      ?? LiveRecordSnapshotStore.Current();
                if (snapshotPath == null)
                {
                    // No snapshot means nothing has been captured - true for a player who has
                    // opted out of sharing. They get no clock, which is honest: the status
                    // already reads "đang chơi, không spec" without a time.
                    DebugLogger.Info("Home: clock refresh skipped - no snapshot for this match yet.");
                    return;
                }

                LiveRecordReader.RecordAnalysis analysis = LiveRecordReader.AnalyzeFile(snapshotPath);
                if (analysis == null || analysis.BodyBytes <= 0)
                {
                    DebugLogger.Info($"Home: clock refresh found no usable body in '{snapshotPath}'.");
                    return;
                }

                DebugLogger.Info($"Home: clock refresh - duration {analysis.DurationMs}ms "
                               + $"from {analysis.BodyBytes} body bytes.");

                MatchShareState.ReportDuration(analysis.DurationMs);
                UiUtils.InvokeOnUiThread(this,
                    () => MatchStatusPublisher.Publish(webSocketService, appContext.GetAllowSpectateSetting()),
                    "HOME_CLOCK_ONLY");
            }
            catch (Exception ex)
            {
                // Never fatal - the clock simply stays where it was.
                DebugLogger.Warn($"Home: clock-only refresh failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts, or keeps alive, the loopback spectator-stream source feeding the snapshot.
        /// Returns true while it is running (so the caller skips the shadow-copy capture), and
        /// false when there is no match yet or the game is not serving the stream.
        /// </summary>
        private bool EnsureSpectatorStreamStarted()
        {
            if (_spectatorStream?.IsRunning == true) return true;

            string recordPath = _watchedRecordPath;
            if (recordPath == null) return false;

            // Only worth trying once the game actually holds the record open - i.e. a match is
            // running and the stream port would be serving.
            if (!LiveRecordReader.IsLockedByGame(recordPath)) return false;

            _spectatorStream?.Dispose();
            _spectatorStream = new SpectatorStreamSource(recordPath,
                (msg, isProblem) => printLog(msg, isProblem ? Color.Orange : Color.RoyalBlue));

            if (_spectatorStream.TryStart())
            {
                // Watchable in real time now, not after the shadow-copy first-capture wait.
                MatchShareState.CaptureInterval = TimeSpan.FromSeconds(3);
                MatchShareState.Captured();
                printLog("[Xem] Nguồn xem: luồng trực tiếp từ game (real-time) - người khác có " +
                         $"thể xem trận của bạn sau {MatchShareState.SpectateAfterGameMs / 1000} giây " +
                         "đầu trận.", Color.DarkGreen);
                return true;
            }

            _spectatorStream = null;
            return false;
        }

        /// <summary>
        /// Whether a shadow copy is worth taking right now: there is a match, the game has
        /// written more of it since last time, and enough time has passed for the cadence the
        /// current audience deserves.
        /// </summary>
        private bool ShouldCaptureNow()
        {
            string recordPath = _watchedRecordPath;
            if (recordPath == null) return false;

            long length;
            try
            {
                var info = new FileInfo(recordPath);
                if (!info.Exists) return false;
                length = info.Length;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"Home: cannot size '{recordPath}': {ex.Message}");
                return false;
            }

            // Nothing new written since the last capture - a snapshot now would cost seconds
            // and produce a file byte-identical to the one already being served.
            if (_liveCaptureCount > 0 && length <= _liveCaptureSourceLength) return false;

            // Once the match is watchable, capture only while somebody is actually watching.
            //
            // It used to keep taking one every 90s regardless, so a shared match nobody opened
            // still paid a shadow copy - and its volume-write freeze - roughly forty times over
            // a long game, to produce snapshots that were never read. The first capture is what
            // makes the match watchable; after that there is nothing to serve until there is
            // someone to serve it to. A viewer arriving later gets that first snapshot at once
            // and a fresh one within WatchedCaptureIntervalMs, which is the same experience
            // they had before.
            if (_liveCaptureCount > 0 && LiveShareServer.CurrentWatchers().Count == 0) return false;

            return (DateTime.UtcNow - _lastCaptureUtc).TotalMilliseconds >= NextCaptureIntervalMs();
        }

        /// <summary>
        /// The gap before the next capture: the long first-capture wait until this match has
        /// been captured once, then whatever the current audience deserves.
        ///
        /// Shared with the countdown reported to viewers, so the number they are shown is the
        /// one actually being used to schedule. It is still a floor rather than a promise - a
        /// capture also needs the record to have grown, and a shadow copy takes a couple of
        /// seconds on top.
        /// </summary>
        private int NextCaptureIntervalMs() =>
            _liveCaptureCount == 0 ? FirstCaptureDelayMs : WatchedCaptureIntervalMs;

        /// <summary>Announces viewers coming and going, so the player knows they are watched.</summary>
        private void LiveShare_WatcherChanged(LiveShareServer.Watcher watcher, bool started)
        {
            if (IsDisposed) return;
            if (started)
            {
                printLog($"[Xem] {watcher.Address} bắt đầu xem trận của bạn.", Color.DarkGreen);
                return;
            }

            double minutes = (DateTime.UtcNow - watcher.FirstSeenUtc).TotalMinutes;
            printLog($"[Xem] {watcher.Address} đã ngừng xem (xem {minutes:F0} phút, " +
                     $"đã gửi {watcher.BytesServed / 1024} KB).", Color.RoyalBlue);
        }

        // Runs off the UI thread: a shadow copy takes seconds.
        private void CaptureLiveMatch()
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string recordPath = _watchedRecordPath;
                if (recordPath == null || !LiveRecordReader.IsLockedByGame(recordPath))
                {
                    return; // no match in progress, or it has already finished and been published
                }

                int watchers = LiveShareServer.CurrentWatchers().Count;
                long sourceLength = new FileInfo(recordPath).Length;
                DebugLogger.Info($"Home: live capture starting for '{Path.GetFileName(recordPath)}' " +
                                 $"({watchers} watching, record is {sourceLength} bytes, " +
                                 $"+{sourceLength - _liveCaptureSourceLength} since last capture).");
                _liveCaptureSourceLength = sourceLength;

                byte[] data = VolumeSnapshotReader.TryReadLockedFile(recordPath, out string error);
                if (data == null)
                {
                    DebugLogger.Warn($"Home: live capture skipped after {started.ElapsedMilliseconds} ms: {error}");
                    return;
                }
                DebugLogger.Info($"Home: shadow copy read {data.Length} bytes in {started.ElapsedMilliseconds} ms.");

                // Mid-match the header length is still a zero placeholder, so the snapshot has
                // to be repaired and trimmed before anyone can play it.
                LiveRecordReader.RecordAnalysis analysis = LiveRecordReader.Analyze(data, recordPath);
                if (analysis == null || analysis.BodyBytes <= 0)
                {
                    DebugLogger.Info($"Home: live capture produced nothing playable yet " +
                                     $"({data.Length} bytes read, no whole operations).");
                    return;
                }

                var snapshot = new LiveRecordReader.Snapshot
                {
                    SourcePath = recordPath,
                    Data = analysis.NeedsRepair
                        ? analysis.RepairedData
                        : Trim(data, analysis.HeaderLength + analysis.BodyBytes),
                    HeaderLength = analysis.HeaderLength,
                    BodyBytes = analysis.BodyBytes,
                    BodyOperations = analysis.Operations
                };
                if (LiveRecordSnapshotStore.Save(snapshot) == null) return;

                bool newMatch = !string.Equals(_liveCaptureMatch, recordPath,
                                               StringComparison.OrdinalIgnoreCase);
                _liveCaptureMatch = recordPath;
                if (newMatch)
                {
                    // Both counters belong to the match, not the app - without the byte reset
                    // the first capture of match two would report growth against match one.
                    _liveCaptureCount = 0;
                    _liveCaptureBytes = 0;
                    printLog("[Xem] Cho phép spec - người khác đã có thể xem trận của bạn.",
                             Color.DarkGreen);
                }
                _liveCaptureCount++;

                // The player gets a line per capture: it is the only outward sign that sharing
                // is alive during a match, and at 90s apart it stays readable over a long game.
                int growthKb = (int)((snapshot.Data.Length - _liveCaptureBytes) / 1024);
                _liveCaptureBytes = snapshot.Data.Length;
                // Published so viewers can be told how far into the match they would be
                // joining, rather than just that something is available.
                MatchShareState.ReportDuration(analysis.DurationMs);
                var clock = TimeSpan.FromMilliseconds(analysis.DurationMs);
                printLog($"[Xem] Đã cập nhật trận cho người xem (phút {clock.Minutes:00}:{clock.Seconds:00}, " +
                         $"lần {_liveCaptureCount}: {snapshot.Data.Length / 1024} KB, +{growthKb} KB).",
                         Color.RoyalBlue);

                DebugLogger.Info($"Home: live capture {_liveCaptureCount} of '{recordPath}': " +
                                 $"{analysis.Operations} ops, {snapshot.Data.Length} bytes " +
                                 $"(header {analysis.HeaderLength}, body {analysis.BodyBytes}, " +
                                 $"repaired={analysis.NeedsRepair}) in {started.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Home: live capture failed: {ex.Message}");
            }
            finally
            {
                // Timed from the END of the capture, so a slow shadow copy does not shorten
                // the gap before the next one and pile them up.
                _lastCaptureUtc = DateTime.UtcNow;
                // Report the interval that will ACTUALLY be used next. Left at the 90s
                // first-capture value, the countdown told viewers to expect a minute and a
                // half between updates while captures were really 10s apart.
                MatchShareState.CaptureInterval = TimeSpan.FromMilliseconds(NextCaptureIntervalMs());
                MatchShareState.Captured();
                MatchStatusPublisher.Publish(webSocketService, appContext.GetAllowSpectateSetting());
                _liveCaptureBusy = false;
            }
        }

        private static byte[] Trim(byte[] data, int length)
        {
            if (length >= data.Length) return data;
            var trimmed = new byte[length];
            Buffer.BlockCopy(data, 0, trimmed, 0, length);
            return trimmed;
        }

        // Runs for the lifetime of the app alongside the record watcher: publishes this
        // player's matches - periodically while one is running, and again once it finishes.
        private AntdUI.Switch _spectateToggle;

        // AntdUI's Switch animates, and CheckedChanged fires off the back of that animation -
        // after construction has returned, so a "still building" flag does not catch it. On a
        // fresh profile that raised the handler unprompted, briefly opening the share port and
        // logging "đã bật chia sẻ" then "đã tắt" before anyone touched anything.
        //
        // So the handler acts only on input the user actually gave: set by a real click on the
        // switch, and re-checked against the stored setting so a repeated or spurious event
        // cannot do any work twice. The stored setting is the truth; the control reports.
        private bool _userToggledSpectate;

        /// <summary>
        /// The "let others watch me" switch, directly under BẮT ĐẦU.
        ///
        /// It lives here rather than buried in Settings because it is a decision players make
        /// per session - "am I willing to be watched in this game?" - and because the cost of
        /// leaving it on is paid by everyone: sharing streams tens of megabytes per viewer
        /// across the same VPN the whole lobby plays through. A control you can see is one you
        /// can turn off.
        ///
        /// Added in code, not the designer: playPanel is a Dock layout whose order comes from
        /// z-order, and Home.Designer.cs is regenerated whenever anyone opens the form.
        /// </summary>
        private void BuildSpectateToggle()
        {
            if (_spectateToggle != null) return;

            // A compact row rather than a docked switch: Dock=Top stretches an AntdUI.Switch
            // across the full width, which reads as a grey bar rather than a toggle.
            var row = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                BackColor = Color.Transparent,
            };

            _spectateToggle = new AntdUI.Switch
            {
                Size = new Size(44, 24),
                Location = new Point(8, 9),
                Checked = appContext.GetAllowSpectateSetting(),
            };

            var caption = new AntdUI.Label
            {
                Text = "Cho phép người khác xem trận của tôi",
                Font = new Font("Segoe UI", 11F),
                ForeColor = Color.FromArgb(90, 98, 105),
                Location = new Point(62, 10),
                Size = new Size(420, 24),
                BackColor = Color.Transparent,
            };

            // Only a real click counts as intent - see the note on _userToggledSpectate.
            _spectateToggle.MouseDown += (sender, e) => _userToggledSpectate = true;

            _spectateToggle.CheckedChanged += (sender, e) =>
            {
                if (!_userToggledSpectate) return;

                bool wanted = _spectateToggle.Checked;
                // Nothing to do if this only restates what is already stored: keeps a repeated
                // animation event from re-running the start/stop work and logging it twice.
                if (wanted == appContext.GetAllowSpectateSetting()) return;

                appContext.SetAllowSpectateSetting(wanted);
                ApplyMatchSharingSetting();
                printLog(wanted
                        ? "[Xem] Đã bật cho phép người khác xem trận của bạn."
                        : "[Xem] Đã tắt chia sẻ trận - không ai xem được trận của bạn.",
                    wanted ? Color.RoyalBlue : Color.Gray);
            };

            row.Controls.Add(caption);
            row.Controls.Add(_spectateToggle);

            playPanel.Controls.Add(row);
            // Dock order is reverse z-order: the LAST child added docks topmost. Index 1 puts
            // this just above the log box (index 0, Dock=Fill) and therefore directly beneath
            // the start button, which is what "under BẮT ĐẦU" means on screen.
            playPanel.Controls.SetChildIndex(row, 1);
        }

        /// <summary>
        /// Starts or stops sharing to match the current setting. Safe to call repeatedly, so the
        /// settings dialog can call it the moment the switch is flipped rather than making the
        /// player restart to have their own choice take effect.
        /// </summary>
        public void ApplyMatchSharingSetting()
        {
            if (appContext.GetAllowSpectateSetting())
            {
                StartMatchSharing();
            }
            else
            {
                StopMatchSharing();
            }
        }

        /// <summary>
        /// Tears sharing down: no capture timer, no listening port, and nothing advertised to
        /// the lobby. Also clears any match already published, or a player who turns this off
        /// mid-match would stay watchable for the rest of it - which is the opposite of what
        /// they just asked for.
        /// </summary>
        private void StopMatchSharing()
        {
            _liveShareTimer?.Stop();
            LiveShareServer.WatcherChanged -= LiveShare_WatcherChanged;

            _spectatorStream?.Dispose();
            _spectatorStream = null;

            _liveShareServer?.Dispose();
            _liveShareServer = null;

            // NOT MatchEnded(): the player is still in their game, others just cannot watch
            // it. Ending the match here made them vanish from the list as "playing" entirely.
            MatchShareState.SharingStopped();
            MatchStatusPublisher.Reset();
            MatchStatusPublisher.Publish(webSocketService, appContext.GetAllowSpectateSetting(), force: true);
        }

        private void StartMatchSharing()
        {
            // Off by default: capturing the record on a timer, listening on a port and streaming
            // tens of megabytes per viewer across the shared VPN is not a cost to impose on
            // someone who never asked for it.
            if (!appContext.GetAllowSpectateSetting())
            {
                DebugLogger.Info("Home: match sharing is off (Cho phép xem trận is disabled).");
                return;
            }

            LiveRecordSnapshotStore.Prune();

            // The countdown a viewer is shown is the wait for the FIRST capture; after that a
            // match is watchable and the number is no longer displayed, so this is the one
            // that belongs here even though later captures run faster.
            MatchShareState.CaptureInterval = TimeSpan.FromMilliseconds(FirstCaptureDelayMs);

            if (_liveShareTimer == null)
            {
                // Ticks often and decides cheaply; ShouldCaptureNow does the real gating.
                _liveShareTimer = new System.Windows.Forms.Timer { Interval = LiveShareTickMs };
                _liveShareTimer.Tick += LiveShareTimer_Tick;
            }
            _liveShareTimer.Start();

            LiveShareServer.WatcherChanged += LiveShare_WatcherChanged;

            _liveShareServer = new LiveShareServer();
            if (_liveShareServer.Start())
            {
                printLog("[Xem] Đã bật chia sẻ trận đấu - người khác có thể xem trận của bạn " +
                         $"sau {FirstCaptureDelayMs / 1000} giây kể từ khi vào trận, rồi cập nhật " +
                         $"mỗi {WatchedCaptureIntervalMs / 1000} giây khi có người xem.",
                         Color.RoyalBlue);
            }
            else
            {
                printLog($"[Xem] Không mở được cổng chia sẻ trận đấu ({LiveShareServer.Port}). " +
                         "Người khác sẽ không xem được trận của bạn.", Color.Orange);
            }
        }

        // Polls player.nfz on a short interval; ReportInGameNameAsync only actually hits the server
        // when the name has changed, so the game can rewrite the profile as often as it likes.
        private void StartProfileWatcher()
        {
            if (_profileWatchTimer == null)
            {
                _profileWatchTimer = new System.Windows.Forms.Timer { Interval = ProfileWatchIntervalMs };
                _profileWatchTimer.Tick += (s, e) => _ = ReportInGameNameAsync();
            }
            _profileWatchTimer.Start();
            _ = ReportInGameNameAsync(); // sync the current name immediately, don't wait a full interval
        }

        // Reads the player's actual in-game profile name (from player.nfz) and, if it changed since
        // last time, reports it to the server as the account's locked identity for replay/ELO matching.
        // Replaces the old approach of FORCING the profile name to the username (which reset in-game
        // hotkeys). Best-effort: failures are logged and never block the app or launching the game.
        private async Task ReportInGameNameAsync()
        {
            try
            {
                string gameFolder = appContext.GetGameFolder();
                string inGameName = GameProfileNameReader.ReadActiveName(gameFolder);
                if (string.IsNullOrWhiteSpace(inGameName)) return;

                // Unchanged since the last sync/warning - nothing to do (keeps the poll cheap).
                if (string.Equals(inGameName, _lastSyncedInGameName, StringComparison.OrdinalIgnoreCase)) return;

                var result = await accountService.SetInGameNameAsync(inGameName);
                if (result == null) return;

                if (!result.Success && result.Conflict)
                {
                    // Remember it so we don't re-warn every poll; the server has also logged this
                    // collision to its smurf list for review.
                    _lastSyncedInGameName = inGameName;
                    UiUtils.InvokeOnUiThread(this, () =>
                        AntdUI.Notification.warn(mainForm, "Tên trong game bị trùng",
                            $"Tên hồ sơ trong game \"{inGameName}\" đã được người chơi khác sử dụng. " +
                            "Hãy đổi tên hồ sơ trong game để trận đấu được tính điểm chính xác.",
                            AntdUI.TAlignFrom.Bottom, Font), "HOME_INGAME_NAME_CONFLICT");
                }
                else if (result.Success)
                {
                    _lastSyncedInGameName = inGameName;
                    // Tell the lobby too. The REST call above updates the database, which is
                    // what a client reads when it CONNECTS - without this, everyone already
                    // online keeps showing the old name until they reconnect.
                    try
                    {
                        _ = webSocketService.SendMessageAsync(new
                        {
                            command = "in_game_name",
                            in_game_name = inGameName
                        });
                    }
                    catch (Exception ex)
                    {
                        // Never fatal: the name is saved server-side regardless, so the worst
                        // case is other players seeing it a reconnect later.
                        DebugLogger.Warn($"Home: could not broadcast in-game name: {ex.Message}");
                    }
                    printLog($"[Hồ sơ] Đã đồng bộ tên trong game \"{inGameName}\" với máy chủ.", Color.DarkGreen);
                }
                // Other (transient) failures: leave _lastSyncedInGameName so the next poll retries.
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Home: report in-game name failed: {ex.Message}");
            }
        }

        // --- VPN wiring ---
        private void WireGuardVpnService_OnConnected(object sender, EventArgs e)
        {
            UiUtils.InvokeOnUiThread(this, () =>
            {
                printLog("[VPN] Đã kết nối VPN thành công.", Color.DarkGreen);
                UpdateVpnUi();
            }, "HOME_VPN_CONNECTED");
        }

        private void WireGuardVpnService_OnDisconnected(object sender, EventArgs e)
        {
            UiUtils.InvokeOnUiThread(this, () =>
            {
                printLog("[VPN] VPN đã ngắt kết nối.", Color.Red);
                UpdateVpnUi();
            }, "HOME_VPN_DISCONNECTED");
        }

        private void WireGuardVpnService_OnErrorOccurred(object sender, string errorMessage)
        {
            UiUtils.InvokeOnUiThread(this, () =>
            {
                printLog($"[Lỗi VPN] {errorMessage}", Color.Red);
                UpdateVpnUi();
            }, "HOME_VPN_ERROR");
        }

        private void WireGuardVpnService_OnIpAddressChanged(object sender, string ip)
        {
            UiUtils.InvokeOnUiThread(this, () =>
            {
                ipAddressLabel.Text = $"IP: {ip}";
            }, "HOME_VPN_IP");

            // Server-side ELO matching cross-checks this against the account's permanently
            // pinned VPN profile IP, so a mismatched/spoofed identity can't earn rating
            // even if the in-game name happens to match.
            _ = ReportIpToServerAsync(ip);

            // The WS server's live user_list only reflects a user's IP once it receives this
            // exact WS message (see GameLobbyServer::onMessage's "ip_address" branch) - it does
            // NOT read the DB value the REST call above just persisted. Without this, the IP
            // column in the online-users list stays blank for everyone until the server process
            // restarts.
            //
            // Best-effort only: the VPN can connect (firing this event) before the WebSocket
            // itself has finished connecting, and SendMessageAsync surfaces a visible error
            // notification if called while disconnected. Skip silently here - the value gets
            // resent once the WS actually connects (see WebSocketService_OnConnected below).
            if (!string.IsNullOrWhiteSpace(ip) && webSocketService.IsConnected)
            {
                _ = webSocketService.SendMessageAsync(new { ip_address = ip });
            }
        }

        private async System.Threading.Tasks.Task ReportIpToServerAsync(string ip)
        {
            try
            {
                UpdateIpResponse result = await accountService.UpdateCurrentIPToServer(ip);
                if (result == null) return;

                if (!result.Matched)
                {
                    printLog($"[Cảnh báo] IP hiện tại ({result.Ip}) khác với IP trước đó của tài khoản " +
                             $"({result.ProfileIp}) - đã cập nhật IP mới.", Color.Firebrick);
                }
                else if (result.Updated)
                {
                    printLog($"[VPN] Đã lưu IP cho tài khoản: {result.Ip}", Color.DarkGreen);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Home: Failed to report VPN IP to server: {ex.Message}");
            }
        }

        private void UpdateVpnUi()
        {
            bool connected = wireGuardVpnService.IsConnected || _externalVpnIp != null;
            string ip = _externalVpnIp ?? wireGuardVpnService.CurrentIpAddress;
            vpnStatusLabel.Text = connected ? "VPN: Đã kết nối" : "VPN: Chưa kết nối";
            vpnStatusLabel.ForeColor = connected ? Color.DarkGreen : Color.Firebrick;
            ipAddressLabel.Text = connected ? $"IP: {ip}" : "IP: -";
            reconnectVpnButton.Visible = !connected;
            UpdateStartButtonState();
        }

        private async void reconnectVpnButton_Click(object sender, EventArgs e)
        {
            reconnectVpnButton.Enabled = false;
            printLog("[VPN] Đang thử kết nối lại VPN...", Color.RoyalBlue);
            try
            {
                bool connected = await wireGuardVpnService.ConnectAsync();
                if (!connected)
                {
                    printLog("[VPN] Kết nối lại VPN thất bại. Thử lại sau.", Color.Red);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Home: Reconnect VPN thất bại: {ex.Message}");
                printLog($"[VPN] Lỗi kết nối lại VPN: {ex.Message}", Color.Red);
            }
            finally
            {
                UiUtils.InvokeOnUiThread(this, () => reconnectVpnButton.Enabled = true);
            }
        }

        // True only when the configured folder actually contains the game (the age2_x1 folder), not
        // merely that some path is set - a moved/deleted/empty folder should still offer the download.
        private bool IsGameInstalled()
        {
            string folder = appContext.GetGameFolder();
            return !string.IsNullOrWhiteSpace(folder)
                && System.IO.Directory.Exists(System.IO.Path.Combine(folder, "age2_x1"));
        }

        private void UpdateStartButtonState()
        {
            // Give the primary button a distinct, mode-appropriate look: a bold red PLAY button when
            // the game is ready, and an inviting blue DOWNLOAD button when it still needs installing.
            // Download doesn't need a VPN, so it's always enabled.
            startGameButton.WaveSize = 6;
            if (IsGameInstalled())
            {
                startGameButton.Text = "BẮT ĐẦU";
                startGameButton.IconSvg = "PlayCircleOutlined";
                startGameButton.Type = AntdUI.TTypeMini.Error;
                startGameButton.Enabled = (wireGuardVpnService.IsConnected || _externalVpnIp != null) && !_uploadInProgress;
            }
            else
            {
                startGameButton.Text = "TẢI GAME";
                startGameButton.IconSvg = "DownloadOutlined";
                startGameButton.Type = AntdUI.TTypeMini.Primary;
                startGameButton.Enabled = !_uploadInProgress;
            }
        }

        // --- WebSocket wiring ---
        private void WebSocketService_OnConnected(object sender, EventArgs e)
        {
            UiUtils.InvokeOnUiThread(this, () =>
            {
                DebugLogger.Info("WebSocketService reported connected. Attempting initial refresh.");
                webSocketService.RefreshAsync();
                UpdateUiBasedOnLobbyState();

                // Re-push the already-known VPN IP - if the VPN connected (and the earlier
                // WireGuardVpnService_OnIpAddressChanged fire) happened while the WS was down or
                // still reconnecting, that ip_address message would have silently no-opped.
                string currentIp = _externalVpnIp ?? wireGuardVpnService.CurrentIpAddress;
                if (!string.IsNullOrWhiteSpace(currentIp))
                {
                    _ = webSocketService.SendMessageAsync(new { ip_address = currentIp });
                }

                // Same reasoning for the shared match: the server drops this machine's match
                // status when the connection closes, so after a reconnect its view and ours
                // have diverged. Reset first, or the "same as last time" check would suppress
                // the re-push and leave everyone seeing nothing for the rest of the match.
                MatchStatusPublisher.Reset();
                MatchStatusPublisher.Publish(webSocketService, appContext.GetAllowSpectateSetting(), force: true);
            }, "HOME_WS_CONNECTED");
        }

        private void WebSocketService_OnDisconnected(object sender, EventArgs e)
        {
            UiUtils.InvokeOnUiThread(this, UpdateUiBasedOnLobbyState, "HOME_WS_DISCONNECTED");
        }

        private void WebSocketService_OnErrorOccurred(object sender, string errorMessage)
        {
            UiUtils.InvokeOnUiThread(this, () =>
            {
                AntdUI.Notification.error(mainForm, AntdUI.Localization.Get("ConnectionErrorTitle", "Lỗi kết nối"), AntdUI.Localization.Get("ConnectionErrorContent", errorMessage), AntdUI.TAlignFrom.Bottom, Font);
                DebugLogger.Error($"Home Control WS Error: {errorMessage}");
            }, "HOME_WS_ERROR");
        }

        private static readonly Color PlayingColor = Color.FromArgb(82, 196, 26);   // antd green-6
        private static readonly Color StartingColor = Color.FromArgb(250, 173, 20); // antd gold-6

        /// <summary>
        /// The one-line "what is this player doing" shown against their name.
        ///
        /// Driven by the match status broadcast with the user list, because the lobby's own
        /// status field does not distinguish someone idle in the lobby from someone twenty
        /// minutes into a game - it says "online" for both. Falls back to that field for
        /// players whose build does not report a match.
        /// </summary>
        /// <summary>
        /// The right-hand status label, in full.
        ///
        /// MsgList gives the name priority on that line and leaves the status whatever width
        /// remains, so this only fits because StyleUserList widens the panel - at the original
        /// 33% even "20:40" rendered as "20...".
        /// </summary>
        private static (string Label, Color Colour) DescribeActivity(User user, string status)
        {
            if (user.IsWatchable)
            {
                TimeSpan t = user.GameTime;
                string clock = t.TotalHours >= 1
                    ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                    : $"{t.Minutes:00}:{t.Seconds:00}";
                return (user.Paused ? $"tạm dừng · {clock}" : $"đang chơi · {clock}",
                        user.Paused ? PausedListColor : PlayingColor);
            }

            // Opted out: they are playing and will never become watchable, so a countdown
            // would be a promise the app cannot keep.
            if (user.InGame && !user.AllowSpectate)
            {
                TimeSpan playing = user.GameTime;
                string clock = playing.TotalMilliseconds > 0
                    ? (playing.TotalHours >= 1
                        ? $"{(int)playing.TotalHours}:{playing.Minutes:00}:{playing.Seconds:00}"
                        : $"{playing.Minutes:00}:{playing.Seconds:00}")
                    : null;
                return (clock == null ? "đang chơi, không spec" : $"đang chơi {clock}, không spec",
                        NoSpecColor);
            }

            if (user.InGame) return ("chờ xem được", StartingColor);

            switch ((status ?? string.Empty).ToLowerInvariant())
            {
                case "host": return ("chủ phòng", HostColor);
                case "joined": return ("trong phòng", RoomColor);
                case "spectating": return ("đang xem", RoomColor);
                // "online" is dropped deliberately: this is the list of players who are online,
                // so stamping it on nearly every row said nothing.
                default: return (string.Empty, Color.Gray);
            }
        }

        /// <summary>
        /// The two lines of a row: who they are, and the supporting detail underneath.
        ///
        /// The in-game name leads the detail line because it is what identifies a player inside
        /// Age of Empires - it is frequently nothing like their TadaPlay username, and matching
        /// the two up is the whole reason someone scans this list. Quoted so it reads as a
        /// handle rather than running into the address beside it.
        ///
        /// Most accounts also have a full name equal to the username, so the row used to print
        /// the same string twice - once truncated on the top line, once in full below. When they
        /// match, the repeat is dropped.
        /// </summary>
        private static (string Name, string Detail) DescribeIdentity(User user)
        {
            string username = (user.Username ?? string.Empty).Trim();
            string fullName = (user.FullName ?? string.Empty).Trim();
            string address = (user.IpAddress ?? string.Empty).Trim();
            string inGame = (user.InGameName ?? string.Empty).Trim();

            bool sameName = fullName.Length == 0
                            || string.Equals(fullName, username, StringComparison.OrdinalIgnoreCase);
            string name = sameName ? username : fullName;

            var parts = new List<string>();
            // Skip a game name that just repeats the row's own title - that is the duplication
            // this layout exists to avoid, not an extra piece of information.
            if (inGame.Length > 0 && !string.Equals(inGame, name, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"\u201c{inGame}\u201d");
            }
            if (!sameName && username.Length > 0) parts.Add(username);
            if (address.Length > 0) parts.Add(address);

            return (name, string.Join(" · ", parts));
        }

        /// <summary>
        /// Where a player belongs in the list: the ones in a match first, then everybody else.
        ///
        /// A lobby of thirty people buries the two or three who are actually playing somewhere
        /// in the middle, and those are the only rows worth clicking - they are the ones that
        /// open a match to watch. Watchable comes above merely-in-a-game so the top of the
        /// list is the part that can be acted on right now.
        /// </summary>
        private static int ActivityRank(User user)
        {
            if (user.IsWatchable) return 0;
            if (user.InGame) return 1;
            return 2;
        }

        // The second line (username / VPN address) is reference detail, not the thing being
        // scanned for, so it is smaller and grey. Both are per-item fonts, which MsgList honours
        // independently of its own Font - that one sets the NAME size and, with it, row height.
        private static readonly Font RowDetailFont = new("Segoe UI", 8.25F);
        private static readonly Font RowStatusFont = new("Segoe UI Semibold", 8.25F);
        private static readonly Color RowDetailColor = Color.FromArgb(120, 128, 134);
        private static readonly Color HostColor = Color.FromArgb(200, 120, 0);
        private static readonly Color RoomColor = Color.FromArgb(90, 120, 170);
        private static readonly Color PausedListColor = Color.FromArgb(214, 158, 46);
        /// <summary>Playing, but opted out of being watched - informational, not a warning.</summary>
        private static readonly Color NoSpecColor = Color.FromArgb(130, 138, 145);

        /// <summary>
        /// Tightens the rows. MsgList exposes no row-height, padding or spacing property - the
        /// height is derived from the control's own Font (verified: per-item fonts change the
        /// text but not the pitch; the control Font changes both). So this is the only lever,
        /// and it trades a slightly smaller name against noticeably more players on screen.
        /// Applied here rather than in the designer file, which Visual Studio rewrites.
        /// </summary>
        private void StyleUserList()
        {
            // 11pt is a measured floor, not a taste call. MsgList derives the status column's
            // width from this font and then fixes it, so at 9pt "đang chơi · 20:40" truncates to
            // "đang chơi · 20..." no matter how wide the window is - verified up to 440px with a
            // one-character name. A long player name then eats into what is left, so 10pt was
            // still cutting the longer names' clocks; 11pt clears both. This same font sets the
            // row height too, so it is as compact as the rows get with the status readable.
            userList.Font = new Font("Segoe UI Semibold", 11F);

            // The status label shares its line with the player name and comes second, so at the
            // original 33% split there was no room for it - every label, even "20:40", rendered
            // truncated. Widening the column is what makes a full status possible; the right
            // pane keeps enough width for the log and the start button.
            homeGridPanel.Span = "42% 58%";
        }

        private void UpdateOnlineUsersListView(IReadOnlyList<User> users)
        {
            userList.Items.Clear();
            // Alphabetical within each group rather than the order the server happened to send:
            // the list is rebuilt from scratch on every broadcast, so without a total order the
            // rows shuffle under the cursor every few seconds.
            var ordered = users.OrderBy(ActivityRank)
                               .ThenBy(u => u.Username ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                               .ToList();
            foreach (var user in ordered)
            {
                AntdUI.Chat.MsgItem userItem = new AntdUI.Chat.MsgItem();
                string status = user.Status ?? "";
                (string name, string detail) = DescribeIdentity(user);

                userItem.Icon = Properties.Resources.user_icon;
                userItem.Text = detail;
                userItem.TextFont = RowDetailFont;
                userItem.TextColor = RowDetailColor;
                userItem.TimeFont = RowStatusFont;
                userItem.Name = name;
                // What a player is DOING beats what the lobby calls them. The server's own
                // status stays "online" for the whole of a match, so on its own this column
                // never moved - the one thing anybody actually watches it for.
                (string label, Color colour) = DescribeActivity(user, status);
                userItem.Time = label;
                userItem.TimeColor = colour;
                // Clicking a row opens their live status, so the row has to carry the player
                // it was built from - the label alone cannot be turned back into a VPN address.
                userItem.Tag = user;

                userList.Items.Add(userItem);
            }
        }

        // Asked once per run. A player who says no should not be nagged every time the lobby
        // list refreshes, and a second prompt cannot arrive while the first is still open.
        private bool _updatePrompted;

        /// <summary>
        /// Offers a newer client when one exists, and installs it in place if the player agrees.
        ///
        /// Separate from the login gate on purpose. The gate is about builds too old to WORK -
        /// it refuses them. This is about builds that work but are behind, which is most of
        /// them most of the time, and the only way they ever caught up before was somebody
        /// noticing a message and re-downloading by hand.
        ///
        /// Never forced: it asks, and it asks once. Restarting the app underneath someone who
        /// is mid-match to save them a click would be a bad trade.
        /// </summary>
        private async System.Threading.Tasks.Task OfferUpdateIfAvailableAsync()
        {
            if (_updatePrompted) return;

            try
            {
                UpdateService.UpdateInfo info = await UpdateService.CheckAsync();
                if (!UpdateService.UpdateAvailable(info)) return;

                // A match in progress outranks an optional update - the record watcher is
                // capturing and other players may be watching. It will be offered next run.
                if (MatchShareState.InGame)
                {
                    DebugLogger.Info($"Home: update {info.LatestVersion} available; deferred, a match is running.");
                    return;
                }

                if (_updatePrompted) return;
                _updatePrompted = true;

                UiUtils.InvokeOnUiThread(this, () =>
                {
                    var config = new AntdUI.Modal.Config(mainForm, "Có bản cập nhật mới",
                        $"Phiên bản {info.LatestVersion} đã sẵn sàng (bạn đang dùng {UpdateService.CurrentVersion}).\n\n" +
                        "TADA Play sẽ tự tải và cài đặt, rồi mở lại. Quá trình này mất khoảng một phút.",
                        AntdUI.TType.Info)
                    {
                        OkText = "Cập nhật ngay",
                        CancelText = "Để sau"
                    };

                    if (AntdUI.Modal.open(config) == DialogResult.OK)
                    {
                        _ = RunUpdateAsync(info);
                    }
                }, "HOME_UPDATE_PROMPT");
            }
            catch (Exception ex)
            {
                // An update check must never be able to stop the app working.
                DebugLogger.Warn($"Home: update check failed: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task RunUpdateAsync(UpdateService.UpdateInfo info)
        {
            printLog($"[Cập nhật] Đang tải bản {info.LatestVersion}...", Color.RoyalBlue);
            try
            {
                string installer = await UpdateService.DownloadAsync(info, message =>
                    UiUtils.InvokeOnUiThread(this, () => printLog($"[Cập nhật] {message}", Color.RoyalBlue),
                                             "HOME_UPDATE_PROGRESS"));

                if (installer == null)
                {
                    printLog("[Cập nhật] Không tải được bản mới - sẽ thử lại lần sau.", Color.Orange);
                    return;
                }

                if (!UpdateService.StartInstaller(installer))
                {
                    printLog("[Cập nhật] Không mở được trình cài đặt - vui lòng tải thủ công.", Color.Orange);
                    return;
                }

                printLog("[Cập nhật] Đang cài đặt, TADA Play sẽ tự mở lại...", Color.DarkGreen);
                // The installer replaces files this process is running from, so it cannot finish
                // while we hold them open. /RELAUNCH brings the app back afterwards.
                Application.Exit();
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Home: update failed: {ex.Message}");
                printLog($"[Cập nhật] Lỗi khi cập nhật: {ex.Message}", Color.Red);
            }
        }

        /// <summary>
        /// Turns a control in the identity block into a way into <see cref="AccountInfo"/>.
        /// </summary>
        private void MakeAccountLink(Control control)
        {
            control.Cursor = Cursors.Hand;
            _accountLinkTip.SetToolTip(control, "Xem và sửa thông tin tài khoản");
            control.Click += (s, e) => OpenAccountInfo();

            // Only the name changes colour: the avatar draws itself, so recolouring its
            // ForeColor would do nothing visible.
            if (control is Label label)
            {
                Color resting = label.ForeColor;
                label.MouseEnter += (s, e) => label.ForeColor = AccountLinkHover;
                label.MouseLeave += (s, e) => label.ForeColor = resting;
            }
        }

        private void OpenAccountInfo()
        {
            var account = new AccountInfo(mainForm, appContext, accountService);
            AntdUI.Modal.open(mainForm, "Thông tin tài khoản", account);
        }

        private void UpdateUiBasedOnLobbyState()
        {
            userAvatar.Badge = "A+";
            usernameLabel.Text = appContext.GetCurrentUser()?.Username ?? "Chưa đăng nhập";
            rankLabel.Text = appContext.GetCurrentUser()?.Ranking ?? "Chưa có hạng";
        }

        // logoutButton stays enabled through the confirm dialog and the async logout that
        // follows it (MainForm.Home_LogoutRequested) unless guarded here - a user re-clicking
        // it while that's in flight got a second stacked confirm dialog and a redundant logout
        // attempt, which read as "have to click a few times to log out".
        private bool _logoutInProgress;

        private void logoutButton_Click(object sender, EventArgs e)
        {
            if (_logoutInProgress) return;

            AntdUI.Modal.open(new AntdUI.Modal.Config(mainForm, "Đăng xuất", "Bạn chắc chắn muốn thoát tài khoản?", AntdUI.TType.Warn)
            {
                OnOk = config =>
                {
                    _logoutInProgress = true;
                    logoutButton.Enabled = false;
                    logoutButton.Loading = true;
                    LogoutRequested?.Invoke(this, EventArgs.Empty);
                    return true;
                }
            });
        }

        // Called by MainForm if the logout attempt failed, so the button becomes usable again -
        // on success Home gets swapped out for the login screen and this never runs.
        public void ResetLogoutButtonState()
        {
            _logoutInProgress = false;
            logoutButton.Enabled = true;
            logoutButton.Loading = false;
        }

        // --- Start Game: sync in-game name, then launch. The record folder is already
        // being watched continuously in the background (started at app load), so a match
        // still gets picked up and uploaded even if this button is never clicked. ---
        private async void startGameButton_Click(object sender, EventArgs e)
        {
            // No game installed yet -> the button acts as "Tải game": open the downloader (no VPN
            // needed). Once it's installed, the button flips back to "Bắt đầu".
            if (!IsGameInstalled())
            {
                OpenGameDownload();
                return;
            }

            if (!wireGuardVpnService.IsConnected && _externalVpnIp == null) return;

            User currentUser = appContext.GetCurrentUser();

            // Copying the ~3MB launcher exe and starting it can take a noticeable moment
            // (antivirus scanning a freshly-written exe, ShellExecute reputation checks, etc.).
            // Use a dedicated LongRunning thread rather than Task.Run/the shared ThreadPool -
            // the WebSocket ping timer's callback is also ThreadPool-scheduled, and this
            // synchronous blocking I/O was starving it long enough to spike reported ping.
            startGameButton.Enabled = false;
            try
            {
                // Report the player's current in-game name (read, not written) before launching, so
                // the server has their latest identity for the match they're about to play. Fire and
                // forget - it must not delay or block starting the game.
                _ = ReportInGameNameAsync();

                await System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    LaunchGame();
                }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
            }
            finally
            {
                UpdateStartButtonState();
            }
        }

        private void LaunchGame()
        {
            // Nothing here touches the game's spectator settings any more. Viewers watch through
            // the replay stream, which needs neither "Allow Spectators" nor an inbound port - so
            // rewriting the player's game config on every launch, and telling them it is how
            // others watch them, was both pointless and untrue.

            string exePath = GameExecutablePreparer.PrepareAndGetExePath(appContext.GetGameFolder(), appContext.GetGameLaunchMode());
            var (status, message) = GameLauncher.Launch(exePath);
            Color color = status switch
            {
                GameLauncher.LaunchStatus.Success => Color.DarkGreen,
                GameLauncher.LaunchStatus.NotConfigured => Color.Orange,
                _ => Color.Red
            };
            printLog($"[Bắt đầu] {message}", color);
        }

        private void RecordWatchTimer_Tick(object sender, EventArgs e)
        {
            if (_uploadInProgress) return; // let the in-flight upload finish first

            string gameFolder = appContext.GetGameFolder();
            if (string.IsNullOrWhiteSpace(gameFolder)) return;

            if (_watchedRecordPath == null)
            {
                // Off the UI thread. FindLatestRecord walks the whole game folder recursively,
                // twice (once per record extension), and stats every match to find the newest.
                // On a plain install that is tens of milliseconds; on a big Voobly tree, a slow
                // disk, or with antivirus inspecting every open, it is a great deal more - and
                // running it on a Windows.Forms.Timer meant all of it blocked the message pump
                // every 5 seconds. This app has been bitten by synchronous I/O on a shared
                // thread before: see the note in startGameButton_Click about it starving the
                // WebSocket ping timer and spiking reported ping.
                //
                // Only reached while no match is being tracked. Once a record is found the tick
                // costs a single FileInfo on a known path, which is why this scan is not what
                // players feel during a game.
                if (_recordScanBusy) return;
                _recordScanBusy = true;
                DateTime cutoff = _watchCutoffUtc;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        string found = RecordedGameFinder.FindLatestRecord(gameFolder, cutoff);
                        if (found == null) return; // nothing new yet - keep waiting indefinitely

                        UiUtils.InvokeOnUiThread(this, () =>
                        {
                            // A scan started before the last one landed could double-report.
                            if (_watchedRecordPath == null) OnNewRecordDetected(found);
                        }, "HOME_RECORD_FOUND");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Warn($"Home: record scan failed: {ex.Message}");
                    }
                    finally
                    {
                        _recordScanBusy = false;
                    }
                });
                return;
            }

            var current = new FileInfo(_watchedRecordPath);
            if (!current.Exists)
            {
                _watchedRecordPath = null; // keep watching for a new one
                return;
            }

            RecordWatchTick(current);
        }

        // Set on the UI thread, cleared on the scan's thread - volatile so the tick actually
        // sees the clear rather than a cached true and never scanning again.
        private volatile bool _recordScanBusy;

        /// <summary>
        /// A new recorded game has appeared: start tracking it and tell the lobby a match has
        /// begun. Always on the UI thread - the scan that finds it is not.
        /// </summary>
        private void OnNewRecordDetected(string latest)
        {
            _watchedRecordPath = latest;
            _watchedRecordStartedUtc = DateTime.UtcNow;
            var fi = new FileInfo(latest);
            _lastKnownLength = fi.Length;
            _lastKnownWriteTimeUtc = fi.LastWriteTimeUtc;
            _stableTicks = 0;
            printLog($"[Theo dõi] Đã phát hiện file record mới: {fi.Name}", Color.DarkGreen);

            // From here others can see a match is running, and how long until it becomes
            // watchable - a countdown rather than "nothing to watch". The capture state is
            // per match, and the first-capture delay is measured from this moment.
            _liveCaptureCount = 0;
            _liveCaptureBytes = 0;
            _liveCaptureSourceLength = 0;
            _lastCaptureUtc = DateTime.UtcNow;
            // Back to the first-capture wait before announcing the match: the previous
            // match left this at its own last cadence, which for a watched game is 30s -
            // and the countdown viewers see here is the 90s one.
            MatchShareState.CaptureInterval = TimeSpan.FromMilliseconds(FirstCaptureDelayMs);
            MatchShareState.MatchStarted(latest);
            MatchStatusPublisher.Publish(webSocketService, appContext.GetAllowSpectateSetting());
            printLog($"[Xem] Đã bắt đầu game - chờ {MatchShareState.WaitSeconds} giây để " +
                     "người khác có thể xem.", Color.RoyalBlue);
        }

        /// <summary>
        /// The cheap per-tick path once a match is being tracked: has the record stopped
        /// growing, and has the game let go of it yet.
        /// </summary>
        private void RecordWatchTick(FileInfo current)
        {
            TimeSpan elapsed = DateTime.UtcNow - (_watchedRecordStartedUtc ?? DateTime.UtcNow);
            if (elapsed.TotalMinutes >= GiveUpWatchingFileAfterMinutes)
            {
                printLog($"[Theo dõi] File '{current.Name}' không ổn định sau {GiveUpWatchingFileAfterMinutes} phút - " +
                         "bỏ qua và tiếp tục theo dõi file mới. Có thể tải lên thủ công.", Color.Orange);
                _watchCutoffUtc = DateTime.UtcNow; // don't pick this same stuck file back up
                _watchedRecordPath = null;
                return;
            }

            if (current.Length == _lastKnownLength && current.LastWriteTimeUtc == _lastKnownWriteTimeUtc)
            {
                _stableTicks++;
                if (_stableTicks >= StableTicksToFinish)
                {
                    // "Stopped growing" is not the same as "match over": a paused game also
                    // stops writing. While the game owns the file it holds it without
                    // FILE_SHARE_READ, so uploading now would throw a sharing violation and
                    // burn the record. The lock dropping is the reliable end-of-match signal.
                    if (LiveRecordReader.IsLockedByGame(_watchedRecordPath))
                    {
                        _stableTicks = StableTicksToFinish; // keep waiting, don't re-announce
                        return;
                    }

                    printLog("[Theo dõi] Trận đấu có vẻ đã kết thúc, đang tải lên record...", Color.Orange);
                    string finishedRecordPath = _watchedRecordPath;
                    _watchCutoffUtc = DateTime.UtcNow; // don't re-detect this same file next tick
                    _watchedRecordPath = null;

                    // Share it before uploading, so the match is watchable even if the upload
                    // fails. The lock has just dropped, so the game is finished with the file.
                    MatchShareState.MatchEnded();
                    // Stop the real-time stream first: the game has closed the record (and its
                    // 53754 port), and the authoritative finished file is published next.
                    _spectatorStream?.Dispose();
                    _spectatorStream = null;
                    PublishFinishedMatch(finishedRecordPath);
                    MatchStatusPublisher.Publish(webSocketService, appContext.GetAllowSpectateSetting());

                    _ = UploadRecordAsync(isAutomatic: true, recordPathOverride: finishedRecordPath);
                }
            }
            else
            {
                _lastKnownLength = current.Length;
                _lastKnownWriteTimeUtc = current.LastWriteTimeUtc;
                _stableTicks = 0;
            }
        }

        // Live spectating is the game's own feature: while a match runs it streams to
        // spectators on TCP 53754, and age2_x1\spectate.exe connects to it. Everyone is on
        // the same WireGuard subnet, so the address is just the other player's VPN IP.
        // Delayed spectating, done through recorded games rather than UserPatch's own
        // spectator - that tool reports "Could not locate game expansion." against this
        // install and only ever launches stock age2_x1.5.exe. Instead the host publishes the
        // live snapshot TadaPlay already captures, and the viewer plays it as a normal
        // replay. The viewer is therefore always behind by at least the snapshot interval,
        // which is the property that makes it safe to allow during a ranked match.
        /// <summary>
        /// Opens one player's live status - now the only way into watching. Clicking a name
        /// asks "what is THIS person doing?", which is the question the online list itself
        /// cannot answer: being online says nothing about being in a game.
        /// </summary>
        private async void userList_ItemSelected(object sender, AntdUI.MsgItemEventArgs e)
        {
            if (e?.Item?.Tag is not User user) return;

            // Cosmetic: MsgList re-raises this on a repeat click of the same row, so clearing
            // is not needed to keep the row clickable - it just stops the player staying
            // highlighted after the dialog closes, as though something were still selected.
            ClearUserListSelection();

            bool isSelf = string.Equals(user.Username, appContext.GetCurrentUser()?.Username,
                                        StringComparison.OrdinalIgnoreCase);
            using var dialog = new UserStatusDialog(user, isSelf, LookupOnlineUser);
            if (dialog.ShowDialog(mainForm) != DialogResult.OK) return;

            if (!TryGetGameFolderForWatching(out string gameFolder)) return;
            await WatchAsync(user.IpAddress, user.Username ?? user.IpAddress, gameFolder);
        }

        /// <summary>
        /// The current record for a player, by name. Handed to the spectate dialogs so they
        /// read live broadcast data instead of the objects they were constructed with.
        /// </summary>
        private User LookupOnlineUser(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            return appContext.AllOnlineUsers?
                .FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        }

        private void ClearUserListSelection()
        {
            foreach (AntdUI.Chat.MsgItem item in userList.Items) item.Select = false;
            userList.Invalidate();
        }

        private bool TryGetGameFolderForWatching(out string gameFolder)
        {
            gameFolder = appContext.GetGameFolder();
            if (!string.IsNullOrWhiteSpace(gameFolder)) return true;

            UiUtils.ShowAntdModal(mainForm, "Chưa cấu hình",
                "Bạn cần chọn thư mục cài đặt AoE2 trong Cài đặt trước khi xem trận đấu.",
                AntdUI.TType.Warn);
            return false;
        }

        /// <summary>Downloads a host's match, launches it, and keeps following the host.</summary>
        private async System.Threading.Tasks.Task WatchAsync(string hostIp, string hostLabel,
                                                             string gameFolder)
        {
            // The spectate button used to double as the guard against starting a second
            // watch while one was still setting up. With it gone this has to guard itself.
            if (_watchInProgress) return;
            _watchInProgress = true;
            try
            {
                // Only one match can be followed at a time - the file name is per host, and a
                // second stream would keep appending to a file nobody is watching any more.
                StopLiveStream();

                // Everyone watches through the replay stream below.
                //
                // The game's own live spectator (age2_x1\spectate.exe over TCP 53754) was tried
                // here first, because in principle it streams the match as it happens and so
                // cannot be ended early by fast-forwarding. In practice it is not usable on this
                // setup, and falling back to the replay after it failed meant every viewer paid
                // a launch attempt and a confusing error line before getting the path that
                // actually works. Going straight to the replay is fewer moving parts and one
                // behaviour to reason about.
                //
                // The replay's known limitation stands: it ends at the last captured byte, so a
                // viewer who speeds up can catch the live edge. That is what the pause overlay
                // and the "tăng tốc độ trong game để đuổi kịp" hint below are for.

                printLog($"[Xem] Đang tải trận đấu của {hostLabel}...", Color.RoyalBlue);
                LiveShareClient.FetchResult fetch =
                    await LiveShareClient.TryFetchAsync(hostIp, gameFolder, hostLabel);
                if (fetch.Path == null)
                {
                    printLog($"[Xem] {fetch.Error}", Color.Red);
                    UiUtils.ShowAntdModal(mainForm, "Không xem được", fetch.Error, AntdUI.TType.Warn);
                    return;
                }

                // Only a match being played can be watched. Both entry points filter on that
                // already, but the host can quit in the seconds between picking and
                // downloading, and the served headers are the truth at that moment - so check
                // again here rather than opening the game on a match that is already over.
                if (!fetch.InGame)
                {
                    string ended = $"{hostLabel} đã thoát game - trận đã kết thúc nên không xem được.";
                    printLog($"[Xem] {ended}", Color.Orange);
                    UiUtils.ShowAntdModal(mainForm, "Trận đã kết thúc", ended, AntdUI.TType.Warn);
                    return;
                }

                string exePath = GameExecutablePreparer.PrepareAndGetExePath(
                    gameFolder, appContext.GetGameLaunchMode());
                var (status, message) = GameLauncher.Launch(exePath, fetch.Path);
                printLog($"[Xem] {message}",
                         status == GameLauncher.LaunchStatus.Success ? Color.DarkGreen : Color.Red);

                if (status != GameLauncher.LaunchStatus.Success)
                {
                    UiUtils.ShowAntdModal(mainForm, "Không mở được game", message, AntdUI.TType.Warn);
                    return;
                }

                // The host is still playing, so the download stops partway through the match.
                // Keep pulling the tail into the same file the game just opened rather than
                // making the viewer download and relaunch for every minute. The stream runs
                // on past the host quitting so the last bytes still arrive and the viewer
                // sees the match out.
                StartLiveStream(hostIp, hostLabel, fetch);
                printLog($"[Xem] Đang theo dõi trận của {hostLabel} - phần mới sẽ tự động " +
                         "được thêm vào. Tăng tốc độ trong game để đuổi kịp.", Color.RoyalBlue);
            }
            finally
            {
                _watchInProgress = false;
            }
        }

        // Follows a match that is still being played: see LiveStreamSession.
        private bool _watchInProgress;
        private LiveStreamSession _liveStream;

        /// <summary>
        /// Polls whether the game opened to watch a match is still running.
        ///
        /// Without it, closing that game did NOT stop the download: StopLiveStream was only ever
        /// reached by shutting the app down or starting a different watch, so a viewer who quit
        /// the replay kept pulling the host's record for the rest of the match - reported in the
        /// log as "đã nhận thêm N KB" long after they had stopped watching. That is wasted
        /// bandwidth on a tunnel every other player shares.
        /// </summary>
        /// <summary>Backstop for the match clock when the stream source is not producing one.</summary>
        private DateTime _lastClockFallbackUtc = DateTime.MinValue;
        private static readonly TimeSpan ClockFallbackInterval = TimeSpan.FromSeconds(15);

        private System.Windows.Forms.Timer _watchExitTimer;
        private const int WatchExitPollMs = 3000;
        private SpectatorOverlay _overlay;
        private ReplayFollower _playheadGovernor;

        private void StartLiveStream(string hostIp, string hostLabel, LiveShareClient.FetchResult fetch)
        {
            _liveStream = new LiveStreamSession(hostIp, hostLabel, fetch, (message, isProblem) =>
            {
                printLog(message, isProblem ? Color.Orange : Color.RoyalBlue);
                // A session reports its final message with IsRunning already false, so this is
                // how the overlay learns the match is over without polling for it.
                if (_liveStream is { IsRunning: false })
                {
                    CloseOverlay();
                    // Nothing left to fast-forward into once the stream is done.
                    _playheadGovernor?.Dispose();
                    _playheadGovernor = null;
                    // Spectating is over (match ended, all data received - a clean finish, not a
                    // connection problem), so close the game that was opened just to watch it,
                    // instead of leaving the viewer to alt-tab and quit the replay by hand.
                    if (!isProblem) _ = AutoCloseSpectatorGameAsync();
                }
            });
            _liveStream.Start();
            StartWatchExitWatchdog();

            // The host's clock is in TadaPlay's dialogs, but the game is about to cover them -
            // and that is precisely when a viewer needs it, to judge how far behind live they
            // are. So it also floats above the game.
            ShowOverlay(hostIp, hostLabel);

            // Keeps the viewer's replay in step with the match: pauses it when the host pauses,
            // resumes it when they play on, and puts the speed back to normal once playback is
            // within 30s of the host's own clock. Nothing else - fast-forwarding is the viewer's
            // to do by hand.
            //
            // The host's state comes from a lookup in the lobby's existing online-user list, so
            // there is no extra traffic to the host, which matters because it is asked every 600ms.
            _playheadGovernor?.Dispose();
            _playheadGovernor = new ReplayFollower(
                fetch.Path,
                msg => printLog(msg, Color.RoyalBlue),
                () => LiveShareClient.FromBroadcast(LookupOnlineUser(hostLabel)),
                appContext.GetGameFolder());
            _playheadGovernor.Start();
        }

        /// <summary>
        /// Closes the game opened for spectating once the watched match is over. A short grace
        /// period first, so the viewer sees the final moments / score screen rather than the
        /// game vanishing the instant the last byte arrives - and if they have meanwhile picked
        /// a new match to watch, that new game is left alone.
        /// </summary>
        private async System.Threading.Tasks.Task AutoCloseSpectatorGameAsync()
        {
            try { await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8)); }
            catch { /* app closing */ }

            // A new watch started during the grace period owns the game now - leave it running.
            if (_liveStream is { IsRunning: true }) return;

            try
            {
                var game = LiveRecordReader.FindGameProcess();
                if (game == null) return;
                int pid = game.Id;
                try { if (!game.HasExited) { game.Kill(); game.WaitForExit(3000); } }
                finally { game.Dispose(); }
                DebugLogger.Info($"Home: auto-closed spectator game (PID {pid}) after the match ended.");
                printLog("[Xem] Trận đã kết thúc - đã tự động đóng game.", Color.RoyalBlue);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"Home: could not auto-close spectator game: {ex.Message}");
            }
        }

        private void ShowOverlay(string hostIp, string hostLabel)
        {
            CloseOverlay();
            try
            {
                _overlay = new SpectatorOverlay(hostIp, hostLabel, LookupOnlineUser);
                // Owner-less on purpose: an overlay owned by the main window is hidden with it
                // when the player minimises TadaPlay to get at the game. And shown without
                // activation, so it never pulls the player out of the game it sits over.
                _overlay.ShowNoActivate();
                printLog("[Xem] Đồng hồ trận đấu hiện ở trên cùng màn hình - kéo để di chuyển, " +
                         "chuột phải để tắt.", Color.RoyalBlue);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"Home: cannot show the spectator overlay: {ex.Message}");
            }
        }

        private void CloseOverlay()
        {
            SpectatorOverlay overlay = _overlay;
            _overlay = null;
            if (overlay == null || overlay.IsDisposed) return;
            try
            {
                // Called from the stream's background loop as well as from the UI thread, and a
                // Form may only be closed on the thread that created it.
                if (overlay.InvokeRequired)
                {
                    overlay.BeginInvoke(new Action(() =>
                    {
                        try { overlay.Close(); overlay.Dispose(); }
                        catch (Exception ex) { DebugLogger.Warn($"Home: overlay close failed: {ex.Message}"); }
                    }));
                    return;
                }
                overlay.Close();
                overlay.Dispose();
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"Home: cannot close the spectator overlay: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the download once the game being watched in has gone.
        ///
        /// Quitting that game IS "I have stopped watching" - there is no other gesture for it,
        /// and the app cannot otherwise tell. Polled rather than hooked to Exited because the
        /// process is found by scanning (GameLauncher does not hand one back), and a 3s poll is
        /// far cheaper than the download it stops.
        /// </summary>
        private void StartWatchExitWatchdog()
        {
            StopWatchExitWatchdog();

            // Give the game generous time to appear before concluding it has gone. It is
            // launched just before this and the launch already reported success, so it is
            // coming - but GameExecutablePreparer copies a ~3MB exe first and antivirus
            // scanning a freshly written binary can stretch that well past a few seconds.
            // Erring long costs one extra minute of download in a case that should not happen;
            // erring short kills the stream out from under someone who is still watching.
            var startedAt = DateTime.UtcNow;
            var grace = TimeSpan.FromSeconds(90);
            bool seen = false;

            _watchExitTimer = new System.Windows.Forms.Timer { Interval = WatchExitPollMs };
            _watchExitTimer.Tick += (s, e) =>
            {
                if (_liveStream is not { IsRunning: true })
                {
                    StopWatchExitWatchdog();
                    return;
                }

                System.Diagnostics.Process game = null;
                try { game = LiveRecordReader.FindGameProcess(); }
                catch (Exception ex) { DebugLogger.Warn($"Home: watch watchdog probe failed: {ex.Message}"); }

                try
                {
                    if (game != null)
                    {
                        seen = true;
                        return;
                    }

                    // Never seen it and still inside the grace period: it may still be starting.
                    if (!seen && DateTime.UtcNow - startedAt < grace) return;

                    DebugLogger.Info("Home: the game opened for watching has exited - stopping the stream.");
                    printLog("[Xem] Đã đóng game - dừng tải trận đang xem.", Color.RoyalBlue);
                    StopLiveStream();
                }
                finally
                {
                    game?.Dispose();
                }
            };
            _watchExitTimer.Start();
        }

        private void StopWatchExitWatchdog()
        {
            var timer = _watchExitTimer;
            _watchExitTimer = null;
            if (timer == null) return;
            timer.Stop();
            timer.Dispose();
        }

        private void StopLiveStream()
        {
            StopWatchExitWatchdog();
            LiveStreamSession session = _liveStream;
            _liveStream = null;
            session?.Dispose();
            _playheadGovernor?.Dispose();
            _playheadGovernor = null;
            CloseOverlay();
        }

        /// <summary>
        /// Locates the recorded game and uploads it (with metadata) to the server.
        /// Safe to call from any thread. Pass <paramref name="recordPathOverride"/> when the
        /// caller already knows which file just finished (the background watcher); otherwise
        /// falls back to whatever's newest under the configured game folder.
        /// </summary>
        private async System.Threading.Tasks.Task UploadRecordAsync(bool isAutomatic, string recordPathOverride = null)
        {
            if (_uploadInProgress)
            {
                printLog("[Record] Đang tải lên, vui lòng đợi...", Color.Orange);
                return;
            }

            _uploadInProgress = true;
            UiUtils.InvokeOnUiThread(this, UpdateStartButtonState);

            try
            {
                string gameFolder = appContext.GetGameFolder();
                if (string.IsNullOrWhiteSpace(gameFolder))
                {
                    string cfgMsg = "Chưa cấu hình thư mục game. Vào Cài đặt để chọn thư mục cài đặt AoE2.";
                    printLog($"[Record] {cfgMsg}", Color.Red);
                    if (!isAutomatic)
                    {
                        UiUtils.ShowAntdModal(mainForm, "Chưa cấu hình", cfgMsg, AntdUI.TType.Warn);
                    }
                    return;
                }

                string recordPath = recordPathOverride ?? RecordedGameFinder.FindLatestRecord(gameFolder);
                if (string.IsNullOrEmpty(recordPath))
                {
                    string msg = "Không tìm thấy file record (.mgz) của trận đấu trong thư mục game. " +
                                 "Hãy chắc chắn rằng bạn đã chơi xong và game đã lưu lại trận đấu.";
                    printLog($"[Record] {msg}", Color.Red);
                    if (!isAutomatic)
                    {
                        UiUtils.ShowAntdModal(mainForm, "Không tìm thấy record", msg, AntdUI.TType.Warn);
                    }
                    return;
                }

                // The record on disk is not automatically the best copy of the match: the game
                // only patches its header-length field on a clean exit, and the file can be
                // deleted or truncated between the last write and this upload. Whichever of
                // the file and the live snapshot holds more of the match wins, with the header
                // repaired if needed. See LiveRecordSnapshotStore.ResolveUploadSource.
                string uploadPath = LiveRecordSnapshotStore.ResolveUploadSource(recordPath, out string sourceNote);
                if (!string.IsNullOrEmpty(sourceNote))
                {
                    printLog($"[Record] {sourceNote}", Color.Orange);
                }

                User currentUser = appContext.GetCurrentUser();
                var metadata = new GameRecordMetadata
                {
                    RoomId = null,
                    RoomName = null,
                    HostUsername = currentUser?.Username,
                    UploadedBy = currentUser?.Username,
                    Players = appContext.AllOnlineUsers.Select(u => u.Username).ToArray(),
                    FinishedAt = DateTime.UtcNow,
                    RecordFileName = Path.GetFileName(recordPath),
                    ClientVersion = Application.ProductVersion
                };

                printLog($"[Record] Đang tải lên '{metadata.RecordFileName}'...", Color.RoyalBlue);

                GameRecordUploadResponse response = await accountService.UploadGameRecordAsync(metadata, uploadPath);
                printLog("[Record] Tải lên record thành công.", Color.DarkGreen);

                // The shared copy deliberately stays after upload - that is what other players
                // watch. Prune() clears it out after a week.

                GameRecordInfo info = response.Record;

                if (info != null && info.Duplicate)
                {
                    printLog("[Record] Trận đấu này đã được người chơi khác tải lên trước đó.", Color.RoyalBlue);
                    return;
                }

                if (info != null && !string.IsNullOrEmpty(info.Mvp))
                {
                    printLog($"[Record] MVP của trận đấu: ⭐ {info.Mvp}", Color.DarkGoldenrod);
                }

                if (info != null && info.CanReport && info.Teams != null)
                {
                    UiUtils.InvokeOnUiThread(this, () => PromptReportWinner(info));
                }
                else if (info != null && info.Status == "needs_review")
                {
                    printLog($"[Record] Không thể xác định đội chơi từ record nên ELO sẽ không được tính tự động." +
                             (string.IsNullOrEmpty(info.ReviewReason) ? "" : $" ({info.ReviewReason})"), Color.Orange);
                    if (!isAutomatic)
                    {
                        UiUtils.ShowAntdModal(mainForm, "Đã tải lên",
                            "Đã tải lên record, nhưng không thể tự động xác định đội chơi để tính ELO.", AntdUI.TType.Warn);
                    }
                }
                else if (!isAutomatic)
                {
                    UiUtils.ShowAntdModal(mainForm, "Thành công", "Đã tải lên record của trận đấu.", AntdUI.TType.Success);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Home: Upload record thất bại: {ex.Message}");
                printLog($"[Record] Lỗi tải lên record: {ex.Message}", Color.Red);
                if (!isAutomatic)
                {
                    UiUtils.ShowAntdModal(mainForm, "Lỗi", ex.Message, AntdUI.TType.Error);
                }
            }
            finally
            {
                _uploadInProgress = false;
                UiUtils.InvokeOnUiThread(this, UpdateStartButtonState);
            }
        }

        /// <summary>
        /// Shows the parsed teams and lets whoever uploaded the record report the winner,
        /// which triggers the server-side 4v4 ELO update.
        /// </summary>
        private void PromptReportWinner(GameRecordInfo info)
        {
            if (info?.Teams == null) return;

            info.Teams.TryGetValue("1", out var team1);
            info.Teams.TryGetValue("2", out var team2);

            using var dialog = new ReportWinnerDialog(team1 ?? Array.Empty<string>(), team2 ?? Array.Empty<string>(), info.SuggestedWinnerTeam);
            if (dialog.ShowDialog(mainForm) == DialogResult.OK && dialog.WinningTeam.HasValue)
            {
                _ = ReportWinnerAsync(info.Id, dialog.WinningTeam.Value);
            }
            else
            {
                printLog("[Kết quả] Chưa báo kết quả. Bạn có thể tải lên lại record để báo kết quả sau.", Color.Orange);
            }
        }

        private async System.Threading.Tasks.Task ReportWinnerAsync(long recordId, int winningTeam)
        {
            try
            {
                printLog($"[Kết quả] Đang gửi kết quả (Đội {winningTeam} thắng)...", Color.RoyalBlue);
                ReportResultResponse result = await accountService.ReportGameResultAsync(recordId, winningTeam);

                printLog($"[Kết quả] Đội {result.WinningTeam} thắng. ELO đã được cập nhật:", Color.DarkGreen);
                if (!string.IsNullOrEmpty(result.WinnerMvp))
                {
                    printLog($"    MVP đội thắng: ⭐ {result.WinnerMvp} (+{result.Ratings?.FirstOrDefault(r => r.MvpRole == "winner")?.MvpDelta})", Color.DarkGoldenrod);
                }
                if (!string.IsNullOrEmpty(result.LoserMvp))
                {
                    printLog($"    MVP đội thua: ⭐ {result.LoserMvp} (+{result.Ratings?.FirstOrDefault(r => r.MvpRole == "loser")?.MvpDelta})", Color.DarkGoldenrod);
                }
                if (result.Ratings != null)
                {
                    foreach (var change in result.Ratings)
                    {
                        string sign = change.Delta >= 0 ? "+" : "";
                        string perf = change.PerfDelta != 0 ? $", điểm {(change.PerfDelta > 0 ? "+" : "")}{change.PerfDelta}" : "";
                        string mvp = change.MvpDelta != 0 ? $", MVP +{change.MvpDelta} ⭐" : "";
                        printLog($"    {change.Username}: {change.Old} → {change.NewRating} ({sign}{change.Delta})  [trận {(change.BaseDelta > 0 ? "+" : "")}{change.BaseDelta}{perf}{mvp}]",
                            change.Delta >= 0 ? Color.DarkGreen : Color.Firebrick);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"Home: Báo kết quả thất bại: {ex.Message}");
                printLog($"[Kết quả] Lỗi báo kết quả: {ex.Message}", Color.Red);
                UiUtils.ShowAntdModal(mainForm, "Lỗi", ex.Message, AntdUI.TType.Error);
            }
        }

        private void printLog(string logMessage, Color textColor)
        {
            UiUtils.InvokeOnUiThread(this, () =>
            {
                if (this.IsDisposed || logRichTextBox.IsDisposed) return;
                logRichTextBox.SelectionStart = logRichTextBox.TextLength;
                logRichTextBox.SelectionLength = 0;
                logRichTextBox.SelectionColor = textColor;
                logRichTextBox.AppendText(logMessage + Environment.NewLine);
                logRichTextBox.SelectionColor = logRichTextBox.ForeColor;

                logRichTextBox.SelectionStart = logRichTextBox.TextLength;
                logRichTextBox.ScrollToCaret();

                if (logRichTextBox.Lines.Length > 500)
                {
                    logRichTextBox.Text = string.Join(Environment.NewLine, logRichTextBox.Lines.Skip(logRichTextBox.Lines.Length - 500));
                }
            }, "HOME_LOG");
        }

        private void vpnStatusLabel_Click(object sender, EventArgs e)
        {

        }
    }
}
