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

            uploadRecordButton.Click += uploadRecordButton_Click;
            spectateButton.Click += spectateButton_Click;
            userList.ItemSelected += userList_ItemSelected;

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
                _liveShareServer?.Dispose();
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
        #endregion

        private void Home_Load(object sender, EventArgs e)
        {
            webSocketService.OnConnected += WebSocketService_OnConnected!;
            webSocketService.OnDisconnected += WebSocketService_OnDisconnected!;
            webSocketService.OnErrorOccurred += WebSocketService_OnErrorOccurred!;
            webSocketService.OnPingUpdate += WebSocketService_OnPingUpdate!;

            appContext.OnCurrentUserUpdated += AppContext_OnCurrentUserUpdated;
            appContext.OnOnlineUsersUpdated += AppContext_OnOnlineUsersUpdated;

            wireGuardVpnService.OnConnected += WireGuardVpnService_OnConnected;
            wireGuardVpnService.OnDisconnected += WireGuardVpnService_OnDisconnected;
            wireGuardVpnService.OnErrorOccurred += WireGuardVpnService_OnErrorOccurred;
            wireGuardVpnService.OnIpAddressChanged += WireGuardVpnService_OnIpAddressChanged;

            UpdateUiBasedOnLobbyState();
            StartRecordWatcher();
            StartMatchSharing();
            // A spectator session swaps the stock age2_x1.5.exe aside; if TadaPlay died before
            // putting it back, do it now rather than leaving the game folder modified.
            GameSpectator.RestoreLaunchTarget(appContext.GetGameFolder());
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
                "Bạn cần chọn thư mục cài đặt AoE2 (nơi chứa SaveGame) trong Cài đặt để TadaPlay có thể đồng bộ tên, theo dõi và tải lên record trận đấu.",
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
        // also the countdown a viewer is shown. After that the cadence follows demand - 10
        // seconds while someone is watching, so the replay keeps moving instead of arriving in
        // 90-second lumps, and back to 90 when nobody is, so an unwatched game is not paying
        // for shadow copies nobody reads.
        //
        // 10 seconds is a floor, not a promise: a shadow copy takes a couple of seconds, and
        // while one is running the next tick is skipped rather than queued, so the real gap is
        // 10 seconds plus however long the capture took. The capture duration is logged.
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
        private const int WatchedCaptureIntervalMs = 10 * 1000;    // somebody is watching
        private const int IdleCaptureIntervalMs = 90 * 1000;       // nobody is
        private bool _liveCaptureBusy;
        private string _liveCaptureMatch;   // record the current live captures belong to
        private int _liveCaptureCount;      // captures of the current match, for the log
        private long _liveCaptureBytes;     // snapshot size at the previous capture, to report growth
        private long _liveCaptureSourceLength; // record size at the previous capture, to detect growth
        private DateTime _lastCaptureUtc = DateTime.MinValue;

        private void LiveShareTimer_Tick(object sender, EventArgs e)
        {
            if (IsDisposed) return;

            // Before the capture, and outside the busy check: the event worth reporting - the
            // last viewer leaving - happens when requests STOP, so nothing else would notice.
            LiveShareServer.SweepWatchers();

            if (_liveCaptureBusy || !ShouldCaptureNow()) return;
            _liveCaptureBusy = true;
            _ = System.Threading.Tasks.Task.Run(CaptureLiveMatch);
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
            _liveCaptureCount == 0
                ? FirstCaptureDelayMs
                : (LiveShareServer.CurrentWatchers().Count > 0
                    ? WatchedCaptureIntervalMs
                    : IdleCaptureIntervalMs);

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
                MatchShareState.DurationMs = analysis.DurationMs;
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
                MatchStatusPublisher.Publish(webSocketService);
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
        private void StartMatchSharing()
        {
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
                MatchStatusPublisher.Publish(webSocketService, force: true);
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

        private void WebSocketService_OnPingUpdate(object sender, PingUpdateEventArgs e)
        {
            UiUtils.InvokeOnUiThread(this, () =>
            {
                pingLabel.Text = $"Ping: {e.PingMs}ms";
                pingLabel.ForeColor = e.IsHighPing ? Color.Firebrick : Color.DarkGreen;
                if (e.IsHighPing)
                {
                    printLog($"[Cảnh báo] Ping cao ({e.PingMs}ms) - kiểm tra kết nối VPN/mạng.", Color.Orange);
                }
            }, "HOME_PING");
        }

        private void UpdateOnlineUsersListView(IReadOnlyList<User> users)
        {
            userList.Items.Clear();
            foreach (var user in users)
            {
                AntdUI.Chat.MsgItem userItem = new AntdUI.Chat.MsgItem();
                string username = user.FullName ?? user.Username;
                string nickname = user.Username;
                string status = user.Status ?? "";

                userItem.Icon = Properties.Resources.user_icon;
                userItem.Text = string.IsNullOrWhiteSpace(user.IpAddress) ? nickname : $"{nickname} · {user.IpAddress}";
                userItem.Name = username;
                userItem.Time = status;
                if (status == "online") userItem.TimeColor = Color.Green;
                // Clicking a row opens their live status, so the row has to carry the player
                // it was built from - the label alone cannot be turned back into a VPN address.
                userItem.Tag = user;

                userList.Items.Add(userItem);
            }
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
            // Make the lobby's "Allow Spectators" box start ticked. The game always listens
            // on the spectator port during a match, but a host who never ticks that box
            // cannot actually be watched - so set it before the game reads its settings.
            if (GameSpectator.EnsureSpectatorsAllowedByDefault())
            {
                printLog("[Xem] Đã bật sẵn 'Allow Spectators' để người khác có thể xem trận của bạn.",
                         Color.RoyalBlue);
            }

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
                string latest = RecordedGameFinder.FindLatestRecord(gameFolder, _watchCutoffUtc);
                if (latest == null) return; // nothing new yet - keep waiting indefinitely

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
                // match left this at its own last cadence, which for a watched game is 10s -
                // and the countdown viewers see here is the 90s one.
                MatchShareState.CaptureInterval = TimeSpan.FromMilliseconds(FirstCaptureDelayMs);
                MatchShareState.MatchStarted(latest);
                MatchStatusPublisher.Publish(webSocketService);
                printLog($"[Xem] Đã bắt đầu game - chờ {MatchShareState.WaitSeconds} giây để " +
                         "người khác có thể xem.", Color.RoyalBlue);
                return;
            }

            var current = new FileInfo(_watchedRecordPath);
            if (!current.Exists)
            {
                _watchedRecordPath = null; // keep watching for a new one
                return;
            }

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
                    PublishFinishedMatch(finishedRecordPath);
                    MatchStatusPublisher.Publish(webSocketService);

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
        private async void spectateButton_Click(object sender, EventArgs e)
        {
            if (!TryGetGameFolderForWatching(out string gameFolder)) return;

            string hostIp, hostLabel;
            using (var picker = new SpectatePickerDialog(appContext.AllOnlineUsers,
                                                         appContext.GetCurrentUser()?.Username))
            {
                if (picker.ShowDialog(mainForm) != DialogResult.OK || picker.SelectedHostIp == null)
                {
                    return;
                }
                hostIp = picker.SelectedHostIp;
                hostLabel = picker.SelectedHostLabel ?? hostIp;
            }

            await WatchAsync(hostIp, hostLabel, gameFolder);
        }

        /// <summary>
        /// Opens one player's live status. This is the other way into watching: the spectate
        /// button asks "who can I watch?", while clicking a name asks "what is THIS person
        /// doing?" - which is the question the online list itself cannot answer, since being
        /// online says nothing about being in a game.
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
            using var dialog = new UserStatusDialog(user, isSelf);
            if (dialog.ShowDialog(mainForm) != DialogResult.OK) return;

            if (!TryGetGameFolderForWatching(out string gameFolder)) return;
            await WatchAsync(user.IpAddress, user.Username ?? user.IpAddress, gameFolder);
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
            spectateButton.Enabled = false;
            try
            {
                // Only one match can be followed at a time - the file name is per host, and a
                // second stream would keep appending to a file nobody is watching any more.
                StopLiveStream();

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
                spectateButton.Enabled = true;
            }
        }

        // Follows a match that is still being played: see LiveStreamSession.
        private LiveStreamSession _liveStream;
        private SpectatorOverlay _overlay;

        private void StartLiveStream(string hostIp, string hostLabel, LiveShareClient.FetchResult fetch)
        {
            _liveStream = new LiveStreamSession(hostIp, hostLabel, fetch, (message, isProblem) =>
            {
                printLog(message, isProblem ? Color.Orange : Color.RoyalBlue);
                // A session reports its final message with IsRunning already false, so this is
                // how the overlay learns the match is over without polling for it.
                if (_liveStream is { IsRunning: false }) CloseOverlay();
            });
            _liveStream.Start();

            // The host's clock is in TadaPlay's dialogs, but the game is about to cover them -
            // and that is precisely when a viewer needs it, to judge how far behind live they
            // are. So it also floats above the game.
            ShowOverlay(hostIp, hostLabel);
        }

        private void ShowOverlay(string hostIp, string hostLabel)
        {
            CloseOverlay();
            try
            {
                _overlay = new SpectatorOverlay(hostIp, hostLabel);
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

        private void StopLiveStream()
        {
            LiveStreamSession session = _liveStream;
            _liveStream = null;
            session?.Dispose();
            CloseOverlay();
        }

        private void uploadRecordButton_Click(object sender, EventArgs e)
        {
            _ = UploadRecordAsync(isAutomatic: false);
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
                    string cfgMsg = "Chưa cấu hình thư mục game. Vào Cài đặt để chọn thư mục cài đặt AoE2 (nơi chứa SaveGame).";
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
