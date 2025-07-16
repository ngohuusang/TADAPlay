using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using TadaPlay.Websockets.Interface;
using TadaPlay.Websockets.Models;
using TadaPlay.Logger;
using TadaPlay.Contexts.Interfaces;
using System.Net.WebSockets;
using System.Threading;
using TadaPlay.Contexts;

namespace TadaPlay.Websockets
{
    public class WebSocketService : IWebSocketService
    {
        private const string WS_BASE_URI = "ws://192.168.1.14:8888/";

        private ClientWebSocket _ws; // The actual WebSocket client
        private CancellationTokenSource _receiveCts; // Manages cancellation for the receive loop
        private System.Timers.Timer _pingTimer; // For sending periodic pings
        private System.Timers.Timer _reconnectTimer; // For managing reconnection delays

        private string _currentFullWsUri; // Full URI including token

        private int _reconnectAttempt = 0;
        private bool _isConnecting = false;
        private bool _isExplicitlyDisconnected = false; // Flag to prevent auto-reconnect on user disconnect

        private readonly int[] _reconnectDelays = { 1000, 2000, 5000, 10000, 15000, 30000 }; // ms delays
        private readonly IAppContext appContext;
        private const int MaxReconnectAttempts = 5; // Max auto-reconnect tries before giving up

        private const int PingInterval = 5000; // 5 seconds
        private const int HighPingThreshold = 300; // ms
        private int _highPingCount = 0; // Consecutive high ping count

        // Events for consumers (MainForm) to subscribe to
        public event EventHandler<WebSocketMessageEventArgs> OnMessageReceived;
        public event EventHandler OnConnected;
        public event EventHandler OnDisconnected;
        public event EventHandler<string> OnErrorOccurred; // For general errors and warnings
        public event EventHandler<PingUpdateEventArgs> OnPingUpdate; // For reporting ping latency
        public event EventHandler<string> OnConnectionStatusChanged; // For UI to display connection state

        public bool IsConnected => _ws?.State == WebSocketState.Open;

        // Public constructor for Dependency Injection (DI)
        public WebSocketService(IAppContext _appContext)
        {
            // Initialize reconnect timer here, but don't start it yet.
            _reconnectTimer = new System.Timers.Timer { AutoReset = false };
            _reconnectTimer.Elapsed += async (s, e) => // Async lambda for reconnect
            {
                StopReconnectTimer();

                await AttemptConnectInternalAsync();
            };
            appContext = _appContext;
        }

        // --- Connection Management ---
        public void Connect()
        {
            if (_isConnecting)
            {
                DebugLogger.Info("WebSocketService: Already attempting to connect.");
                return;
            }
            if (IsConnected)
            {
                DebugLogger.Info("WebSocketService: Already connected.");
                return;
            }

            string jwtToken = appContext.GetJwtTokenSetting();
            if (string.IsNullOrEmpty(jwtToken))
            {
                DebugLogger.Error("WebSocketService: Cannot connect. JWT token is null or empty in AppContext.");
                OnErrorOccurred?.Invoke(this, "Không có mã thông báo JWT. Vui lòng đăng nhập lại.");
                OnConnectionStatusChanged?.Invoke(this, "Failed: No JWT Token");
                return;
            }

            _currentFullWsUri = $"{WS_BASE_URI}?token={jwtToken}";

            _isExplicitlyDisconnected = false;
            _reconnectAttempt = 0;

            _ = AttemptConnectInternalAsync();
        }

        private async Task AttemptConnectInternalAsync()
        {
            if (_isExplicitlyDisconnected)
            {
                DebugLogger.Info("WebSocketService: Explicitly disconnected, aborting reconnect attempt.");
                return;
            }

            _isConnecting = true;
            OnConnectionStatusChanged?.Invoke(this, $"Connecting (attempt {_reconnectAttempt + 1}/{MaxReconnectAttempts})...");
            DebugLogger.Info($"WebSocketService: Attempting connection ... (attempt {_reconnectAttempt + 1}).");

            if (_ws != null)
            {
                if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.Connecting)
                {
                    try { _receiveCts?.Cancel(); await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None); }
                    catch (Exception ex) { DebugLogger.Warn($"WebSocketService: Error during old WS close for reconnect: {ex.Message}"); }
                }
                _ws.Dispose();
            }
            StopPing();

            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            _receiveCts?.Dispose();
            _receiveCts = new CancellationTokenSource();

