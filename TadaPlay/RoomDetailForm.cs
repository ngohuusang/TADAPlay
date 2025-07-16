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
using TadaPlay.Websockets.Interface;

namespace TadaPlay
{
    public partial class RoomDetailForm : Window
    {
        private readonly IWebSocketService _webSocketService;
        private readonly IWireGuardVpnService _wireGuardVpnService;
        private readonly IAppContext _appContext;
        private ClientRoom _currentRoom;

        public RoomDetailForm(IWebSocketService webSocketService, IAppContext appContext, IWireGuardVpnService wireGuardVpnService)
        {
            InitializeComponent();
            _webSocketService = webSocketService;
            _appContext = appContext;
            _wireGuardVpnService = wireGuardVpnService;

            // Subscribe to AppContext events relevant to this form
            _appContext.OnCurrentRoomDetailsUpdated += AppContext_OnCurrentRoomDetailsUpdated;
            _appContext.OnOnlineUsersUpdated += AppContext_OnOnlineUsersUpdated; // For user status within room
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
            this.userListView.SelectedIndexChanged += usersInRoomListView_SelectedIndexChanged;
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

        private void WireguardVpnService_OnConnected(object sender, EventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() => {
                    DebugLogger.Info("VPN connected successfully. You can interact with this room.");
                    Notification.info(this, "Đã kết nối VPN", "VPN đã kết nối thành công. Bạn có thể tương tác với phòng này.", TAlignFrom.Bottom);
                    UpdateButtonsState(); // Update buttons as VPN state affects them
                });
            }
            else 
            { 
                DebugLogger.Warn("[ROOM_DETAIL_FORM - No UI Handle] VPN Connected.");
                Notification.info(this, "Đã kết nối VPN", "VPN đã kết nối thành công. Bạn có thể tương tác với phòng này.", TAlignFrom.Bottom);
                UpdateButtonsState(); // Update buttons as VPN state affects them
            }
        }

