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
                    printLog("[Theo dõi] Trận đấu có vẻ đã kết thúc, đang tải lên record...", Color.Orange);
                    string finishedRecordPath = _watchedRecordPath;
                    _watchCutoffUtc = DateTime.UtcNow; // don't re-detect this same file next tick
                    _watchedRecordPath = null;
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

                GameRecordUploadResponse response = await accountService.UploadGameRecordAsync(metadata, recordPath);
                printLog("[Record] Tải lên record thành công.", Color.DarkGreen);

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
    }
}
