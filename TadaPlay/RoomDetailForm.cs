using AntdUI;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TadaPlay.Common.Models;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Websockets.Interface;

namespace TadaPlay
{
    public partial class RoomDetailForm : Window
    {
        private readonly IWebSocketService _webSocketService;
        private readonly IAppContext _appContext;
        private ClientRoom _currentRoom;

        public RoomDetailForm(IWebSocketService webSocketService, IAppContext appContext)
        {
            InitializeComponent();
            _webSocketService = webSocketService;
            _appContext = appContext;

            // Subscribe to AppContext events relevant to this form
            _appContext.OnCurrentRoomDetailsUpdated += AppContext_OnCurrentRoomDetailsUpdated;
            _appContext.OnOnlineUsersUpdated += AppContext_OnOnlineUsersUpdated; // For user status within room
            _webSocketService.OnErrorOccurred += WebSocketService_OnErrorOccurred;
            // No need to subscribe to OnMessageReceived from WebSocketService here, AppContext processes it.

            userListView.Columns.Add("Username", 100);
            userListView.Columns.Add("Nickname", 100);
            userListView.Columns.Add("Status", 80);
            userListView.View = View.Details;

            // Hook up button click events
            this.kickUserButton.Click += kickUserButton_Click;
            this.startGameButton.Click += startGameButton_Click; // Assuming you have a start game button
            this.userListView.SelectedIndexChanged += usersInRoomListView_SelectedIndexChanged;
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
                            MessageBox.Show($"Room '{_currentRoom.Name}' has been closed by the host.", "Room Closed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            this.Close();
                        }
                    }
                    // Or if current user is no longer in *any* room and this form is showing their previous room
                    else if (_appContext.CurrentRoomDetails == null && _currentRoom != null && currentUser?.CurrentRoomId == null)
                    {
                        // Check if this specific room is no longer in the active rooms list
                        if (!_appContext.AllActiveRooms.Any(r => r.Id == _currentRoom.Id && r.Status != "closed"))
                        {
                            MessageBox.Show($"You have been removed from room '{_currentRoom.Name}' or it no longer exists.", "Removed from Room", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                                MessageBox.Show($"Room '{_currentRoom.Name}' no longer exists or is closed.", "Room Missing", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    MessageBox.Show(errorMessage, "WebSocket Error in Room", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    DebugLogger.Error($"RoomDetailForm WS Error: {errorMessage}");
                });
            }
            else { DebugLogger.Error($"[ROOM_DETAIL_FORM - No UI Handle] WS Error: {errorMessage}"); }
        }

        // --- UI Update Logic ---
        private void UpdateRoomDetailsUi()
        {
            if (_currentRoom == null)
            {
                this.Close();
                return;
            }

            windowBar.Text = $"Room Name: {_currentRoom.Name}";
            windowBar.SubText = $"Host: {_currentRoom.HostUsername} | Status: {_currentRoom.Status}";


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
            if (currentUser == null || currentUser.Username != _currentRoom.HostUsername) return;
            //if (this.Owner is MainForm mainForm) mainForm.ShowLoading("Starting game...");
            try
            {
                //TODO Start loading
                //await _webSocketService.StartGameInRoomAsync(_currentRoom.Id);
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"RoomDetailForm: Failed to send start game command: {ex.Message}");
            }
            finally
            {
                //if (this.Owner is MainForm mainForm) mainForm.HideLoading();
                //TODO Stop loading
            }
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
                try
                {
                    //TODO Start loading
                    await _webSocketService.KickUserFromRoomAsync(_currentRoom.Id, userNameToKick);
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"RoomDetailForm: Failed to send kick command: {ex.Message}");
                }
                finally
                {
                    //TODO Stop loading
                }
            }
        }

        private async void closeRoomButton_Click(object sender, EventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser == null || currentUser.Username != _currentRoom.HostUsername) return;

            DialogResult confirm = MessageBox.Show("Are you sure you want to close this room? All users will be removed.", "Confirm Close Room", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm == DialogResult.Yes)
            {
                //if (this.Owner is MainForm mainForm) mainForm.ShowLoading("Closing room...");
                try { await _webSocketService.CloseRoomAsync(_currentRoom.Id); }
                catch (Exception ex) { DebugLogger.Error($"RoomDetailForm: Failed to send close room command: {ex.Message}"); }
                finally
                {
                    //if (this.Owner is MainForm mainForm) mainForm.HideLoading();
                }
            }
        }

        private async void leaveRoomButton_Click(object sender, EventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser == null || currentUser.CurrentRoomId != _currentRoom.Id) return;

            DialogResult confirm = MessageBox.Show("Are you sure you want to leave this room?", "Confirm Leave", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm == DialogResult.Yes)
            {
                //if (this.Owner is MainForm mainForm) mainForm.ShowLoading("Leaving room...");
                try { await _webSocketService.SendMessageAsync(new { command = "leave_room" }); }
                catch (Exception ex) { DebugLogger.Error($"RoomDetailForm: Failed to send leave room command: {ex.Message}"); }
                finally
                {
                    //if (this.Owner is MainForm mainForm) mainForm.HideLoading();
                }
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
        }

        private void RoomDetailForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            User currentUser = _appContext.GetCurrentUser();
            if (currentUser != null)
            {
                if (currentUser.Username == _currentRoom?.HostUsername)
                {
                    closeRoomButton_Click(sender, e);
                } 
                else
                {
                    leaveRoomButton_Click(sender, e);
                }
            }
        }
    }
}