using System;
using System.Collections.Generic;
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

                    var hasDefaultRoute = false;
                    var tunnelRoutes = new List<IPNetwork2>();
                    for (var i = 0; i < _wgConfig.LoctlWireGuardConfig.WgPeerConfigs.Length; i++)
                    {
                        var peerConfig = _wgConfig.LoctlWireGuardConfig.WgPeerConfigs[i];
                        MIB_IPFORWARD_ROW2 row; InitializeIpForwardEntry(out row);
                        row.InterfaceLuid = _adapterLuid; row.Metric = 1;
                        var maskedIp = IPNetwork2.Parse("" + peerConfig.allowdIp.V4.Addr, peerConfig.allowdIp.Cidr);
                        if (maskedIp.Cidr == 0) { hasDefaultRoute = true; }
                        tunnelRoutes.Add(maskedIp);
                        WarnIfSubnetCollidesWithLan(maskedIp);
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

                    // A TADAVPNAdapter left over from a previous session (app killed, or Windows
                    // just hasn't finished reaping it yet) still holds this same tunnel IP, so the
                    // fresh adapter's CreateUnicastIpAddressEntry fails with
                    // ERROR_OBJECT_ALREADY_EXISTS. That was the real reason first-login VPN start
                    // failed for ~50s of retries until the OS freed the address on its own.
                    // Actively reclaim the stale IP and retry once instead of waiting it out.
                    if (lastError == Win32Error.ERROR_OBJECT_ALREADY_EXISTS)
                    {
                        DebugLogger.Warn("WireguardVPNService: tunnel IP already assigned (stale adapter from a previous session). Reclaiming and retrying.");
                        RemoveConflictingUnicastIp(_wgConfig.InterfaceAddress.GetAddressBytes());
                        lastError = CreateUnicastIpAddressEntry(ref unicastIpAddressRow);
                    }

                    if (lastError.Failed) { OnErrorOccurred?.Invoke(this, "Failed to set IP address: " + lastError); DebugLogger.Error("WireguardVPNService: CreateUnicastIpAddressEntry failed: " + lastError); return false; }

                    InitializeIpInterfaceEntry(out MIB_IPINTERFACE_ROW ipInterfaceRow);
                    ipInterfaceRow.InterfaceLuid = _adapterLuid; ipInterfaceRow.Family = Ws2_32.ADDRESS_FAMILY.AF_INET;
                    lastError = GetIpInterfaceEntry(ref ipInterfaceRow);
                    if (!lastError.Failed)
                    {
                        ipInterfaceRow.ForwardingEnabled = true;
                        ipInterfaceRow.NlMtu = _wgConfig.InterfaceMtu;
                        ipInterfaceRow.SitePrefixLength = 0;
                        // Pin the tunnel to metric 0 only when it actually carries a default route,
                        // which is what the official WireGuard Windows client does. Forcing it
                        // unconditionally ranked TADAVPNAdapter above the physical NIC for every
                        // interface-ordered decision Windows makes - most damagingly DNS, where the
                        // whole machine's lookups were sent to the tunnel's DNS servers even though
                        // only the game subnet is routed through it. That added latency to every
                        // name resolution while connected and made the internet feel slow.
                        if (hasDefaultRoute)
                        {
                            ipInterfaceRow.UseAutomaticMetric = false;
                            ipInterfaceRow.Metric = 0;
                        }
                        lastError = SetIpInterfaceEntry(ipInterfaceRow);
                        if (lastError.Failed) { DebugLogger.Warn("WireguardVPNService: SetIpInterfaceEntry failed: " + lastError); }
                    }

                    // Only hand Windows a DNS server the tunnel can actually carry.
                    //
                    // This is a split-tunnel game VPN: it routes only the game subnet (10.10.0.0/16) and
                    // connect by IP, so nothing inside it is ever reached by name. But the profiles
                    // shipped a public resolver (DNS = 8.8.8.8) and the adapter was pinned to
                    // metric 0, so Windows ranked it above the physical NIC and sent the whole
                    // machine's lookups here - sourced from a tunnel address that cannot
                    // reach 8.8.8.8 because only the game subnet is routed through the tunnel.
                    // Every query black-holed and burned the resolver's full retry backoff before
                    // falling back to the real NIC, so any page with many hostnames (YouTube,
                    // Facebook) crawled the moment the VPN came up while the game itself was fine.
                    // Measured on two machines: ~7s and ~12s per lookup, down to single-digit ms
                    // once the tunnel DNS is gone.
                    //
                    // Filtering on routability rather than refusing DNS outright: for today's
                    // split tunnel the two are identical, but this still does the right thing if a
                    // resolver ever lives inside the VPN subnet, or if a full tunnel
                    // (AllowedIPs = 0.0.0.0/0) is ever added - where the tunnel's DNS is both
                    // reachable and the correct one to use.
                    foreach (var dnsAddress in _wgConfig.DnsAddresses ?? new IPAddress[0])
                    {
                        if (!IsRoutedThroughTunnel(dnsAddress, tunnelRoutes))
                        {
                            DebugLogger.Info($"WireguardVPNService: ignoring DNS server {dnsAddress} - not routed through the tunnel, leaving system DNS alone.");
                            continue;
                        }

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

        // A player whose home LAN happens to sit in the same range as the tunnel (10.10.0.0/16)
        // has that whole network pulled into the VPN the moment this route is installed - router,
        // printer and NAS all stop answering, and it looks like the app broke their internet. The
        // client can't route its way out of a genuine collision, but silently breaking someone's
        // LAN is worse than telling them, so log it and surface it in the UI. Non-fatal: the tunnel
        // still comes up, since for most players the warning won't apply.
        private void WarnIfSubnetCollidesWithLan(IPNetwork2 tunnelSubnet)
        {
            try
            {
                // A full tunnel deliberately covers everything - nothing to report.
                if (tunnelSubnet.Cidr == 0 || tunnelSubnet.Cidr > 32) { return; }

                var networkBytes = tunnelSubnet.Network.GetAddressBytes();
                if (networkBytes.Length != 4) { return; }
                var network = ToUInt32(networkBytes);
                var mask = uint.MaxValue << (32 - tunnelSubnet.Cidr);

                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) { continue; }
                    if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) { continue; }
                    // Skip our own adapter, including a stale one from a previous session that may
                    // still be holding a tunnel IP - that is not a LAN collision.
                    if (nic.Name.IndexOf(ADATER_NAME, StringComparison.OrdinalIgnoreCase) >= 0
                        || nic.Description.IndexOf("WireGuard", StringComparison.OrdinalIgnoreCase) >= 0) { continue; }

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) { continue; }
                        if ((ToUInt32(unicast.Address.GetAddressBytes()) & mask) != (network & mask)) { continue; }

                        var message = $"Mạng LAN của bạn ({unicast.Address} trên \"{nic.Name}\") trùng dải với VPN ({tunnelSubnet}). "
                                    + "Khi bật VPN, các thiết bị trong mạng nội bộ (router, máy in, NAS) có thể không truy cập được.";
                        DebugLogger.Warn("WireguardVPNService: LAN subnet collides with tunnel subnet - " + message);
                        OnErrorOccurred?.Invoke(this, message);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"WireguardVPNService: subnet collision check failed: {ex.Message}");
            }
        }

        // True when `address` falls inside one of the tunnel's AllowedIPs ranges, i.e. traffic to
        // it would actually be carried by the tunnel rather than the physical NIC.
        private static bool IsRoutedThroughTunnel(IPAddress address, List<IPNetwork2> tunnelRoutes)
        {
            var addressBytes = address.GetAddressBytes();
            if (addressBytes.Length != 4) { return false; }
            var value = ToUInt32(addressBytes);

            foreach (var route in tunnelRoutes)
            {
                if (route.Cidr == 0) { return true; } // a full tunnel carries everything
                if (route.Cidr > 32) { continue; }

                var networkBytes = route.Network.GetAddressBytes();
                if (networkBytes.Length != 4) { continue; }

                var mask = uint.MaxValue << (32 - route.Cidr);
                if ((value & mask) == (ToUInt32(networkBytes) & mask)) { return true; }
            }
            return false;
        }

        // Big-endian (network order) bytes -> uint, so two IPv4 addresses can be masked and compared.
        private static uint ToUInt32(byte[] addressBytes)
        {
            return ((uint)addressBytes[0] << 24) | ((uint)addressBytes[1] << 16)
                 | ((uint)addressBytes[2] << 8) | addressBytes[3];
        }

        // Deletes any existing IPv4 unicast address entry matching targetAddrBytes, whatever
        // interface currently owns it - used to reclaim our tunnel IP from a leftover adapter of
        // a prior session so a fresh CreateUnicastIpAddressEntry can succeed on the first attempt
        // instead of failing with ERROR_OBJECT_ALREADY_EXISTS until the OS eventually frees it.
        private static void RemoveConflictingUnicastIp(byte[] targetAddrBytes)
        {
            try
            {
                var target = new Ws2_32.IN_ADDR(targetAddrBytes);
                var err = GetUnicastIpAddressTable(Ws2_32.ADDRESS_FAMILY.AF_INET, out MIB_UNICASTIPADDRESS_TABLE table);
                if (err.Failed) { DebugLogger.Warn($"WireguardVPNService: GetUnicastIpAddressTable failed while reclaiming IP: {err}"); return; }

                for (var i = 0; i < table.NumEntries; i++)
                {
                    if (table.Table[i].Address.si_family == Ws2_32.ADDRESS_FAMILY.AF_INET
                        && table.Table[i].Address.Ipv4.sin_addr.S_addr == target.S_addr)
                    {
                        var delErr = DeleteUnicastIpAddressEntry(ref table.Table[i]);
                        if (delErr.Failed) DebugLogger.Warn($"WireguardVPNService: DeleteUnicastIpAddressEntry failed: {delErr}");
                        else DebugLogger.Info("WireguardVPNService: removed stale unicast IP entry left by a previous adapter.");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"WireguardVPNService: error reclaiming stale unicast IP: {ex.Message}");
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

                // Disconnect AND dispose the adapter. SetStateDown alone only brings the tunnel
                // down - it does NOT remove the adapter from the system; WireGuard NT only does
                // that when the adapter handle is closed via Adapter.Dispose (WireGuardCloseAdapter).
                // Without the Dispose, the TADAVPNAdapter (and its bound tunnel IP) survived process
                // exit, so the next launch hit ERROR_OBJECT_ALREADY_EXISTS and couldn't start the
                // VPN until Windows eventually reaped the orphan. This is the source-side fix for
                // that leak; RemoveConflictingUnicastIp covers the crash/kill case where this
                // graceful path never runs.
                try
                {
                    if (_adapter != null)
                    {
                        DebugLogger.Info("WireguardVPNService: Disposing, bringing adapter down and removing it.");
                        _adapter.SetStateDown(); // Bring the tunnel down gracefully first
                        _adapter.Dispose();      // Then close the handle so the adapter is removed
                        _adapter = null;
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