using AntdUI;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TadaPlay.Common.Models;
using TadaPlay.Connections;
using TadaPlay.Connections.Interface;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Services.Interface;
using TadaPlay.Utils;
using TadaPlay.Websockets;
using TadaPlay.Websockets.Interface;

namespace TadaPlay
{
    public partial class RoomDetailForm : Window
    {
        private readonly IWebSocketService _webSocketService;
        private readonly IWireGuardVpnService _wireGuardVpnService;
        private readonly IAppContext _appContext;
        private readonly IAccountService _accountService;
        private ClientRoom _currentRoom;

        private bool _isClosingOrClosed = false;

        // Set when the game starts ("playing"); used as a cutoff so we only pick up
        // the record produced by this match. Guards against uploading the same record twice.
        private DateTime? _gameStartedAtUtc = null;
        private bool _recordUploaded = false;
        private bool _uploadInProgress = false;

        public RoomDetailForm(IWebSocketService webSocketService, IAppContext appContext, IWireGuardVpnService wireGuardVpnService, IAccountService accountService)
        {
            InitializeComponent();
            _webSocketService = webSocketService;
            _appContext = appContext;
            _wireGuardVpnService = wireGuardVpnService;
            _accountService = accountService;

            // Subscribe to AppContext events relevant to this form
            _appContext.OnCurrentRoomDetailsUpdated += AppContext_OnCurrentRoomDetailsUpdated;
            _appContext.OnOnlineUsersUpdated += AppContext_OnOnlineUsersUpdated; // For user status within room

            _webSocketService.OnConnectionStatusChanged += WebSocketService_OnConnectionStatusChanged;
            _webSocketService.OnErrorOccurred += WebSocketService_OnErrorOccurred;
            // No need to subscribe to OnMessageReceived from WebSocketService here, AppContext processes it.

            userListView.Columns.Add("user_name", "Tên");
            userListView.Columns.Add("ping", "Ping");
            userListView.Columns.Add("status", "Trạng thái");
            userListView.View = View.Details;
            userListView.FullRowSelect = true;

            resizeColumns();

            // Hook up button click events
            this.kickUserButton.Click += kickUserButton_Click;
            this.startGameButton.Click += startGameButton_Click; // Assuming you have a start game button
            this.uploadRecordButton.Click += uploadRecordButton_Click;
            this.userListView.SelectedIndexChanged += usersInRoomListView_SelectedIndexChanged;

            InitializeRoomEvents();
        }

