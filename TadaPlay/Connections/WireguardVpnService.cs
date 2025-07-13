using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using TadaPlay.Logger;
using TadaPlay.Connections.Interface;
using Vanara.PInvoke;
using WireGuardNT_PInvoke;
using WireGuardNT_PInvoke.WireGuard;
using static Vanara.PInvoke.IpHlpApi;

namespace TadaPlay.Connections
{
    public class WireguardVpnService : IWireGuardVpnService
    {
        private const string ADATER_NAME = "TADAVPNAdapter"; // Default adapter name, can be overridden in constructor
        private const string CLIENT_TUNNEL_TYPE = "client"; // Default tunnel type for client connections

        private Adapter _adapter;
        private Guid _adapterGuid;
        private NET_LUID _adapterLuid;
        private WgConfig _wgConfig; // WireGuard configuration

        // --- New/Updated Fields for Robustness and DI ---
        private string _currentIpAddress; // Stores the IP address after successful connection
        private bool _isConnecting = false; // Flag to prevent multiple concurrent connect attempts
        private bool _isExplicitlyDisconnected = false; // Flag to prevent auto-reconnect if user explicitly disconnected

        // Reconnection Logic
        private System.Timers.Timer _reconnectTimer;
        private int _reconnectAttempt = 0;
        private readonly int[] _reconnectDelays = { 1000, 2000, 5000, 10000, 15000, 30000 }; // Delays in ms
        private const int MaxReconnectAttempts = 5; // Max attempts before giving up

        // --- Events from IVpnService ---
        public event EventHandler<string> OnStatusChanged;
        public event EventHandler<string> OnErrorOccurred;
        public event EventHandler OnConnected;
        public event EventHandler OnDisconnected;
        public event EventHandler<string> OnIpAddressChanged; // Reports the VPN IP address

        public bool IsConnected
        {
            get { return _adapter != null && _adapter.GetAdapterState() == WireGuardAdapterState.WIREGUARD_ADAPTER_STATE_UP; }
        }

        public string CurrentIpAddress => _currentIpAddress;

        // Constructor for DI. _configPath should now be relative to base directory
        public WireguardVpnService()
        {
            // Initialize reconnect timer
            _reconnectTimer = new System.Timers.Timer { AutoReset = false };
            _reconnectTimer.Elapsed += async (s, e) =>
            {
                _reconnectTimer.Dispose();
                _reconnectTimer = null; // Allow re-creation
                await AttemptConnectInternalAsync();
            };
        }

        // --- Public Connect/Disconnect Methods (from IVpnService) ---
        public async Task<bool> ConnectAsync(string configContent)
        {
            if (_isConnecting)
            {
                DebugLogger.Info("WireguardVPNService: Already attempting to connect VPN.");
                return false;
            }
            if (IsConnected)
            {
                DebugLogger.Info("WireguardVPNService: VPN is already connected.");
                OnStatusChanged?.Invoke(this, "VPN already connected.");
                OnConnected?.Invoke(this, EventArgs.Empty);
                // Report IP immediately if already connected
                _currentIpAddress = GetAdapterIpAddress();
                OnIpAddressChanged?.Invoke(this, _currentIpAddress);
                return true;
            }

            _isExplicitlyDisconnected = false; // Reset flag for new connect
            _reconnectAttempt = 0; // Reset retry counter

            var _configContent = configContent ?? throw new ArgumentNullException(nameof(configContent), "WireGuard configuration content cannot be null.");
            var configAllLines = _configContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (!_adapter.ParseConfFile(configAllLines, out _wgConfig))
            {
                OnErrorOccurred?.Invoke(this, "Failed to parse config.");
                DebugLogger.Error("WireguardVPNService: Failed to parse config file.");
                return false;
            }

            return await AttemptConnectInternalAsync();
        }

