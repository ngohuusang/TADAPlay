using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using AntdUI.Chat;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using TadaPlay.Common.Models;
using TadaPlay.Contexts;
using TadaPlay.Contexts.Interfaces;
using TadaPlay.Logger;
using TadaPlay.Services;
using TadaPlay.Websockets;
using TadaPlay.Websockets.Interface;
using TadaPlay.Websockets.Models;

namespace TadaPlay.Controls
{
    public partial class Home : UserControl
    {
        private readonly MainForm mainForm;
        private readonly IWebSocketService webSocketService;
        private readonly IAppContext appContext;
        private readonly IServiceProvider serviceProvider;
        private TaskCompletionSource<bool> roomCreationTcs;
        private string pendingRoomCreationId = null;

        public event EventHandler LogoutRequested;

        public Home(MainForm _mainForm, IWebSocketService _webSocketService, IAppContext _appContext, IServiceProvider _serviceProvider)
        {
            InitializeComponent();
            mainForm = _mainForm;
            webSocketService = _webSocketService;
            appContext = _appContext;
            serviceProvider = _serviceProvider;
        }

        #region AppContext events
        // --- AppContext Event Handlers ---
        private void AppContext_OnCurrentUserUpdated(object sender, EventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() =>
                {
                    DebugLogger.Info($"Home: Current user model updated by AppContext.");
                    UpdateUiBasedOnLobbyState();
                });
            }
            else
            {
                UpdateUiBasedOnLobbyState();
                DebugLogger.Warn($"[HOME_CONTROL - No UI Handle] AppContext Current User Update.");
            }

        }

        private void AppContext_OnOnlineUsersUpdated(object sender, EventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() =>
                {
                    UpdateOnlineUsersListView(appContext.AllOnlineUsers);
                    DebugLogger.Info($"Home: Online users list updated by AppContext. Count: {appContext.AllOnlineUsers.Count}");
                    UpdateUiBasedOnLobbyState();
                });
            }
            else
            {
                UpdateOnlineUsersListView(appContext.AllOnlineUsers);
                DebugLogger.Warn($"[HOME_CONTROL - No UI Handle] AppContext Online Users Update.");
            }
        }

        private void AppContext_OnActiveRoomsUpdated(object sender, EventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() =>
                {
                    UpdateRoomsListView(appContext.AllActiveRooms);
                    DebugLogger.Info($"Home: Active rooms list updated by AppContext. Count: {appContext.AllActiveRooms.Count}");
                    UpdateUiBasedOnLobbyState();
                });
            }
            else
            {
                UpdateRoomsListView(appContext.AllActiveRooms);
                DebugLogger.Info($"Home: Active rooms list updated by AppContext. Count: {appContext.AllActiveRooms.Count}");
                UpdateUiBasedOnLobbyState();
            }
        }

        private void AppContext_OnCurrentRoomDetailsUpdated(object sender, EventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(() =>
                {
                    DebugLogger.Info($"Home: Current room details updated by AppContext. Room: {appContext.CurrentRoomDetails?.Name ?? "None"}");
                    UpdateUiBasedOnLobbyState();
                });
            }
            else
            {
                UpdateUiBasedOnLobbyState();
                DebugLogger.Warn($"[HOME_CONTROL - No UI Handle] AppContext Current Room Update.");
            }
        }
        #endregion

        private void Home_Load(object sender, EventArgs e)
        {
            // Subscribe to WebSocketService events here, just like before
            webSocketService.OnConnected += WebSocketService_OnConnected!;
            webSocketService.OnDisconnected += WebSocketService_OnDisconnected!;
            webSocketService.OnErrorOccurred += WebSocketService_OnErrorOccurred!;
            webSocketService.OnPingUpdate += WebSocketService_OnPingUpdate!;
            webSocketService.OnConnectionStatusChanged += WebSocketService_OnConnectionStatusChanged!;

            appContext.OnCurrentUserUpdated += AppContext_OnCurrentUserUpdated;
            appContext.OnOnlineUsersUpdated += AppContext_OnOnlineUsersUpdated;
            appContext.OnActiveRoomsUpdated += AppContext_OnActiveRoomsUpdated;

            this.roomTable.SelectIndexChanged += (s, e) => UpdateUiBasedOnLobbyState();

            UpdateUiBasedOnLobbyState();

            webSocketService.Connect();
        }

        private async Task createRoom()
        {
            if (!webSocketService.IsConnected)
            {
                mainForm.BeginInvoke(new Action(() =>
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(mainForm, AntdUI.Localization.Get("Error", "Lỗi"), AntdUI.Localization.Get("NotConnectedToServer", "Chưa kết nối đến máy chủ."), AntdUI.TType.Error)
                    { CancelText = null, OkText = AntdUI.Localization.Get("CloseButton", "Đóng") });
                }));
                return;
            }
            if (appContext.GetCurrentUser()?.CurrentRoomId != null)
            {
                mainForm.BeginInvoke(new Action(() =>
                {
                    AntdUI.Modal.open(new AntdUI.Modal.Config(mainForm, AntdUI.Localization.Get("Error", "Lỗi"), AntdUI.Localization.Get("AlreadyInRoom", "Bạn đã ở trong một phòng. Vui lòng rời khỏi trước."), AntdUI.TType.Warn)
                    { CancelText = null, OkText = AntdUI.Localization.Get("CloseButton", "Đóng") });
                }));
                return;
            }

            string roomName = "Phòng của " + appContext.GetCurrentUser()?.FullName;

            roomCreationTcs = new TaskCompletionSource<bool>();
            pendingRoomCreationId = null;

            // Temporarily subscribe to OnMessageReceived for this specific response (via AppContext)
            // The handler will be called by AppContext's ProcessWebSocketMessage
            EventHandler appContextCreateRoomHandler = null;
            appContextCreateRoomHandler = (s, e) => // This handler reacts to AppContext's events
            {
                mainForm.BeginInvoke(new Action(() =>
                { // Marshal to UI thread (if needed for MessageBox or UI changes)
                    // The AppContext.OnActiveRoomsUpdated event implies room list is fresh
                    ClientRoom createdRoom = appContext.AllActiveRooms.FirstOrDefault(r => r.Id == pendingRoomCreationId);
                    User currentUser = appContext.GetCurrentUser();

                    if (createdRoom != null && currentUser?.CurrentRoomId == pendingRoomCreationId)
                    {
                        // Found the room in the list and current user is now in it
                        roomCreationTcs?.TrySetResult(true);
                    }
                    else if (pendingRoomCreationId != null && !appContext.AllActiveRooms.Any(r => r.Id == pendingRoomCreationId))
                    {
                        // If we were waiting for it, but it didn't appear (and not an explicit success/error message yet)
                        // This might be a server-side error not explicitly communicated.
                        // Or a different client error message.
                        // For this, we still rely on the explicit 'success/error' message from the server below.
                    }
                }));
            };

            appContext.OnActiveRoomsUpdated += appContextCreateRoomHandler; // Subscribe to app context room updates


            // --- New handler for explicit success/error from server via AppContext ---
            EventHandler<WebSocketMessageEventArgs> explicitServerResponseHandler = null;
            explicitServerResponseHandler = (s, e) =>
            {
                if (mainForm != null && mainForm.IsHandleCreated)
                {
                    mainForm.BeginInvoke(new Action(() =>
                    { // Marshal to UI thread
                        try
                        {
                            var data = JObject.Parse(e.Data);
                            bool isSuccess = data["success"]?.ToObject<bool>() ?? false;
                            string command = data["command"]?.ToString();
                            string errorMsg = data["error"]?.ToString();
                            string roomId = data["room_id"]?.ToString();

                            if (command == "create_room")
                            {
                                if (isSuccess)
                                {
                                    pendingRoomCreationId = roomId; // Store the new room ID from server confirmation
                                    roomCreationTcs?.TrySetResult(true);
                                }
                                else
                                {
                                    roomCreationTcs?.TrySetException(new InvalidOperationException(errorMsg ?? "Unknown error during room creation."));
                                }
                                // Unsubscribe this handler after receiving the explicit response
                                webSocketService.OnMessageReceived -= explicitServerResponseHandler;
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.Error($"Home: Error processing explicit create_room response: {ex.Message}");
                            roomCreationTcs?.TrySetException(ex);
                            webSocketService.OnMessageReceived -= explicitServerResponseHandler;
                        }
                    }));
                }
            };
            webSocketService.OnMessageReceived += explicitServerResponseHandler; // This is directly from WebSocketService


            await AntdUI.Spin.open(mainForm, AntdUI.Localization.Get("CreatingRoom", "Đang tạo phòng ..."), async config =>
            {
                try
                {
                    bool sendSuccess = await webSocketService.CreateRoomAsync(roomName);
                    if (!sendSuccess)
                    {
                        throw new InvalidOperationException("Failed to send create room message to server.");
                    }

                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
                    var completedTask = await Task.WhenAny(roomCreationTcs.Task, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        throw new TimeoutException("Server did not respond to room creation request in time.");
                    }

                    bool roomCreatedSuccessfully = await roomCreationTcs.Task;

                    if (roomCreatedSuccessfully)
                    {
                        ClientRoom newRoomData = appContext.AllActiveRooms.FirstOrDefault(r => r.Id == pendingRoomCreationId);

                        if (newRoomData != null)
                        {
                            mainForm.BeginInvoke(new Action(() => // UI thread
                            {
                                RoomDetailForm roomForm = serviceProvider.GetRequiredService<RoomDetailForm>();
                                roomForm.SetRoom(newRoomData);
                                roomForm.ShowDialog(mainForm);
                            }));
                        }
                        else
                        {
                            throw new InvalidOperationException("Room details not found in active rooms list after creation.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    mainForm.BeginInvoke(new Action(() => { AntdUI.Modal.open(new AntdUI.Modal.Config(mainForm, AntdUI.Localization.Get("CreateRoomCancelled", "Tạo phòng bị hủy"), AntdUI.Localization.Get("CreateRoomCancelledContent", "Thao tác tạo phòng đã bị hủy."), AntdUI.TType.Warn) { CancelText = null, OkText = AntdUI.Localization.Get("CloseButton", "Đóng") }); }));
                }
                catch (TimeoutException)
                {
                    mainForm.BeginInvoke(new Action(() =>
                    {
                        AntdUI.Modal.open(new AntdUI.Modal.Config(mainForm, AntdUI.Localization.Get("CreateRoomTimeout", "Thời gian chờ tạo phòng đã hết"), AntdUI.Localization.Get("CreateRoomTimeoutContent", "Máy chủ không phản hồi kịp thời."), AntdUI.TType.Error) { CancelText = null, OkText = AntdUI.Localization.Get("CloseButton", "Đóng") });

                    }));
                }
                catch (Exception ex)
                {
                    mainForm.BeginInvoke(new Action(() =>
                    {
                        DebugLogger.Error($"Home Control: Error creating room: {ex.Message}");
                        AntdUI.Modal.open(new AntdUI.Modal.Config(mainForm, AntdUI.Localization.Get("CreateRoomError", "Lỗi tạo phòng"), AntdUI.Localization.Get("CreateRoomErrorContent", ex.InnerException?.Message ?? ex.Message), AntdUI.TType.Error) { CancelText = null, OkText = AntdUI.Localization.Get("CloseButton", "Đóng") });
                    }));
                }
                finally
                {
                    // Ensure handler is removed even if spin is canceled or errors
                    if (appContextCreateRoomHandler != null)
                    {
                        appContext.OnActiveRoomsUpdated -= appContextCreateRoomHandler; // Unsubscribe this one
                    }
                    if (explicitServerResponseHandler != null)
                    {
                        webSocketService.OnMessageReceived -= explicitServerResponseHandler; // Ensure this is also unsubscribed
                    }
                }
            }, () => { System.Diagnostics.Debug.WriteLine("Hoàn tất"); });
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

                userItem.Icon = Properties.Resources.user_icon; //TODO change avatar
                userItem.Text = nickname;
                userItem.Name = username;
                userItem.Time = status;
                if (status == "host") userItem.TimeColor = Color.Red;
                if (status == "joined") userItem.TimeColor = Color.Orange;
                if (status == "online") userItem.TimeColor = Color.Green;

                userList.Items.Add(userItem);
            }
        }

        private void UpdateRoomsListView(IReadOnlyList<ClientRoom> rooms)
        {
            roomTable.DataSource = rooms;
        }

        private void UpdateUiBasedOnLobbyState()
        {
            userAvatar.Badge = "A+";
            usernameLabel.Text = appContext.GetCurrentUser()?.Username ?? "Chưa đăng nhập";
            rankLabel.Text = appContext.GetCurrentUser()?.Ranking ?? "Chưa có hạng";
            // Create Room button always enabled if user is connected and not currently hosting/in a full room
            // Assuming we don't allow hosting while already in a room.
            createRoomButton.Enabled = webSocketService.IsConnected &&
                                       (appContext.GetCurrentUser()?.CurrentRoomId == null || appContext.GetCurrentUser()?.Status == "online"); // Only if not in a room
        }

        private void WebSocketService_OnConnectionStatusChanged(object sender, string statusMessage)
        {
            if (this.InvokeRequired) // Check if invoking is needed (i.e., not on UI thread)
            {
                // Only call BeginInvoke if the handle has actually been created
                if (this.IsHandleCreated)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        DebugLogger.Info($"Connection Status: {statusMessage}");
                        // Update a status label on your MainForm, e.g., toolStripStatusLabelConnection.Text = statusMessage;
                    }));
                }
                else
                {
                    // Log to console or a different mechanism if UI handle not ready yet.
                    // This happens when the event fires BEFORE the form is fully shown.
                    Console.WriteLine($"[WARNING - No UI Handle] Connection Status: {statusMessage}");
                    DebugLogger.Warn($"[WARNING - No UI Handle] Connection Status: {statusMessage}");
                }
            }
            else // Already on the UI thread, so direct access is fine
            {
                DebugLogger.Info($"Connection Status: {statusMessage}");
                // Update status label directly
            }
        }

        private void WebSocketService_OnConnected(object sender, EventArgs e)
        {
            this.BeginInvoke(new Action(() =>
            {
                DebugLogger.Info("WebSocketService reported connected. Attempting initial refresh.");
                webSocketService.RefreshAsync();
                UpdateUiBasedOnLobbyState();
            }));
        }

        private void WebSocketService_OnDisconnected(object sender, EventArgs e)
        {
            this.BeginInvoke(new Action(() =>
            {
                DebugLogger.Info("WebSocketService reported disconnected.");
                UpdateUiBasedOnLobbyState();
            }));
        }

        private void WebSocketService_OnErrorOccurred(object sender, string errorMessage)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(new Action(() =>
                {
                    // Display error message to the user (e.g., MessageBox or status label)
                    AntdUI.Notification.error(mainForm, AntdUI.Localization.Get("ConnectionErrorTitle", "Lỗi kết nối"), AntdUI.Localization.Get("ConnectionErrorContent", errorMessage), AntdUI.TAlignFrom.Bottom, Font);
                    DebugLogger.Error($"Home Control WS Error: {errorMessage}");
                }));
            }
            else
            {
                Console.Error.WriteLine($"[HOME_CONTROL - No UI Handle] WS Error: {errorMessage}");
                DebugLogger.Error($"[HOME_CONTROL - No UI Handle] WS Error: {errorMessage}");
            }
        }

        private void WebSocketService_OnPingUpdate(object sender, PingUpdateEventArgs e)
        {
            if (this.InvokeRequired && this.IsHandleCreated)
            {
                this.BeginInvoke(new Action(() =>
                {
                    // Example: Update a ping status label within Home control
                    // this.pingLabel.Text = $"Ping: {e.PingMs}ms";
                    DebugLogger.Info($"Home Control Ping Update: {e.PingMs}ms");
                }));
            }
            else
            {
                Console.WriteLine($"[HOME_CONTROL - No UI Handle] Ping Update: {e.PingMs}ms");
                DebugLogger.Warn($"[HOME_CONTROL - No UI Handle] Ping Update: {e.PingMs}ms");
            }
        }

        private async void createRoomButton_Click(object sender, EventArgs e)
        {
            createRoom();
        }

        private void roomTable_CellClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() =>
                {
                    UpdateUiBasedOnLobbyState();
                }));
            }
        }

        private void refreshRoomButton_Click(object sender, EventArgs e)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() =>
                {
                    webSocketService.RefreshAsync();
                }));
            }
            else
            {
                webSocketService.RefreshAsync();
            }
        }

        private void logoutButton_Click(object sender, EventArgs e)
        {
            AntdUI.Modal.open(new AntdUI.Modal.Config(mainForm, "Đăng xuất", "Bạn chắc chắn muốn thoát tài khoản?", AntdUI.TType.Warn)
            {
                OnOk = config =>
                {
                    LogoutRequested?.Invoke(this, EventArgs.Empty);
                    return true;
                }
            });
        }
    }
}
