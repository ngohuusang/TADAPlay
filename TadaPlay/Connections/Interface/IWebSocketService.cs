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
        /// <summary>
        /// The account logged in on another PC, which replaced this session. The service has
        /// already stopped reconnecting; the argument is the server's message for the player.
        /// </summary>
        event EventHandler<string> OnSessionReplaced;

        bool IsConnected { get; }

        void Connect();
        void Disconnect();
        Task<bool> SendMessageAsync(object message);
        Task<bool> SendRawAsync(string message);
        void RefreshAsync();
    }
}
