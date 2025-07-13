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
        event EventHandler<string> OnIpAddressChanged; // Report the new IP address after connection

        bool IsConnected { get; }
        string CurrentIpAddress { get; } // Get the currently assigned VPN IP address

        // Connect method, often triggered by login or user action
        Task<bool> ConnectAsync(string configContent);

        // Disconnect method, user-initiated or upon app exit
        Task DisconnectAsync();

        // You might add a method to get the current IP if it can change dynamically without a full reconnect
        // string GetCurrentIpAddress(); // This is covered by CurrentIpAddress property now.
    }
}
