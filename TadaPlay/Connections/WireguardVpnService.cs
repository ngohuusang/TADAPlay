using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using TadaPlay.Logger;
using TadaPlay.Connections.Interface;
using Vanara.PInvoke;
using WireGuardNT_PInvoke;
using WireGuardNT_PInvoke.WireGuard;
using static Vanara.PInvoke.IpHlpApi;
using TadaPlay.Contexts.Interfaces;


namespace TadaPlay.Connections
{
    public class WireguardVpnService : IWireGuardVpnService
    {
        private const string ADATER_NAME = "TADAVPNAdapter";
        private const string CLIENT_TUNNEL_TYPE = "client";

        private Adapter _adapter; // Instance of the WireGuard adapter
        private Guid _adapterGuid;
        private NET_LUID _adapterLuid;
        private WgConfig _wgConfig; // Parsed WireGuard configuration

        private string _currentIpAddress;
        private bool _isConnecting = false;
        private bool _isExplicitlyDisconnected = false;

        private System.Timers.Timer _reconnectTimer;
        private int _reconnectAttempt = 0;
        private readonly int[] _reconnectDelays = { 1000, 2000, 5000, 10000, 15000, 30000 };
        private const int MaxReconnectAttempts = 5;

        // Injected IAppContext to retrieve config content for reconnects
        private readonly IAppContext _appContext;

        // Serializes every operation that reads or mutates _adapter/_wgConfig/_adapterGuid/_adapterLuid.
        // Login fires two independent triggers in quick succession - AppContext.OnVpnProfileUpdated
        // (-> InitAdapter) and Home_Load (-> ConnectAsync, which also calls InitAdapter if needed) -
        // both against this same singleton. Without this lock the second one can call SetStateUp()
        // (or re-run InitAdapter and swap out _adapter) while the first's InitAdapter is still
        // mid-flight on its background Task.Run, bringing up a half-configured adapter or racing the
        // adapter reference itself. This was the root cause of "first login doesn't connect."
        private readonly SemaphoreSlim _adapterLock = new SemaphoreSlim(1, 1);


        public event EventHandler<string> OnStatusChanged;
        public event EventHandler<string> OnErrorOccurred;
        public event EventHandler OnConnected;
        public event EventHandler OnDisconnected;
        public event EventHandler<string> OnIpAddressChanged;

        public bool IsConnected
        {
            get { return _adapter != null && _adapter.GetAdapterState() == WireGuardAdapterState.WIREGUARD_ADAPTER_STATE_UP; }
        }

        public string CurrentIpAddress => _currentIpAddress; // Gets the IP of the *active* connection

        // Constructor with IAppContext injection (update Program.cs for this)
        public WireguardVpnService(IAppContext appContext)
        {
            _appContext = appContext; // Assign injected AppContext

            // Initialize _reconnectTimer once in the constructor
            _reconnectTimer = new System.Timers.Timer { AutoReset = false };
            _reconnectTimer.Elapsed += async (s, e) =>
            {
                // ONLY STOP the timer here. Disposal happens in the Dispose() method.
                _reconnectTimer.Stop();
                // When reconnecting, fetch the config content from AppContext
                string reconnectConfig = _appContext.GetVpnProfile()?.ConfigContent;
                await _adapterLock.WaitAsync();
                try
                {
                    await AttemptConnectInternalAsyncCore(reconnectConfig); // Pass config content for reconnect
                }
                finally
                {
                    _adapterLock.Release();
                }
            };
        }

        /// <summary>
        /// Initializes the WireGuard adapter and applies the configuration (parses config, sets routes, IP, DNS).
        /// This method does NOT bring the adapter online (SetStateUp).
        /// </summary>
        /// <param name="configContent">The WireGuard configuration content string.</param>
        /// <returns>True if adapter initialization is successful, false otherwise.</returns>
        public async Task<bool> InitAdapter(string configContent)
        {
            await _adapterLock.WaitAsync();
            try
            {
                return await InitAdapterCore(configContent);
            }
            finally
            {
                _adapterLock.Release();
            }
        }