        private void InitializeRoomEvents()
        {
            // Subscribe to WebSocket events for user actions
            _webSocketService.OnMessageReceived += (sender, e) =>
            {
                try
                {
                    var message = JObject.Parse(e.Data);
                    if (message["command"]?.ToString() == "user_joined")
                    {
                        var user = message["user"]?.ToObject<User>();
                        if (user != null)
                        {
                            LogRoomAction(user, "đã tham gia phòng.", Color.Green);
                        }
                    }
                    else if (message["command"]?.ToString() == "user_left")
                    {
                        var user = message["user"]?.ToObject<User>();
                        if (user != null)
                        {
                            LogRoomAction(user, "đã rời phòng.", Color.Orange);
                        }
                    }
                    else if (message["command"]?.ToString() == "user_kicked")
                    {
                        var user = message["user"]?.ToObject<User>();
                        if (user != null)
                        {
                            LogRoomAction(user, "đã bị đá khỏi phòng.", Color.Red);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"Error processing room action message: {ex.Message}");
                }
            };
        }

        private void resizeColumns()
        {
            if (this.userListView.Columns.Count < 3) return; // Ensure columns exist

            int width = this.userListView.Size.Width;
            // Adjust calculation based on your desired column widths
            int fixedWidths = 80 + 50; // Ping + Status
            int remainingWidth = width - fixedWidths;
            if (remainingWidth < 0) remainingWidth = 0; // Prevent negative width

            this.userListView.Columns[0].Width = remainingWidth - 5;
            this.userListView.Columns[1].Width = 80;
            this.userListView.Columns[2].Width = 50;
        }

        public void SetRoom(ClientRoom room)
        {
            _currentRoom = room;
            this.Text = $"Room: {_currentRoom.Name}";
            LogRoomAction(_appContext.GetCurrentUser(), "đã tham gia phòng.", Color.Green);
            UpdateRoomDetailsUi();
        }

        private void RoomDetailForm_Load(object sender, EventArgs e)
        {
            UpdateRoomDetailsUi();

            _wireGuardVpnService.OnConnected += WireguardVpnService_OnConnected;
            _wireGuardVpnService.OnDisconnected += WireguardVpnService_OnDisconnected;
            _wireGuardVpnService.OnErrorOccurred += WireguardVpnService_OnErrorOccurred;

            _wireGuardVpnService.ConnectAsync();
        }

        private void WebSocketService_OnConnectionStatusChanged(object sender, string message)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() => {
                    printLog($"[Kết nối] {message}", logRichTextBox.ForeColor);
                });
            }
            else
            {
                printLog($"[Kết nối] {message}", logRichTextBox.ForeColor);
            }
        }

        private void WireguardVpnService_OnConnected(object sender, EventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() => {
                    DebugLogger.Info("VPN connected successfully. You can interact with this room.");
                    printLog($"[Kết nối VPN] VPN đã kết nối thành công.", Color.DarkGreen);
                    UpdateButtonsState(); // Update buttons as VPN state affects them
                });
            }
            else 
            { 
                DebugLogger.Warn("[ROOM_DETAIL_FORM - No UI Handle] VPN Connected.");
                printLog($"[Kết nối VPN] VPN đã kết nối thành công.", Color.DarkGreen);
                UpdateButtonsState(); // Update buttons as VPN state affects them
            }
        }

        private void WireguardVpnService_OnDisconnected(object sender, EventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() => {
                    DebugLogger.Warn("VPN disconnected. User may not be able to interact with the room.");
                    printLog($"[Lỗi kết nối VPN] VPN đã ngắt kết nối. Hãy mở lại ứng dụng hoặc kiểm tra mạng", Color.Red);
                    UpdateButtonsState(); // Update buttons
                });
            }
            else 
            { 
                DebugLogger.Warn("[ROOM_DETAIL_FORM - No UI Handle] VPN Disconnected.");
                printLog($"[Lỗi kết nối VPN] VPN đã ngắt kết nối. Hãy mở lại ứng dụng hoặc kiểm tra mạng", Color.Red);
                UpdateButtonsState(); // Update buttons
            }
        }

        private void printLog(string logMessage, Color textColor)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(() =>
                {
                    if (this.IsDisposed || logRichTextBox.IsDisposed) return;
                    logRichTextBox.SelectionStart = logRichTextBox.TextLength;
                    logRichTextBox.SelectionLength = 0;
                    logRichTextBox.SelectionColor = textColor;
                    logRichTextBox.AppendText(logMessage + Environment.NewLine);
                    logRichTextBox.SelectionColor = logRichTextBox.ForeColor; // Reset color for subsequent text

                    // Auto-scroll to the bottom
                    logRichTextBox.SelectionStart = logRichTextBox.TextLength;
                    logRichTextBox.ScrollToCaret();

                    // Optional: Limit number of lines to prevent performance issues with very large logs
                    if (logRichTextBox.Lines.Length > 500) // Keep last 500 lines
                    {
                        logRichTextBox.Text = string.Join(Environment.NewLine, logRichTextBox.Lines.Skip(logRichTextBox.Lines.Length - 500));
                    }
                });
            }
            else
            {
                if (this.IsDisposed || logRichTextBox.IsDisposed) return;
                logRichTextBox.SelectionStart = logRichTextBox.TextLength;
                logRichTextBox.SelectionLength = 0;
                logRichTextBox.SelectionColor = textColor;
                logRichTextBox.AppendText(logMessage + Environment.NewLine);
                logRichTextBox.SelectionColor = logRichTextBox.ForeColor; // Reset color for subsequent text

                // Auto-scroll to the bottom
                logRichTextBox.SelectionStart = logRichTextBox.TextLength;
                logRichTextBox.ScrollToCaret();

                // Optional: Limit number of lines to prevent performance issues with very large logs
                if (logRichTextBox.Lines.Length > 500) // Keep last 500 lines
                {
                    logRichTextBox.Text = string.Join(Environment.NewLine, logRichTextBox.Lines.Skip(logRichTextBox.Lines.Length - 500));
                }
            }
        }

        private void WireguardVpnService_OnErrorOccurred(object sender, string errorMessage)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() => {
                    DebugLogger.Error($"VPN Error: {errorMessage}");
                    printLog($"[Lỗi kết nối VPN] {errorMessage}.", Color.Red);
                    UpdateButtonsState(); // Update buttons
                });
            }
            else { 
                DebugLogger.Error($"[ROOM_DETAIL_FORM - No UI Handle] VPN Error: {errorMessage}");
                printLog($"[Lỗi kết nối VPN] {errorMessage}.", Color.Red);
                UpdateButtonsState(); // Update buttons
            }
        }

        // --- AppContext Event Handlers ---
        private void AppContext_OnCurrentRoomDetailsUpdated(object sender, EventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(new Action(() =>
                {
                    HandleRoomDetailsUpdate();
                }));
            }
            else
            {
                HandleRoomDetailsUpdate();
            }
        }

        private void HandleRoomDetailsUpdate()
        {
            User currentUser = _appContext.GetCurrentUser();
            if (_currentRoom != null && _appContext.CurrentRoomDetails?.Id == _currentRoom.Id)
            {
                // Get the old status before updating
                string oldStatus = _currentRoom.Status;
                _currentRoom = _appContext.CurrentRoomDetails;
                
                // Log status changes
                if (oldStatus != _currentRoom.Status)
                {
                    switch (_currentRoom.Status)
                    {
                        case "playing":
                            _gameStartedAtUtc = DateTime.UtcNow;
                            _recordUploaded = false;
                            LogGameAction("Trò chơi đã bắt đầu!", Color.Green);
                            break;
                        case "finished":
                            LogGameAction("Trò chơi đã kết thúc.", Color.Orange);
                            // Only the host auto-uploads + reports the result (drives ELO).
                            // This avoids every member uploading a duplicate of the same game.
                            if (currentUser != null && currentUser.Username == _currentRoom.HostUsername)
                            {
                                _ = UploadRecordAsync(isAutomatic: true);
                            }
                            break;
                        case "closed":
                            LogGameAction("Phòng đã đóng.", Color.Red);
                            break;
                    }
                }

                UpdateRoomDetailsUi();
                if (_currentRoom.Status == "closed")
                {
                    _isClosingOrClosed = true;
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                        "Phòng đã đóng",
                        $"Phòng '{_currentRoom.Name}' đã bị đóng.",
                        AntdUI.TType.Info)
                    {
                        CancelText = null,
                        OkText = "Đóng"
                    });
                    this.Close();
                }
            }
            else if (_appContext.CurrentRoomDetails == null && _currentRoom != null && currentUser?.CurrentRoomId == null)
            {
                if (!_appContext.AllActiveRooms.Any(r => r.Id == _currentRoom.Id && r.Status != "closed"))
                {
                    _isClosingOrClosed = true;
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                        "Phòng đã đóng",
                        $"Bạn bị mời ra khỏi phòng '{_currentRoom.Name}' hoặc nó đã bị đóng.",
                        AntdUI.TType.Info)
                    {
                        CancelText = null,
                        OkText = "Đóng"
                    });
                    this.Close();
                }
            }
        }

        private void AppContext_OnOnlineUsersUpdated(object sender, EventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(new Action(() =>
                {
                    if (_currentRoom != null)
                    {
                        var updatedRoomInList = _appContext.AllActiveRooms.FirstOrDefault(r => r.Id == _currentRoom.Id);
                        if (updatedRoomInList != null)
                        {
                            _currentRoom = updatedRoomInList;
                            UpdateRoomDetailsUi();
                        }
                        else
                        {
                            if (_currentRoom.Status != "closed")
                            {
                                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                                    "Phòng không tồn tại",
                                    $"Phòng '{_currentRoom.Name}' không tồn tại hoặc đã đóng.",
                                    AntdUI.TType.Info)
                                {
                                    CancelText = null,
                                    OkText = "Đóng"
                                });
                                this.Close();
                            }
                        }
                    }
                }));
            }
        }


        private void WebSocketService_OnErrorOccurred(object sender, string errorMessage)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() =>
                {
                    printLog($"[Mất kết nối] {errorMessage}.", Color.Red);
                    DebugLogger.Error($"RoomDetailForm WS Error: {errorMessage}");
                });
            }
            else 
            {
                printLog($"[Mất kết nối] {errorMessage}.", Color.Red);
                DebugLogger.Error($"[ROOM_DETAIL_FORM - No UI Handle] WS Error: {errorMessage}");
            }
        }

        // --- UI Update Logic ---
        private void UpdateRoomDetailsUi()
        {
            if (_currentRoom == null)
            {
                this.Close();
                return;
            }

            windowBar.Text = $"{_currentRoom.Name}";
            windowBar.SubText = $"Trạng thái: {_currentRoom.Status}";


            userListView.Items.Clear();
            if (_currentRoom.Users != null)
            {
                foreach (var user in _currentRoom.Users)
                {
                    string username = user.FullName ?? user.Username;
                    string nickname = user.NickName ?? username;
                    string status = user.Status ?? "";

                    ListViewItem item = new ListViewItem(user.Username);
                    item.SubItems.Add(nickname);
                    item.SubItems.Add(status);
                    item.Tag = user.Username;

                    if (user.Status == "host") item.ForeColor = Color.OrangeRed;
                    else if (user.Status == "joined") item.ForeColor = Color.Green;

                    userListView.Items.Add(item);
                }
            }
            UpdateButtonsState();
        }

        private void UpdateButtonsState()
        {
            User currentUser = _appContext.GetCurrentUser();

            if (_currentRoom == null || currentUser == null)
            {
                kickUserButton.Enabled = false;
                startGameButton.Enabled = false;
                startGameButton.Text = "Bắt đầu";
                uploadRecordButton.Enabled = false;
                return;
            }

            // Only the host uploads/reports the finished game (drives ELO). Enabled once finished.
            bool isHostForUpload = currentUser.Username == _currentRoom.HostUsername;
            uploadRecordButton.Enabled = isHostForUpload && _currentRoom.Status == "finished" && !_uploadInProgress;
            uploadRecordButton.Visible = isHostForUpload;

            bool isHost = currentUser.Username == _currentRoom.HostUserId;
            bool isVpnConnected = _wireGuardVpnService.IsConnected;
            bool isJoined = currentUser.CurrentRoomId == _currentRoom.Id && currentUser.Status == "joined";

            // Enable kickUserButton if host and a joined user is selected (not self)
            if (isHost && userListView.SelectedItems.Count > 0)
            {
                var selectedUserTag = userListView.SelectedItems[0].Tag as string;
                var selectedUser = _currentRoom.Users.FirstOrDefault(u => u.Username == selectedUserTag);
                kickUserButton.Enabled = selectedUser != null
                    && selectedUser.Username != currentUser.Username
                    && selectedUser.Status == "joined";
            }
            else
            {
                kickUserButton.Enabled = false;
            }

            if (isHost && isVpnConnected)
            {
                startGameButton.Enabled = true;
                startGameButton.Text = "Bắt đầu";
            }
            else if (isJoined)
            {
                startGameButton.Enabled = false;
                startGameButton.Text = "Tham gia";
            }
            else
            {
                startGameButton.Enabled = false;
                startGameButton.Text = "Bắt đầu";
            }
        }

        // --- Button Click Handlers ---
        private async void startGameButton_Click(object sender, EventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser == null || currentUser.Username != _currentRoom.HostUsername) return; // Chỉ chủ phòng mới được bắt đầu game

            if (_currentRoom.Status != "open")
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                    "Lỗi",
                    "Trạng thái phòng không phải là 'mở'.",
                    AntdUI.TType.Warn)
                {
                    CancelText = null,
                    OkText = "Đóng"
                });
                return;
            }

            if (_currentRoom.Users.Length <= 1)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                    "Lỗi",
                    "Cần ít nhất 2 người chơi để bắt đầu game.",
                    AntdUI.TType.Warn)
                {
                    CancelText = null,
                    OkText = "Đóng"
                });
                return;
            }

            await AntdUI.Spin.open(this, AntdUI.Localization.Get("StartingGame", "Đang bắt đầu game..."), async config =>
            {
                try
                {
                    bool success = await _webSocketService.SendMessageAsync(new { command = "start_game", room_id = _currentRoom.Id });
                    if (!success) { throw new Exception("Gửi lệnh bắt đầu game thất bại."); }
                    DebugLogger.Info($"RoomDetailForm: Đã gửi lệnh bắt đầu game cho phòng {_currentRoom.Id}");
                    // Server sẽ cập nhật trạng thái phòng thành 'playing' và broadcast, UI sẽ được cập nhật
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"RoomDetailForm: Gửi lệnh bắt đầu game thất bại: {ex.Message}");
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this, "Lỗi", ex.InnerException?.Message ?? ex.Message, AntdUI.TType.Error)
                    { CancelText = null, OkText = "Đóng" });
                }
            });
        }

        private void uploadRecordButton_Click(object sender, EventArgs e)
        {
            _ = UploadRecordAsync(isAutomatic: false);
        }

        /// <summary>
        /// Locates the recorded game for this match and uploads it (with metadata) to the server.
        /// Safe to call from any thread. When <paramref name="isAutomatic"/> is true the call is
        /// skipped silently if a record was already uploaded for this game.
        /// </summary>
        private async Task UploadRecordAsync(bool isAutomatic)
        {
            if (_currentRoom == null) return;

            // Avoid duplicate auto-uploads; the manual button can still force a retry.
            if (isAutomatic && (_recordUploaded || _uploadInProgress)) return;
            if (_uploadInProgress)
            {
                printLog("[Record] Đang tải lên, vui lòng đợi...", Color.Orange);
                return;
            }

            _uploadInProgress = true;
            UiUtils.InvokeOnUiThread(this, UpdateButtonsState);

            try
            {
                string gameFolder = _appContext.GetGameFolder();
                if (string.IsNullOrWhiteSpace(gameFolder))
                {
                    string cfgMsg = "Chưa cấu hình thư mục game. Vào Cài đặt để chọn thư mục cài đặt AoE2 (nơi chứa SaveGame).";
                    printLog($"[Record] {cfgMsg}", Color.Red);
                    if (!isAutomatic)
                    {
                        UiUtils.ShowAntdModal(this, "Chưa cấu hình", cfgMsg, AntdUI.TType.Warn);
                    }
                    return;
                }

                string recordPath = RecordedGameFinder.FindLatestRecord(gameFolder, _gameStartedAtUtc);
                if (string.IsNullOrEmpty(recordPath))
                {
                    string msg = "Không tìm thấy file record (.mgz) của trận đấu trong thư mục game. " +
                                 "Hãy chắc chắn rằng bạn đã chơi xong và game đã lưu lại trận đấu.";
                    printLog($"[Record] {msg}", Color.Red);
                    if (!isAutomatic)
                    {
                        UiUtils.ShowAntdModal(this, "Không tìm thấy record", msg, AntdUI.TType.Warn);
                    }
                    return;
                }

                User currentUser = _appContext.GetCurrentUser();
                var metadata = new GameRecordMetadata
                {
                    RoomId = _currentRoom.Id,
                    RoomName = _currentRoom.Name,
                    HostUsername = _currentRoom.HostUsername,
                    UploadedBy = currentUser?.Username,
                    Players = _currentRoom.Users?.Select(u => u.Username).ToArray() ?? Array.Empty<string>(),
                    FinishedAt = DateTime.UtcNow,
                    RecordFileName = System.IO.Path.GetFileName(recordPath),
                    ClientVersion = Application.ProductVersion
                };

                printLog($"[Record] Đang tải lên '{metadata.RecordFileName}'...", Color.RoyalBlue);

                GameRecordUploadResponse response = await _accountService.UploadGameRecordAsync(metadata, recordPath);
                _recordUploaded = true;
                printLog("[Record] Tải lên record thành công.", Color.DarkGreen);

                GameRecordInfo info = response.Record;

                if (info != null && !string.IsNullOrEmpty(info.Mvp))
                {
                    printLog($"[Record] MVP của trận đấu: ⭐ {info.Mvp}", Color.DarkGoldenrod);
                }

                // Only the host reports the winner. The result drives the 4v4 ELO ranking.
                bool isHost = currentUser != null && currentUser.Username == _currentRoom.HostUsername;
                if (isHost && info != null && info.CanReport && info.Teams != null)
                {
                    UiUtils.InvokeOnUiThread(this, () => PromptReportWinner(info));
                }
                else if (info != null && info.Status == "needs_review")
                {
                    printLog($"[Record] Không thể xác định đội chơi từ record nên ELO sẽ không được tính tự động." +
                             (string.IsNullOrEmpty(info.ReviewReason) ? "" : $" ({info.ReviewReason})"), Color.Orange);
                    if (!isAutomatic)
                    {
                        UiUtils.ShowAntdModal(this, "Đã tải lên",
                            "Đã tải lên record, nhưng không thể tự động xác định đội chơi để tính ELO.", AntdUI.TType.Warn);
                    }
                }
                else if (!isAutomatic)
                {
                    UiUtils.ShowAntdModal(this, "Thành công", "Đã tải lên record của trận đấu.", AntdUI.TType.Success);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"RoomDetailForm: Upload record thất bại: {ex.Message}");
                printLog($"[Record] Lỗi tải lên record: {ex.Message}", Color.Red);
                if (!isAutomatic)
                {
                    UiUtils.ShowAntdModal(this, "Lỗi", ex.Message, AntdUI.TType.Error);
                }
            }
            finally
            {
                _uploadInProgress = false;
                UiUtils.InvokeOnUiThread(this, UpdateButtonsState);
            }
        }

        /// <summary>
        /// Host-only: shows the parsed teams and lets the host report the winner, which
        /// triggers the server-side 4v4 ELO update. Must be called on the UI thread.
        /// </summary>
        private void PromptReportWinner(GameRecordInfo info)
        {
            if (this.IsDisposed || info?.Teams == null) return;

            info.Teams.TryGetValue("1", out var team1);
            info.Teams.TryGetValue("2", out var team2);

            using var dialog = new ReportWinnerDialog(team1 ?? Array.Empty<string>(), team2 ?? Array.Empty<string>(), info.SuggestedWinnerTeam);
            if (dialog.ShowDialog(this) == DialogResult.OK && dialog.WinningTeam.HasValue)
            {
                _ = ReportWinnerAsync(info.Id, dialog.WinningTeam.Value);
            }
            else
            {
                printLog("[Kết quả] Chưa báo kết quả. Bạn có thể tải lên lại record để báo kết quả sau.", Color.Orange);
            }
        }

        private async Task ReportWinnerAsync(long recordId, int winningTeam)
        {
            try
            {
                printLog($"[Kết quả] Đang gửi kết quả (Đội {winningTeam} thắng)...", Color.RoyalBlue);
                ReportResultResponse result = await _accountService.ReportGameResultAsync(recordId, winningTeam);

                printLog($"[Kết quả] Đội {result.WinningTeam} thắng. ELO đã được cập nhật:", Color.DarkGreen);
                if (result.Ratings != null)
                {
                    foreach (var change in result.Ratings)
                    {
                        string sign = change.Delta >= 0 ? "+" : "";
                        // Breakdown: win/loss base, score-performance, MVP bonus.
                        string perf = change.PerfDelta != 0 ? $", điểm {(change.PerfDelta > 0 ? "+" : "")}{change.PerfDelta}" : "";
                        string mvp = change.MvpDelta != 0 ? $", MVP +{change.MvpDelta} ⭐" : "";
                        printLog($"    {change.Username}: {change.Old} → {change.NewRating} ({sign}{change.Delta})  [trận {(change.BaseDelta > 0 ? "+" : "")}{change.BaseDelta}{perf}{mvp}]",
                            change.Delta >= 0 ? Color.DarkGreen : Color.Firebrick);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"RoomDetailForm: Báo kết quả thất bại: {ex.Message}");
                printLog($"[Kết quả] Lỗi báo kết quả: {ex.Message}", Color.Red);
                UiUtils.ShowAntdModal(this, "Lỗi", ex.Message, AntdUI.TType.Error);
            }
        }

        private void kickUserButton_Click(object sender, EventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser == null || currentUser.Username != _currentRoom.HostUsername) return;

            if (userListView.SelectedItems.Count == 0)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                    "Lỗi",
                    "Vui lòng chọn người dùng để đá khỏi phòng.",
                    AntdUI.TType.Warn)
                {
                    CancelText = null,
                    OkText = "Đóng"
                });
                return;
            }
            string userNameToKick = userListView.SelectedItems[0].Tag?.ToString();
            if (userNameToKick == currentUser.Username)
            {
                AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                    "Lỗi",
                    "Bạn không thể tự đá mình khỏi phòng.",
                    AntdUI.TType.Warn)
                {
                    CancelText = null,
                    OkText = "Đóng"
                });
                return;
            }

            AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "Xác nhận",
                $"Bạn có chắc chắn muốn đá '{userNameToKick}' khỏi phòng này?",
                AntdUI.TType.Warn)
            {
                CancelText = "Hủy",
                OkText = "Đồng ý",
                OnOk = config =>
                {
                    // Use Task.Run to avoid blocking UI thread, since OnOk must be sync
                    Task.Run(async () =>
                    {
                        await AntdUI.Spin.open(this, AntdUI.Localization.Get("KickingUser", "Đang đá người dùng..."), async spinConfig =>
                        {
                            try
                            {
                                bool success = await _webSocketService.KickUserFromRoomAsync(_currentRoom.Id, userNameToKick);
                                if (!success) { throw new Exception("Gửi lệnh đá người dùng thất bại."); }
                                DebugLogger.Info($"RoomDetailForm: Đã gửi lệnh đá người dùng {userNameToKick} khỏi phòng {_currentRoom.Id}");
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Error($"RoomDetailForm: Gửi lệnh đá người dùng thất bại: {ex.Message}");
                                AntdUI.Modal.open(new AntdUI.Modal.Config(this, "Lỗi", ex.InnerException?.Message ?? ex.Message, AntdUI.TType.Error)
                                { CancelText = null, OkText = "Đóng" });
                            }
                        });
                    });
                    return true;
                }
            });
        }

        private async void closeRoom(object sender, FormClosingEventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser == null || currentUser.Username != _currentRoom.HostUsername) return;

            AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "Xác nhận",
                "Bạn có chắc chắn muốn đóng phòng này? Tất cả người dùng sẽ bị loại khỏi phòng.",
                AntdUI.TType.Warn)
            {
                CancelText = "Hủy",
                OkText = "Đồng ý",
                // Replace all instances of:
                // OnOk = async config => { ... }

                // With the following pattern:
                OnOk = config =>
                {
                    // Use Task.Run to execute async code without blocking the UI thread
                    Task.Run(async () =>
                    {
                        await AntdUI.Spin.open(this, AntdUI.Localization.Get("ClosingRoom", "Đang đóng phòng..."), async spinConfig =>
                        {
                            try
                            {
                                bool success = await _webSocketService.CloseRoomAsync(_currentRoom.Id);
                                if (success)
                                {
                                    e.Cancel = false;
                                }
                                DebugLogger.Info($"RoomDetailForm: Đã gửi lệnh đóng phòng {_currentRoom.Id}");
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Error($"RoomDetailForm: Gửi lệnh đóng phòng thất bại: {ex.Message}");
                                AntdUI.Modal.open(new AntdUI.Modal.Config(this, "Lỗi", ex.InnerException?.Message ?? ex.Message, AntdUI.TType.Error)
                                { CancelText = null, OkText = "Đóng" });
                            }
                        });
                    });
                    return true;
                }
            });
        }

        private async void leaveRoom(object sender, FormClosingEventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser == null || currentUser.CurrentRoomId != _currentRoom.Id) return;

            AntdUI.Modal.open(new AntdUI.Modal.Config(this,
                "Xác nhận",
                "Bạn có chắc chắn muốn rời khỏi phòng này?",
                AntdUI.TType.Warn)
            {
                CancelText = "Hủy",
                OkText = "Đồng ý",
                OnOk = config =>
                {
                    // Use Task.Run to execute async code without blocking the UI thread
                    Task.Run(async () =>
                    {
                        await AntdUI.Spin.open(this, AntdUI.Localization.Get("LeavingRoom", "Đang rời phòng..."), async spinConfig =>
                        {
                            try
                            {
                                LogRoomAction(currentUser, "đang rời phòng...", Color.Orange);
                                bool success = await _webSocketService.LeaveRoomAsync();
                                if (success)
                                {
                                    e.Cancel = false;
                                }
                                DebugLogger.Info($"RoomDetailForm: Đã gửi lệnh rời phòng {_currentRoom.Id}");
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Error($"RoomDetailForm: Gửi lệnh rời phòng thất bại: {ex.Message}");
                                AntdUI.Modal.open(new AntdUI.Modal.Config(this, "Lỗi", ex.InnerException?.Message ?? ex.Message, AntdUI.TType.Error)
                                { CancelText = null, OkText = "Đóng" });
                            }
                        });
                    });
                    return true;
                }
            });
        }

        private void usersInRoomListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateButtonsState();
        }

        private void RoomDetailForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            _isClosingOrClosed = true;

            _webSocketService.OnErrorOccurred -= WebSocketService_OnErrorOccurred;
            _appContext.OnCurrentRoomDetailsUpdated -= AppContext_OnCurrentRoomDetailsUpdated;
            _appContext.OnOnlineUsersUpdated -= AppContext_OnOnlineUsersUpdated;

            _wireGuardVpnService.OnConnected -= WireguardVpnService_OnConnected;
            _wireGuardVpnService.OnDisconnected -= WireguardVpnService_OnDisconnected;
            _wireGuardVpnService.OnErrorOccurred -= WireguardVpnService_OnErrorOccurred;

            if (_wireGuardVpnService.IsConnected)
            {
                // This will run on a thread pool thread, not block FormClosed.
                _ = _wireGuardVpnService.DisconnectAsync(); // Fire and forget, or handle logging internally
                _wireGuardVpnService.Dispose(); // Fire and forget, or handle logging internally
            }
        }

        private void RoomDetailForm_FormClosing(object sender, FormClosingEventArgs e)
        {

            if (_isClosingOrClosed)
            {
                return;
            }

            User currentUser = _appContext.GetCurrentUser();
            if (currentUser != null && _currentRoom != null) // Ensure _currentRoom is not null
            {
                if (currentUser.Username == _currentRoom.HostUsername) // Use ID for comparison, more robust
                {
                    closeRoom(sender, e); // Host closing the room
                }
                else if (currentUser.CurrentRoomId == _currentRoom.Id) // Ensure they are in THIS room
                {
                    leaveRoom(sender, e); // Member leaving the room
                }
            }
        }

        private void LogRoomAction(User user, string action, Color color = default)
        {
            if (color == default) color = logRichTextBox.ForeColor;
            string username = user?.FullName ?? user?.Username ?? "Unknown user";
            printLog($"[Hành động] {username} {action}", color);
        }

        private void LogGameAction(string action, Color color = default)
        {
            if (color == default) color = logRichTextBox.ForeColor;
            printLog($"[Trò chơi] {action}", color);
        }
    }
}