        private void WireguardVpnService_OnDisconnected(object sender, EventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() => {
                    DebugLogger.Warn("VPN disconnected. User may not be able to interact with the room.");
                    Notification.warn(this, "Đã ngắt kết nối VPN", "VPN đã ngắt kết nối. Bạn có thể không tương tác được với phòng này.", TAlignFrom.Bottom);
                    UpdateButtonsState(); // Update buttons
                });
            }
            else 
            { 
                DebugLogger.Warn("[ROOM_DETAIL_FORM - No UI Handle] VPN Disconnected.");
                Notification.warn(this, "Đã ngắt kết nối VPN", "VPN đã ngắt kết nối. Bạn có thể không tương tác được với phòng này.", TAlignFrom.Bottom);
                UpdateButtonsState(); // Update buttons
            }
        }

        private void WireguardVpnService_OnErrorOccurred(object sender, string errorMessage)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() => {
                    DebugLogger.Error($"VPN Error: {errorMessage}");
                    Notification.error(this, "Lỗi kết nối VPN", errorMessage, TAlignFrom.Bottom);
                    UpdateButtonsState(); // Update buttons
                });
            }
            else { 
                DebugLogger.Error($"[ROOM_DETAIL_FORM - No UI Handle] VPN Error: {errorMessage}");
                Notification.error(this, "Lỗi kết nối VPN", errorMessage, TAlignFrom.Bottom);
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
                    User currentUser = _appContext.GetCurrentUser();
                    // If the updated current room in AppContext is THIS room
                    if (_currentRoom != null && _appContext.CurrentRoomDetails?.Id == _currentRoom.Id)
                    {
                        _currentRoom = _appContext.CurrentRoomDetails; // Get the latest details
                        UpdateRoomDetailsUi();
                        // Check if room was closed
                        if (_currentRoom.Status == "closed")
                        {
                            MessageBox.Show($"Phòng '{_currentRoom.Name}' đã bị đóng.", "Phòng đã đóng", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            this.Close();
                        }
                    }
                    // Or if current user is no longer in *any* room and this form is showing their previous room
                    else if (_appContext.CurrentRoomDetails == null && _currentRoom != null && currentUser?.CurrentRoomId == null)
                    {
                        // Check if this specific room is no longer in the active rooms list
                        if (!_appContext.AllActiveRooms.Any(r => r.Id == _currentRoom.Id && r.Status != "closed"))
                        {
                            MessageBox.Show($"Bạn bị mời ra khỏi phòng '{_currentRoom.Name}' hoặc nó đã bị đóng.", "Phòng đã đóng", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            this.Close();
                        }
                    }
                }));
            }
        }

        private void AppContext_OnOnlineUsersUpdated(object sender, EventArgs e)
        {
            // This event indicates any user's status or list changed.
            // We need to check if the users in *our* room (_currentRoom) were affected.
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(new Action(() =>
                {
                    if (_currentRoom != null)
                    {
                        // Get the latest version of this room from AppContext's rooms list
                        var updatedRoomInList = _appContext.AllActiveRooms.FirstOrDefault(r => r.Id == _currentRoom.Id);
                        if (updatedRoomInList != null)
                        {
                            _currentRoom = updatedRoomInList; // Update local room object
                            UpdateRoomDetailsUi(); // Refresh UI
                        }
                        else
                        {
                            // If our room is no longer in the AllActiveRooms list (it was closed/deleted)
                            // This scenario is mostly covered by OnCurrentRoomDetailsUpdated, but good to double check.
                            if (_currentRoom.Status != "closed") // If not already marked closed, implies it vanished
                            {
                                MessageBox.Show($"Phòng '{_currentRoom.Name}' không tồn tại hoặc đã đóng.", "Phòng không tồn tại", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    Notification.error(this, "Mất kết nối", errorMessage, TAlignFrom.Bottom);
                    DebugLogger.Error($"RoomDetailForm WS Error: {errorMessage}");
                });
            }
            else 
            {
                Notification.error(this, "Mất kết nối", errorMessage, TAlignFrom.Bottom);
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
                return;
            }

            bool isHost = currentUser.Id.ToString() == _currentRoom.HostUserId;

            bool isInThisRoom = (currentUser.CurrentRoomId == _currentRoom.Id) && (currentUser.Status == "host" || currentUser.Status == "joined" || currentUser.Status == "spectating");


            kickUserButton.Enabled = isHost && userListView.SelectedItems.Count > 0 &&
                                    (string)userListView.SelectedItems[0].Tag != currentUser.Username;

        }

        // --- Button Click Handlers ---
        private async void startGameButton_Click(object sender, EventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser == null || currentUser.Username != _currentRoom.HostUsername) return; // Only host can start game
            if (_currentRoom.Status != "open") { MessageBox.Show("Game is not in 'open' status.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (_currentRoom.Users.Length <= 1) { MessageBox.Show("Need at least 2 players to start a game.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }


            await AntdUI.Spin.open(this, AntdUI.Localization.Get("StartingGame", "Đang bắt đầu game..."), async config =>
            {
                try
                {
                    // Assuming you have a command for starting game in WebSocketService
                    bool success = await _webSocketService.SendMessageAsync(new { command = "start_game", room_id = _currentRoom.Id });
                    if (!success) { throw new Exception("Failed to send start game command."); }
                    DebugLogger.Info($"RoomDetailForm: Sent start_game command for room {_currentRoom.Id}");
                    // Server will update room status to 'playing' and broadcast, which updates UI
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"RoomDetailForm: Failed to send start game command: {ex.Message}");
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this, AntdUI.Localization.Get("Error", "Lỗi"), ex.InnerException?.Message ?? ex.Message, AntdUI.TType.Error)
                    { CancelText = null, OkText = AntdUI.Localization.Get("CloseButton", "Đóng") });
                }
            });
        }

        private async void kickUserButton_Click(object sender, EventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser == null || currentUser.Username != _currentRoom.HostUsername) return;

            if (userListView.SelectedItems.Count == 0) { MessageBox.Show("Please select a user to kick.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string userNameToKick = userListView.SelectedItems[0].Tag?.ToString();
            if (userNameToKick == currentUser.Username) { MessageBox.Show("You cannot kick yourself.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            DialogResult confirm = MessageBox.Show($"Are you sure you want to kick '{userNameToKick}' from this room?", "Confirm Kick", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm == DialogResult.Yes)
            {
                await AntdUI.Spin.open(this, AntdUI.Localization.Get("KickingUser", "Đang đá người dùng..."), async config =>
                {
                    try
                    {
                        bool success = await _webSocketService.KickUserFromRoomAsync(_currentRoom.Id, userNameToKick);
                        if (!success) { throw new Exception("Failed to send kick command."); }
                        DebugLogger.Info($"RoomDetailForm: Sent kick_user command for user {userNameToKick} in room {_currentRoom.Id}");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error($"RoomDetailForm: Failed to send kick command: {ex.Message}");
                        AntdUI.Modal.open(new AntdUI.Modal.Config(this, AntdUI.Localization.Get("Error", "Lỗi"), ex.InnerException?.Message ?? ex.Message, AntdUI.TType.Error)
                        { CancelText = null, OkText = AntdUI.Localization.Get("CloseButton", "Đóng") });
                    }
                });
            }
        }

        private async void closeRoom(object sender, FormClosingEventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser == null || currentUser.Username != _currentRoom.HostUsername) return;

            DialogResult confirm = MessageBox.Show("Are you sure you want to close this room? All users will be removed.", "Confirm Close Room", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm == DialogResult.Yes)
            {
                await AntdUI.Spin.open(this, AntdUI.Localization.Get("ClosingRoom", "Đang đóng phòng..."), async config =>
                {
                    try
                    {
                        bool success = await _webSocketService.CloseRoomAsync(_currentRoom.Id);
                        if (success) 
                        {
                            e.Cancel = false;
                        }
                        DebugLogger.Info($"RoomDetailForm: Sent close_room command for room {_currentRoom.Id}");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error($"RoomDetailForm: Failed to send close room command: {ex.Message}");
                        AntdUI.Modal.open(new AntdUI.Modal.Config(this, AntdUI.Localization.Get("Error", "Lỗi"), ex.InnerException?.Message ?? ex.Message, AntdUI.TType.Error)
                        { CancelText = null, OkText = AntdUI.Localization.Get("CloseButton", "Đóng") });
                    }
                });
            }
        }

        private async void leaveRoom(object sender, FormClosingEventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser == null || currentUser.CurrentRoomId != _currentRoom.Id) return;

            DialogResult confirm = MessageBox.Show("Are you sure you want to leave this room?", "Confirm Leave", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm == DialogResult.Yes)
            {
                await AntdUI.Spin.open(this, AntdUI.Localization.Get("LeavingRoom", "Đang rời phòng..."), async config =>
                {
                    try
                    {
                        bool success = await _webSocketService.LeaveRoomAsync();
                        if (success) 
                        {
                            e.Cancel = false;
                        }
                        DebugLogger.Info($"RoomDetailForm: Sent leave_room command for room {_currentRoom.Id}");
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error($"RoomDetailForm: Failed to send leave room command: {ex.Message}");
                        AntdUI.Modal.open(new AntdUI.Modal.Config(this, AntdUI.Localization.Get("Error", "Lỗi"), ex.InnerException?.Message ?? ex.Message, AntdUI.TType.Error)
                        { CancelText = null, OkText = AntdUI.Localization.Get("CloseButton", "Đóng") });
                    }
                });
            }
        }

        private void usersInRoomListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateButtonsState();
        }

        private void RoomDetailForm_FormClosed(object sender, FormClosedEventArgs e)
        {
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
    }
}