        // Lock-free core - callers that already hold _adapterLock (e.g. AttemptConnectInternalAsyncCore)
        // must call this directly rather than the public InitAdapter, which would otherwise deadlock
        // trying to re-acquire the non-reentrant semaphore.
        private async Task<bool> InitAdapterCore(string configContent)
        {
            if (string.IsNullOrWhiteSpace(configContent))
            {
                DebugLogger.Error("WireguardVPNService: InitAdapter: Configuration content is null or empty.");
                OnErrorOccurred?.Invoke(this, "Nội dung cấu hình VPN trống. Không thể khởi tạo adapter.");
                return false;
            }

            // Ensure previous adapter is disposed before re-initializing
            if (_adapter != null)
            {
                try { _adapter.SetStateDown(); _adapter.Dispose(); }
                catch (Exception ex) { DebugLogger.Warn($"WireguardVPNService: Error disposing old adapter during InitAdapter: {ex.Message}"); }
                _adapter = null; // Clear old reference
            }

            try
            {
                var configAllLines = configContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                // --- Instantiate Adapter ---
                AddArch(); // Ensure architecture DLLs are in PATH
                _adapterGuid = Guid.NewGuid(); // New GUID for each adapter instance
                _adapter = new Adapter(ADATER_NAME, CLIENT_TUNNEL_TYPE);
                // Attach event handlers to the NEW adapter instance
                _adapter.EventInfoMessage += (sender, arg) => DebugLogger.Info($"WireguardVPNService [INFO]: {arg.Message}");
                _adapter.EventErrorMessage += (sender, arg) => DebugLogger.Error($"WireguardVPNService [ERROR]: {arg.Message} Win32Error: {arg.Win32ErrorNum}");

                // Init adapter
                _adapter.Init(ref _adapterGuid, out _adapterLuid);

                // Parse config file
                if (!_adapter.ParseConfFile(configAllLines, out _wgConfig))
                {
                    OnErrorOccurred?.Invoke(this, "Failed to parse config content.");
                    DebugLogger.Error("WireguardVPNService: Failed to parse config content.");
                    return false;
                }

                // Run network configurations on a background thread
                bool networkConfigSuccess = await Task.Run(() =>
                {
                    var lastError = GetIpForwardTable2(Ws2_32.ADDRESS_FAMILY.AF_INET, out MIB_IPFORWARD_TABLE2 table);
                    if (!lastError.Failed)
                    {
                        for (var i = 0; i < table.NumEntries; i++)
                        {
                            var row = table.Table[i];
                            if (row.InterfaceLuid.Equals(_adapterLuid)) { DeleteIpForwardEntry2(ref table.Table[i]); }
                        }
                    }

                    for (var i = 0; i < _wgConfig.LoctlWireGuardConfig.WgPeerConfigs.Length; i++)
                    {
                        var peerConfig = _wgConfig.LoctlWireGuardConfig.WgPeerConfigs[i];
                        MIB_IPFORWARD_ROW2 row; InitializeIpForwardEntry(out row);
                        row.InterfaceLuid = _adapterLuid; row.Metric = 1;
                        var maskedIp = IPNetwork2.Parse("" + peerConfig.allowdIp.V4.Addr, peerConfig.allowdIp.Cidr);
                        row.DestinationPrefix.Prefix.Ipv4.sin_addr = new Ws2_32.IN_ADDR(maskedIp.Network.GetAddressBytes());
                        row.DestinationPrefix.Prefix.si_family = Ws2_32.ADDRESS_FAMILY.AF_INET;
                        row.DestinationPrefix.PrefixLength = maskedIp.Cidr;
                        row.Protocol = MIB_IPFORWARD_PROTO.MIB_IPPROTO_LOCAL;
                        row.NextHop.Ipv4.sin_addr = Ws2_32.IN_ADDR.INADDR_ANY; row.NextHop.si_family = Ws2_32.ADDRESS_FAMILY.AF_INET;
                        lastError = CreateIpForwardEntry2(ref row);
                        if (lastError.Failed) { DebugLogger.Error($"WireguardVPNService: CreateIpForwardEntry2 [{i}] failed: {lastError}"); }
                    }

                    InitializeUnicastIpAddressEntry(out MIB_UNICASTIPADDRESS_ROW unicastIpAddressRow);
                    unicastIpAddressRow.InterfaceLuid = _adapterLuid;
                    unicastIpAddressRow.Address.Ipv4.sin_addr = new Ws2_32.IN_ADDR(_wgConfig.InterfaceAddress.GetAddressBytes());
                    unicastIpAddressRow.Address.Ipv4.sin_family = Ws2_32.ADDRESS_FAMILY.AF_INET;
                    unicastIpAddressRow.OnLinkPrefixLength = _wgConfig.InterfaceNetwork.Cidr;
                    unicastIpAddressRow.DadState = NL_DAD_STATE.IpDadStatePreferred;
                    lastError = CreateUnicastIpAddressEntry(ref unicastIpAddressRow);
                    if (lastError.Failed) { OnErrorOccurred?.Invoke(this, "Failed to set IP address: " + lastError); DebugLogger.Error("WireguardVPNService: CreateUnicastIpAddressEntry failed: " + lastError); return false; }

                    InitializeIpInterfaceEntry(out MIB_IPINTERFACE_ROW ipInterfaceRow);
                    ipInterfaceRow.InterfaceLuid = _adapterLuid; ipInterfaceRow.Family = Ws2_32.ADDRESS_FAMILY.AF_INET;
                    lastError = GetIpInterfaceEntry(ref ipInterfaceRow);
                    if (!lastError.Failed)
                    {
                        ipInterfaceRow.ForwardingEnabled = true; ipInterfaceRow.UseAutomaticMetric = false;
                        ipInterfaceRow.Metric = 0; ipInterfaceRow.NlMtu = _wgConfig.InterfaceMtu;
                        ipInterfaceRow.SitePrefixLength = 0;
                        lastError = SetIpInterfaceEntry(ipInterfaceRow);
                        if (lastError.Failed) { DebugLogger.Warn("WireguardVPNService: SetIpInterfaceEntry failed: " + lastError); }
                    }

                    foreach (var dnsAddress in _wgConfig.DnsAddresses)
                    {
                        try
                        {
                            var process = Process.Start(new ProcessStartInfo("netsh.exe", $"interface ipv4 add dnsservers name=\"{ADATER_NAME}\" address={dnsAddress} validate=no") { CreateNoWindow = true, UseShellExecute = false });
                            process?.WaitForExit(5000);
                            if (process?.ExitCode != 0) { DebugLogger.Warn($"WireguardVPNService: netsh for DNS failed with exit code {process?.ExitCode} for {dnsAddress}"); }
                        }
                        catch (Exception ex) { DebugLogger.Error("WireguardVPNService: Failed to set DNS via netsh: " + ex.Message); }
                    }

                    _adapter.SetConfiguration(_wgConfig); // Apply WireGuard specific configuration
                    return true; // Initialization successful
                });

                if (networkConfigSuccess)
                {
                    DebugLogger.Info("WireguardVPNService: Adapter initialized and network configured.");
                    return true;
                }
                else
                {
                    // Network config failed, dispose adapter as it's in a bad state
                    try { _adapter.Dispose(); } catch (Exception ex) { DebugLogger.Error($"WireguardVPNService: Error disposing adapter after failed config: {ex.Message}"); }
                    _adapter = null;
                    return false;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"WireguardVPNService: Error during InitAdapter: {ex.Message}");
                OnErrorOccurred?.Invoke(this, "Lỗi khởi tạo adapter VPN: " + ex.Message);
                // Attempt to dispose adapter even on exception
                try { _adapter?.Dispose(); } catch (Exception ex2) { DebugLogger.Error($"WireguardVPNService: Error disposing adapter after InitAdapter exception: {ex2.Message}"); }
                _adapter = null;
                return false;
            }
        }

