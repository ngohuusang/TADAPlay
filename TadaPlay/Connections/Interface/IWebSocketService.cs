using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TadaPlay.Websockets.Models;

namespace TadaPlay.Websockets.Interface
{
    public interface IWebSocketService : IDisposable
    {
        event EventHandler<WebSocketMessageEventArgs> OnMessageReceived;
        event EventHandler OnConnected;
        event EventHandler OnDisconnected;
        event EventHandler<string> OnErrorOccurred;
        event EventHandler<PingUpdateEventArgs> OnPingUpdate;
        event EventHandler<string> OnConnectionStatusChanged;

        bool IsConnected { get; }

        void Connect();
        void Disconnect();
        Task<bool> SendMessageAsync(object message);
        Task<bool> SendRawAsync(string message);
        void StartPing();
        void StopPing();

        Task<bool> KickUserFromRoomAsync(string roomId, string userNameToKick);
        Task<bool> CloseRoomAsync(string roomId);
        Task<bool> CreateRoomAsync(string roomName);
        void RefreshAsync();
        Task<bool> LeaveRoomAsync();
    }
}