        public async Task DisconnectAsync()
        {
            _isExplicitlyDisconnected = true; // Set flag to prevent auto-reconnect
            StopReconnectTimer(); // Stop any pending reconnect attempts

            if (_adapter != null)
            {
                try
                {
                    DebugLogger.Info("WireguardVPNService: Setting VPN state down...");
                    OnStatusChanged?.Invoke(this, "Disconnecting VPN...");

                    // It's often best to run P/Invoke calls on a background thread
                    await Task.Run(() =>
                    {
                        _adapter.SetStateDown(); // Blocking call
                    });

                    _adapter.Dispose();
                    _adapter = null;
                    _currentIpAddress = null;

                    DebugLogger.Info("WireguardVPNService: VPN Disconnected.");
                    OnStatusChanged?.Invoke(this, "VPN Disconnected.");
                    OnDisconnected?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"WireguardVPNService: Error during VPN disconnect: {ex.Message}");
                    OnErrorOccurred?.Invoke(this, "Lỗi khi ngắt kết nối VPN: " + ex.Message);
                }
            }
            else
            {
                DebugLogger.Info("WireguardVPNService: VPN not active, nothing to disconnect.");
                OnStatusChanged?.Invoke(this, "VPN not active.");
            }
        }

        // --- Internal Connect/Reconnect Logic ---
        private async Task<bool> AttemptConnectInternalAsync()
        {
            if (_isExplicitlyDisconnected)
            {
                DebugLogger.Info("WireguardVPNService: Explicitly disconnected, aborting reconnect attempt.");
                return false;
            }

            _isConnecting = true;
            OnStatusChanged?.Invoke(this, $"Connecting VPN (attempt {_reconnectAttempt + 1}/{MaxReconnectAttempts})...");
            DebugLogger.Info($"WireguardVPNService: Attempting VPN connection (attempt {_reconnectAttempt + 1}).");

            try
            {
                // Ensure the adapter is properly disposed before new attempt
                _adapter?.Dispose();
                _adapter = null;

                // Run the core WireGuard P/Invoke operations on a background thread
                // to avoid blocking the caller (e.g., UI thread)
                bool success = await Task.Run(() =>
                {
                    // Call the architecture helper
                    AddArch();

                    _adapterGuid = Guid.NewGuid(); // New GUID for each connect attempt
                    _adapter = new Adapter(ADATER_NAME, CLIENT_TUNNEL_TYPE);
                    _adapter.EventInfoMessage += (sender, arg) => DebugLogger.Info($"WireguardVPNService [INFO]: {arg.Message}"); // Log directly
                    _adapter.EventErrorMessage += (sender, arg) => DebugLogger.Error($"WireguardVPNService [ERROR]: {arg.Message} Win32Error: {arg.Win32ErrorNum}"); // Log directly

                    _adapter.Init(ref _adapterGuid, out _adapterLuid);

                    // --- Clean up old routes from previous sessions (important for stability) ---
                    var lastError = GetIpForwardTable2(Ws2_32.ADDRESS_FAMILY.AF_INET, out MIB_IPFORWARD_TABLE2 table);
                    if (!lastError.Failed)
                    {
                        for (var i = 0; i < table.NumEntries; i++)
                        {
                            var row = table.Table[i];
                            // Only delete routes associated with this adapter (if it somehow persisted)
                            if (row.InterfaceLuid.Equals(_adapterLuid))
                            {
                                DeleteIpForwardEntry2(ref table.Table[i]);
                            }
                        }
                    }

                    // --- Add new routes for peers ---
                    for (var i = 0; i < _wgConfig.LoctlWireGuardConfig.WgPeerConfigs.Length; i++)
                    {
                        var peerConfig = _wgConfig.LoctlWireGuardConfig.WgPeerConfigs[i];
                        MIB_IPFORWARD_ROW2 row;
                        InitializeIpForwardEntry(out row);
                        row.InterfaceLuid = _adapterLuid;
                        row.Metric = 1; // Lower metric for higher priority

                        var maskedIp = IPNetwork2.Parse("" + peerConfig.allowdIp.V4.Addr, peerConfig.allowdIp.Cidr);

                        row.DestinationPrefix.Prefix.Ipv4.sin_addr = new Ws2_32.IN_ADDR(maskedIp.Network.GetAddressBytes());
                        row.DestinationPrefix.Prefix.si_family = Ws2_32.ADDRESS_FAMILY.AF_INET;
                        row.DestinationPrefix.PrefixLength = maskedIp.Cidr;

                        row.Protocol = MIB_IPFORWARD_PROTO.MIB_IPPROTO_LOCAL;
                        row.NextHop.Ipv4.sin_addr = Ws2_32.IN_ADDR.INADDR_ANY; // Next hop is the adapter itself
                        row.NextHop.si_family = Ws2_32.ADDRESS_FAMILY.AF_INET;

                        lastError = CreateIpForwardEntry2(ref row);
                        if (lastError.Failed)
                        {
                            DebugLogger.Error($"WireguardVPNService: CreateIpForwardEntry2 [{i}] failed: {lastError}");
                            // Continue on error for other routes, but log it
                        }
                    }

                    // --- Set IP address ---
                    InitializeUnicastIpAddressEntry(out MIB_UNICASTIPADDRESS_ROW unicastIpAddressRow);
                    unicastIpAddressRow.InterfaceLuid = _adapterLuid;
                    unicastIpAddressRow.Address.Ipv4.sin_addr = new Ws2_32.IN_ADDR(_wgConfig.InterfaceAddress.GetAddressBytes());
                    unicastIpAddressRow.Address.Ipv4.sin_family = Ws2_32.ADDRESS_FAMILY.AF_INET;
                    unicastIpAddressRow.OnLinkPrefixLength = _wgConfig.InterfaceNetwork.Cidr; // CIDR from config
                    unicastIpAddressRow.DadState = NL_DAD_STATE.IpDadStatePreferred;

                    lastError = CreateUnicastIpAddressEntry(ref unicastIpAddressRow);
                    if (lastError.Failed)
                    {
                        OnErrorOccurred?.Invoke(this, "Failed to set IP address: " + lastError);
                        DebugLogger.Error("WireguardVPNService: CreateUnicastIpAddressEntry failed: " + lastError);
                        return false; // Fatal, cannot proceed without IP
                    }

                    // --- Set interface properties (MTU, etc.) ---
                    InitializeIpInterfaceEntry(out MIB_IPINTERFACE_ROW ipInterfaceRow);
                    ipInterfaceRow.InterfaceLuid = _adapterLuid;
                    ipInterfaceRow.Family = Ws2_32.ADDRESS_FAMILY.AF_INET;

                    lastError = GetIpInterfaceEntry(ref ipInterfaceRow);
                    if (!lastError.Failed)
                    {
                        ipInterfaceRow.ForwardingEnabled = true;
                        ipInterfaceRow.UseAutomaticMetric = false;
                        ipInterfaceRow.Metric = 0;
                        ipInterfaceRow.NlMtu = _wgConfig.InterfaceMtu;
                        ipInterfaceRow.SitePrefixLength = 0; // Usually 0 for non-site-specific VPNs

                        lastError = SetIpInterfaceEntry(ipInterfaceRow);
                        if (lastError.Failed)
                        {
                            DebugLogger.Warn("WireguardVPNService: SetIpInterfaceEntry failed: " + lastError); // Warn, not necessarily fatal
                        }
                    }

                    // --- Set DNS (via netsh external process, this is blocking) ---
                    foreach (var dnsAddress in _wgConfig.DnsAddresses)
                    {
                        try
                        {
                            // This blocks, but it's within the Task.Run so it won't block the calling thread
                            var process = Process.Start(new ProcessStartInfo("netsh.exe", $"interface ipv4 add dnsservers name=\"{ADATER_NAME}\" address={dnsAddress} validate=no")
                            {
                                CreateNoWindow = true,
                                UseShellExecute = false
                            });
                            process?.WaitForExit(5000); // Wait up to 5 seconds
                            if (process?.ExitCode != 0)
                            {
                                DebugLogger.Warn($"WireguardVPNService: netsh for DNS failed with exit code {process?.ExitCode} for {dnsAddress}");
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.Error("WireguardVPNService: Failed to set DNS via netsh: " + ex.Message);
                            // This might not be fatal, but log it
                        }
                    }

                    // --- Activate the WireGuard adapter ---
                    _adapter.SetConfiguration(_wgConfig); // Apply WireGuard specific configuration
                    _adapter.SetStateUp(); // Bring the adapter up (VPN connection active)

                    return true; // Connection successful
                });

                if (success)
                {
                    _isConnecting = false;
                    _reconnectAttempt = 0; // Reset on success
                    StopReconnectTimer(); // Stop any pending reconnect timers

                    _currentIpAddress = GetAdapterIpAddress(); // Get IP after successful connection
                    DebugLogger.Info("WireguardVPNService: VPN Connected.");
                    OnStatusChanged?.Invoke(this, "VPN Connected.");
                    OnConnected?.Invoke(this, EventArgs.Empty);
                    OnIpAddressChanged?.Invoke(this, _currentIpAddress);
                    return true;
                }
                else
                {
                    // If success is false from Task.Run, it means an internal error occurred
                    DebugLogger.Error("WireguardVPNService: VPN connection failed internally.");
                    OnStatusChanged?.Invoke(this, "VPN connection failed.");
                    HandleConnectionLoss();
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Catch any exceptions from the Task.Run block or external logic
                _isConnecting = false;
                DebugLogger.Error($"WireguardVPNService: Exception during VPN connect attempt: {ex.Message}");
                OnErrorOccurred?.Invoke(this, "Lỗi kết nối VPN: " + ex.Message);
                OnStatusChanged?.Invoke(this, "VPN connection failed.");
                HandleConnectionLoss(); // Attempt reconnect on any exception
                return false;
            }
        }

        private void HandleConnectionLoss()
        {
            if (_isExplicitlyDisconnected) return;

            _reconnectAttempt++;
            if (_reconnectAttempt > MaxReconnectAttempts)
            {
                DebugLogger.Error($"WireguardVPNService: Exceeded max reconnect attempts ({MaxReconnectAttempts}). Giving up.");
                OnErrorOccurred?.Invoke(this, $"Kết nối VPN không ổn định, đã thử lại {MaxReconnectAttempts} lần nhưng không thành công. Vui lòng kiểm tra lại cấu hình hoặc mạng.");
                OnStatusChanged?.Invoke(this, "VPN reconnect failed (Max retries)");
                _isConnecting = false; // Allow manual connect
                _reconnectAttempt = 0; // Reset for next manual attempt
                return;
            }

            int delay = _reconnectDelays[Math.Min(_reconnectAttempt - 1, _reconnectDelays.Length - 1)];
            DebugLogger.Info($"WireguardVPNService: Attempting reconnect in {delay}ms (attempt {_reconnectAttempt})...");
            OnStatusChanged?.Invoke(this, $"Reconnecting VPN in {delay / 1000}s (attempt {_reconnectAttempt})...");

            _reconnectTimer?.Stop();
            _reconnectTimer.Interval = delay;
            _reconnectTimer.Start();
        }

        private void StopReconnectTimer()
        {
            _reconnectTimer?.Stop();
        }

        // --- Helper for IP Address ---
        private string GetAdapterIpAddress()
        {
            try
            {
                var ipAddress = _wgConfig.InterfaceAddress.ToString();
               
                return ipAddress;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"WireguardVPNService: Error getting adapter IP: {ex.Message}");
                return null;
            }
        }


        // --- P/Invoke Setup for WireGuardNT (Keep this as it is) ---
        private void AddArch()
        {
            string[] first = new string[1]
            {
                Environment.GetEnvironmentVariable("PATH") ?? string.Empty
            };
            string[] second = new string[2]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "x86"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "x64")
            };
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator.ToString(), first.Concat(second)));
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
                DisconnectAsync().Wait(5000); // Attempt sync disconnect and wait briefly
                _reconnectTimer?.Dispose();
            }
            // No unmanaged resources that require explicit handling other than adapter disposal in DisconnectAsync
        }

        ~WireguardVpnService()
        {
            Dispose(false);
        }
    }
}
