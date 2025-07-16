using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TadaPlay.Connections.Interface
{
    public interface IWireGuardVpnService : IDisposable
    {
        event EventHandler<string> OnStatusChanged; // General status messages (connecting, connected, disconnecting)
        event EventHandler<string> OnErrorOccurred; // Error messages
        event EventHandler OnConnected; // VPN connection established
        event EventHandler OnDisconnected; // VPN disconnected

        bool IsConnected { get; }

        string CurrentIpAddress { get; }

        Task<bool> InitAdapter(string configContent);
        // Connect method, often triggered by login or user action
        Task<bool> ConnectAsync();

        // Disconnect method, user-initiated or upon app exit
        Task DisconnectAsync();
    }
}