            try
            {
                await _ws.ConnectAsync(new Uri(_currentFullWsUri), _receiveCts.Token);

                _isConnecting = false;
                _reconnectAttempt = 0;
                StopReconnectTimer(); // This will stop the timer, not dispose it.

                DebugLogger.Info("✅ WebSocketService: Connected.");
                OnConnected?.Invoke(this, EventArgs.Empty);
                OnConnectionStatusChanged?.Invoke(this, "Connected");
                StartPing();

                _ = ReceiveLoopAsync(_receiveCts.Token);
            }
            catch (WebSocketException ex)
            {
                _isConnecting = false;
                DebugLogger.Error($"❌ WebSocketService: Connection failed (WebSocketException): {ex.Message}");
                OnErrorOccurred?.Invoke(this, $"Kết nối không thành công: {ex.Message}");
                OnConnectionStatusChanged?.Invoke(this, $"Failed: {ex.Message}");
                HandleConnectionLoss();
            }
            catch (Exception ex)
            {
                _isConnecting = false;
                DebugLogger.Error($"❌ WebSocketService: Connection failed (General Exception): {ex.Message}");
                OnErrorOccurred?.Invoke(this, $"Kết nối gặp lỗi không mong muốn: {ex.Message}");
                OnConnectionStatusChanged?.Invoke(this, $"Failed: {ex.Message}");
                HandleConnectionLoss();
            }
        }

        public async void Disconnect()
        {
            _isExplicitlyDisconnected = true;
            StopReconnectTimer(); // Stop it
            StopPing();

            if (_ws != null && (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.Connecting))
            {
                _receiveCts?.Cancel();
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client initiated disconnect", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    DebugLogger.Warn($"WebSocketService: Error during clean close: {ex.Message}");
                }
            }
            _ws?.Dispose();
            _ws = null;
            DebugLogger.Info("WebSocketService: Explicitly disconnected and disposed.");
            OnConnectionStatusChanged?.Invoke(this, "Disconnected (User action)");
        }

        private void HandleConnectionLoss()
        {
            if (_isExplicitlyDisconnected) return;

            _reconnectAttempt++;
            if (_reconnectAttempt > MaxReconnectAttempts)
            {
                DebugLogger.Error($"WebSocketService: Exceeded max reconnect attempts ({MaxReconnectAttempts}). Giving up.");
                OnErrorOccurred?.Invoke(this, $"Kết nối không ổn định, đã thử lại {MaxReconnectAttempts} lần nhưng không thành công. Vui lòng kiểm tra mạng của bạn.");
                OnConnectionStatusChanged?.Invoke(this, "Failed to connect (Max retries)");
                _isConnecting = false;
                _reconnectAttempt = 0;
                return;
            }

            int delay = _reconnectDelays[Math.Min(_reconnectAttempt - 1, _reconnectDelays.Length - 1)];
            DebugLogger.Info($"WebSocketService: Attempting reconnect in {delay}ms (attempt {_reconnectAttempt})...");
            OnConnectionStatusChanged?.Invoke(this, $"Reconnecting in {delay / 1000}s (attempt {_reconnectAttempt})...");

            // Stop any *running* timer before setting interval and starting.
            // _reconnectTimer will always be non-null here as it's initialized in the constructor.
            _reconnectTimer.Stop();
            _reconnectTimer.Interval = delay;
            _reconnectTimer.Start();
        }

        private void StopReconnectTimer()
        {
            _reconnectTimer?.Stop();
        }

        // --- Sending Messages ---
        public async Task<bool> SendMessageAsync(object message)
        {
            if (!IsConnected)
            {
                DebugLogger.Warn("WebSocketService: Attempted to send message, but not connected."); // Changed to DebugLogger.Warn
                OnErrorOccurred?.Invoke(this, "WebSocket not connected for sending message.");
                return false;
            }
            try
            {
                string json = JsonConvert.SerializeObject(message);
                await SendRawAsync(json);
                DebugLogger.Info($"📤 WebSocketService: Sent message: {json}"); // Changed to DebugLogger.Info
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"WebSocketService: Error sending message: {ex.Message}"); // Changed to DebugLogger.Error
                OnErrorOccurred?.Invoke(this, $"Lỗi gửi tin nhắn: {ex.Message}");

                return false;
            }

            return true; // Indicate success
        }

        public async Task<bool> SendRawAsync(string message)
        {
            if (!IsConnected)
            {
                DebugLogger.Warn("WebSocketService: Attempted to send raw message, but not connected."); // Changed to DebugLogger.Warn
                OnErrorOccurred?.Invoke(this, "WebSocket not connected for sending raw message.");
                return false;
            }
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                DebugLogger.Info($"📤 WebSocketService: Sent raw message: {message.Substring(0, Math.Min(message.Length, 50))}..."); // Changed to DebugLogger.Info
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"WebSocketService: Error sending raw message: {ex.Message}"); // Changed to DebugLogger.Error
                OnErrorOccurred?.Invoke(this, $"Lỗi gửi tin nhắn (raw): {ex.Message}");

                return false; // Indicate failure
            }

            return true; // Indicate success
        }

        // --- Ping/Pong Logic ---
        public void StartPing()
        {
            if (_pingTimer == null)
            {
                _pingTimer = new System.Timers.Timer(PingInterval);
                _pingTimer.Elapsed += async (s, e) => await SendPingAsync(); // Async lambda for ping
            }
            //TODO _pingTimer.Start();
            DebugLogger.Info("WebSocketService: Ping timer started."); // Changed to DebugLogger.Info
        }

        public void StopPing()
        {
            _pingTimer?.Stop();
            _pingTimer?.Dispose();
            _pingTimer = null;
            DebugLogger.Info("WebSocketService: Ping timer stopped and disposed."); // Changed to DebugLogger.Info
        }

        private async Task SendPingAsync()
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                long ts = GetUnixTimeMilliseconds();
                var pingMsg = new { ping = true, ts = ts };
                string json = JsonConvert.SerializeObject(pingMsg);
                await SendRawAsync(json);
                DebugLogger.Info("📡 WebSocketService: Ping sent"); // Changed to DebugLogger.Info
            }
        }

        // --- Receiving Messages ---
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[1024 * 4]; // 4KB buffer for incoming messages
            try
            {
                while (_ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        DebugLogger.Info("WebSocketService: Receive operation cancelled."); // Changed to DebugLogger.Info
                        break; // Exit loop if cancelled
                    }
                    catch (WebSocketException ex)
                    {
                        DebugLogger.Error($"WebSocketService: Receive error (WebSocketException): {ex.Message}"); // Changed to DebugLogger.Error
                        HandleConnectionLoss(); // Attempt reconnect on receive errors
                        break; // Exit loop on error
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error($"WebSocketService: Unhandled error during receive: {ex.Message}"); // Changed to DebugLogger.Error
                        OnErrorOccurred?.Invoke(this, $"Unhandled receive error: {ex.Message}");
                        HandleConnectionLoss();
                        break;
                    }


                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        DebugLogger.Info($"WebSocketService: Received close frame. Status: {result.CloseStatus}, Reason: {result.CloseStatusDescription}"); // Changed to DebugLogger.Info
                        break; // Exit receive loop
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        HandleIncomingMessage(message); // Process the message
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Fatal($"WebSocketService: Critical error in receive loop: {ex.Message}"); // Changed to DebugLogger.Fatal
                OnErrorOccurred?.Invoke(this, $"Critical error in receive loop: {ex.Message}");
            }
            finally
            {
                if (_ws != null && _ws.State != WebSocketState.Closed && _ws.State != WebSocketState.Aborted && !_isExplicitlyDisconnected)
                {
                    DebugLogger.Info("WebSocketService: Receive loop exited, handling connection loss."); // Changed to DebugLogger.Info
                    HandleConnectionLoss();
                }
                else if (_ws == null || _ws.State == WebSocketState.Closed || _ws.State == WebSocketState.Aborted)
                {
                    DebugLogger.Info("WebSocketService: Receive loop exited due to closed/aborted state or null WebSocket."); // Changed to DebugLogger.Info
                }
            }
        }

        private void HandleIncomingMessage(string json)
        {
            try
            {
                var data = JObject.Parse(json);
                if (data["pong"] != null && data["ts"] != null)
                {
                    long tsSent = data["ts"].ToObject<long>();
                    long now = GetUnixTimeMilliseconds();
                    long pingMs = now - tsSent;

                    DebugLogger.Info($"📡 WebSocketService: Ping received: {pingMs} ms"); // Changed to DebugLogger.Info
                    OnPingUpdate?.Invoke(this, new PingUpdateEventArgs(pingMs));

                    if (pingMs > HighPingThreshold)
                    {
                        _highPingCount++;
                        DebugLogger.Warn($"⚠️ WebSocketService: High ping detected ({_highPingCount}/{MaxReconnectAttempts})"); // Changed to DebugLogger.Warn

                        if (_highPingCount >= MaxReconnectAttempts)
                        {
                            _highPingCount = 0;
                            OnErrorOccurred?.Invoke(this,
                                "Kết nối mạng của bạn đang không ổn định. Vui lòng kiểm tra lại kết nối hoặc VPN.");
                        }
                    }
                    else
                    {
                        _highPingCount = 0; // Reset count if ping is good
                    }
                }
                appContext.ProcessWebSocketMessage(json);
                // Raise event for all other messages (user_list, etc.)
                OnMessageReceived?.Invoke(this, new WebSocketMessageEventArgs(json));
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"⚠️ WebSocketService: Lỗi phân tích JSON hoặc xử lý tin nhắn: {ex.Message}"); // Changed to DebugLogger.Error
                OnErrorOccurred?.Invoke(this, $"Lỗi xử lý tin nhắn từ server: {ex.Message}");
            }
        }

        private long GetUnixTimeMilliseconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Sends a command to kick a specific user from a room.
        /// Only the room host should be able to execute this.
        /// </summary>
        /// <param name="roomId">The ID of the room.</param>
        /// <param name="userIdToKick">The WebSocket User ID of the user to kick.</param>
        public async Task<bool> KickUserFromRoomAsync(string roomId, string userIdToKick)
        {
            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(userIdToKick))
            {
                DebugLogger.Error("WebSocketService: KickUserFromRoomAsync: Room ID or User ID to kick cannot be empty.");
                OnErrorOccurred?.Invoke(this, "Room ID or User ID to kick is missing.");
                return false;
            }
            await SendMessageAsync(new
            {
                command = "kick_user",
                room_id = roomId,
                user_id_to_kick = userIdToKick
            });
            DebugLogger.Info($"WebSocketService: Sent kick_user command for user {userIdToKick} in room {roomId}");

            return true; // Indicate success
        }

        /// <summary>
        /// Sends a command to close a specific room.
        /// Only the room host should be able to execute this.
        /// </summary>
        /// <param name="roomId">The ID of the room to close.</param>
        public async Task<bool> CloseRoomAsync(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                DebugLogger.Error("WebSocketService: CloseRoomAsync: Room ID cannot be empty.");
                OnErrorOccurred?.Invoke(this, "Room ID is missing for close_room command.");
                return false;
            }
            await SendMessageAsync(new
            {
                command = "close_room",
                room_id = roomId
            });
            DebugLogger.Info($"WebSocketService: Sent close_room command for room {roomId}");

            return true; // Indicate success
        }

        // --- IDisposable Implementation ---
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose managed resources
                Disconnect(); // Ensure disconnected cleanly
                _reconnectTimer?.Dispose();
                _pingTimer?.Dispose();
                _receiveCts?.Dispose(); // Dispose the cancellation token source
            }
            // No unmanaged resources in this class that require explicit handling
        }

        public Task<bool> CreateRoomAsync(string roomName)
        {
            return SendMessageAsync(new { command = "create_room", room_name = roomName });
        }

        public async void RefreshAsync()
        {
            await SendMessageAsync(new { refresh = "refresh" });
        }

        public Task<bool> LeaveRoomAsync()
        {
           return SendMessageAsync(new { command = "leave_room" });
        }

        // Finalizer (destructor)
        ~WebSocketService()
        {
            Dispose(false);
        }
    }
}