        // --- Public Connect/Disconnect Methods (from IVpnService) ---
        public async Task<bool> ConnectAsync()
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
                _currentIpAddress = GetAdapterIpAddress(); // Get IP from _wgConfig if already connected
                OnIpAddressChanged?.Invoke(this, _currentIpAddress);
                return true;
            }

            _isExplicitlyDisconnected = false; // Reset flag for new connect
            _reconnectAttempt = 0; // Reset retry counter

            string reconnectConfig = _appContext.GetVpnProfile()?.ConfigContent;

            // --- Pass configContent to InitAdapter, then connect ---
            // If InitAdapter succeeds, then proceed to bring adapter up.
            // This is the core retry loop now.
            await _adapterLock.WaitAsync();
            try
            {
                return await AttemptConnectInternalAsyncCore(reconnectConfig);
            }
            finally
            {
                _adapterLock.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            _isExplicitlyDisconnected = true;
            StopReconnectTimer(); // This stops the timer

            await _adapterLock.WaitAsync();
            try
            {
                await DisconnectAsyncCore();
            }
            finally
            {
                _adapterLock.Release();
            }
        }

        private async Task DisconnectAsyncCore()
        {
            if (_adapter != null)
            {
                try
                {
                    DebugLogger.Info("WireguardVPNService: Setting VPN state down...");
                    OnStatusChanged?.Invoke(this, "Disconnecting VPN...");

                    await Task.Run(() =>
                    {
                        _adapter.SetStateDown(); // Blocking call
                    });

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
        // Now responsible for initiating connection, and retrying if SetStateUp fails.
        // Assumes InitAdapter has already succeeded.
        // Lock-free core - every caller (ConnectAsync, the reconnect timer) must hold _adapterLock
        // before calling this, so the null-check on _adapter below can't race a concurrent InitAdapter.
        private async Task<bool> AttemptConnectInternalAsyncCore(string configContent = null) // ConfigContent is only for initial InitAdapter if _adapter is null
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
                // --- Step 1: Ensure adapter is initialized and configured ---
                // This is only called here if _adapter is null (e.g., initial connect or after a full dispose)
                if (_adapter == null)
                {
                    DebugLogger.Info("WireguardVPNService: Adapter not initialized. Attempting InitAdapter.");
                    bool initSuccess = await InitAdapterCore(configContent); // Call InitAdapter here
                    if (!initSuccess)
                    {
                        // If InitAdapter fails, that's a serious issue, handle as connection loss.
                        DebugLogger.Error("WireguardVPNService: InitAdapter failed. Cannot proceed with connection.");
                        HandleConnectionLoss();
                        return false;
                    }
                }

                // If InitAdapter passed, _adapter and _wgConfig are now guaranteed to be non-null.

                // --- Step 2: Bring the adapter online (SetStateUp) ---
                bool setStateUpSuccess = await Task.Run(() =>
                {
                    _adapter.SetStateUp(); // Blocking call to bring adapter online
                    return true;
                });

                if (setStateUpSuccess)
                {
                    _isConnecting = false;
                    _reconnectAttempt = 0;
                    StopReconnectTimer(); // Stop the timer on success

                    _currentIpAddress = GetAdapterIpAddress(); // Get IP from _wgConfig if now connected
                    DebugLogger.Info("WireguardVPNService: VPN Connected.");
                    OnStatusChanged?.Invoke(this, "VPN Connected.");
                    OnConnected?.Invoke(this, EventArgs.Empty);
                    OnIpAddressChanged?.Invoke(this, _currentIpAddress);
                    return true;
                }
                else
                {
                    DebugLogger.Error("WireguardVPNService: SetStateUp failed. VPN connection internally failed.");
                    OnStatusChanged?.Invoke(this, "VPN connection failed.");
                    HandleConnectionLoss(); // Attempt reconnect on internal failure
                    return false;
                }
            }
            catch (Exception ex) // Catch any exceptions during Task.Run or direct calls
            {
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

            _reconnectTimer.Stop(); // Ensure it's stopped before changing Interval and starting
            _reconnectTimer.Interval = delay;
            _reconnectTimer.Start();
        }

        private void StopReconnectTimer()
        {
            _reconnectTimer?.Stop();
        }

        private string GetAdapterIpAddress()
        {
            try
            {
                // This gets the IP from the parsed _wgConfig, which should be reliable.
                return _wgConfig.InterfaceAddress.ToString();
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"WireguardVPNService: Error getting adapter IP from _wgConfig: {ex.Message}");
                return null;
            }
        }

        private void AddArch()
        {
            // Only expose the folder matching this process's own bitness. Adding both would let
            // the wrong-architecture wireguard.dll shadow the right one in the PATH search order,
            // failing to load with ERROR_BAD_FORMAT (0x8007000B) even in a correctly-built process.
            string archDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Environment.Is64BitProcess ? "x64" : "x86");
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Environment.SetEnvironmentVariable("PATH", currentPath + Path.PathSeparator + archDir);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopReconnectTimer(); // Calls _reconnectTimer.Stop()
                _reconnectTimer?.Dispose(); // Explicitly dispose _reconnectTimer here

                // Disconnect and dispose the adapter
                try
                {
                    if (_adapter != null)
                    {
                        DebugLogger.Info("WireguardVPNService: Disposing, attempting adapter state down.");
                        _adapter.SetStateDown(); // Try to set state down gracefully
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Error($"WireguardVPNService: Error during adapter disposal: {ex.Message}");
                }

                _isConnecting = false;
                _isExplicitlyDisconnected = true;
            }
        }

        ~WireguardVpnService()
        {
            Dispose(false);
        }
    }
}