using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>How one readiness check ended.</summary>
    public enum ReadinessState
    {
        /// <summary>Still being worked on - the dialog paints this as a muted row.</summary>
        Running,
        /// <summary>Already correct before this ran; nothing was changed.</summary>
        Pass,
        /// <summary>Was wrong, and this run fixed it.</summary>
        Repaired,
        /// <summary>Wrong, and this app cannot fix it - the player has to act.</summary>
        Warn,
        /// <summary>Wrong, and the attempt to fix it failed.</summary>
        Fail,
    }

    /// <summary>
    /// One row of the network report. Mutable on purpose: the dialog holds the same instances it
    /// was given and repaints them as the run progresses.
    /// </summary>
    public sealed class ReadinessCheck
    {
        internal ReadinessCheck(string id, string title)
        {
            Id = id;
            Title = title;
        }

        public string Id { get; }
        public string Title { get; }
        public ReadinessState State { get; internal set; } = ReadinessState.Running;
        public string Detail { get; internal set; } = "Đang kiểm tra...";

        /// <summary>What the player has to do themselves, when this app cannot do it.</summary>
        public string Advice { get; internal set; }
    }

    /// <summary>
    /// "Is this machine ready to play?" - the four things that stop a match from connecting even
    /// when the VPN itself is up, checked and (where possible) repaired in one pass.
    ///
    /// Everything here deliberately avoids parsing command output. netsh, sc and the firewall all
    /// print in the machine's display language, and much of this player base runs a Vietnamese
    /// Windows, so a check that looked for the word "RUNNING" or "ON" would report nonsense on
    /// exactly the machines it exists for. State is read from the registry and from the service
    /// control manager, which answer in numbers; the external tools are only ever used to CHANGE
    /// something, and only their exit code is read.
    /// </summary>
    public static class NetworkReadiness
    {
        public const string UpnpServicesCheck = "upnp-services";
        public const string RouterUpnpCheck = "router-upnp";
        public const string FirewallCheck = "firewall-profile";
        public const string GamePortsCheck = "game-ports";

        /// <summary>
        /// Must match WireguardVpnService.ADATER_NAME - the adapter whose network is the one the
        /// game actually plays over, and so the one the firewall check is about.
        /// </summary>
        private const string VpnAdapterName = "TADAVPNAdapter";

        /// <summary>
        /// Same tunnel range as <see cref="VpnFirewall"/>. Every rule added here is scoped to it,
        /// so opening a game port opens nothing towards the internet.
        /// </summary>
        private const string VpnSubnet = "10.10.0.0/16";

        /// <summary>
        /// Windows' own UPnP pair, in stop order: upnphost depends on SSDPSRV, so the host has to
        /// go down first or the SSDP stop is refused.
        ///
        /// Neither is used by AoE2, which finds its opponents over the VPN. SSDP answers
        /// multicast discovery on every interface it can see, including the tunnel, which is
        /// noise on a subnet where players are already listed by the server; UPnP Device Host is
        /// the half that asks the router to punch holes, which is what the next check looks for.
        /// </summary>
        private static readonly (string Service, string Label)[] UpnpServices =
        {
            ("upnphost", "UPnP Device Host"),
            ("SSDPSRV", "SSDP Discovery"),
        };

        /// <summary>
        /// The inbound rules a match needs, scoped to the tunnel.
        ///
        /// 47624 is where a DirectPlay host's lobby is found and 2300-2400 is where the match
        /// itself runs; 6073 is DirectPlay's enumeration port. 53754 is UserPatch's spectator
        /// stream (see GameSpectator) and 53755 is TadaPlay's own match sharing (see
        /// LiveShareServer), so a host who is watched or shared is covered by the same press.
        /// </summary>
        private static readonly (string Name, string Protocol, string Ports, string Description)[] GameRules =
        {
            ("TadaPlay AoE2 (UDP)", "UDP", "2300-2400,6073",
             "DirectPlay: tim tran va truyen du lieu tran dau qua VPN"),
            ("TadaPlay AoE2 (TCP)", "TCP", "2300-2400,47624,53754,53755",
             "DirectPlay, xem truc tiep va chia se tran dau qua VPN"),
        };

        /// <summary>The rows, in the order the dialog shows them.</summary>
        public static IReadOnlyList<ReadinessCheck> CreateChecks() => new[]
        {
            new ReadinessCheck(UpnpServicesCheck, "UPnP của Windows"),
            new ReadinessCheck(RouterUpnpCheck, "UPnP trên router"),
            new ReadinessCheck(FirewallCheck, "Tường lửa trên mạng đang chơi"),
            new ReadinessCheck(GamePortsCheck, "Cổng mạng cho AoE2"),
        };

        /// <summary>
        /// Runs every check in order, repairing what it can, and calls <paramref name="onUpdated"/>
        /// each time a row settles. The callback is raised on a background thread - the dialog
        /// marshals it.
        /// </summary>
        public static async Task RunAsync(IReadOnlyList<ReadinessCheck> checks,
                                          Action<ReadinessCheck> onUpdated,
                                          CancellationToken ct)
        {
            ReadinessCheck Row(string id) => checks.First(c => c.Id == id);

            ReadinessCheck services = Row(UpnpServicesCheck);
            await Task.Run(() => DisableWindowsUpnp(services), ct);
            onUpdated(services);
            ct.ThrowIfCancellationRequested();

            ReadinessCheck router = Row(RouterUpnpCheck);
            await ProbeRouterUpnpAsync(router, ct);
            onUpdated(router);
            ct.ThrowIfCancellationRequested();

            ReadinessCheck firewall = Row(FirewallCheck);
            await Task.Run(() => DisableFirewallOnPlayingNetwork(firewall), ct);
            onUpdated(firewall);
            ct.ThrowIfCancellationRequested();

            ReadinessCheck ports = Row(GamePortsCheck);
            await Task.Run(() => OpenGamePorts(ports), ct);
            onUpdated(ports);
        }

        #region UPnP - the two Windows services

        private static void DisableWindowsUpnp(ReadinessCheck check)
        {
            var repaired = new List<string>();
            var afterRestart = new List<string>();
            var failed = new List<string>();
            int absent = 0;

            foreach ((string service, string label) in UpnpServices)
            {
                try
                {
                    using var controller = new ServiceController(service);

                    ServiceControllerStatus status;
                    try
                    {
                        status = controller.Status;
                    }
                    catch (InvalidOperationException)
                    {
                        absent++;   // not installed on this edition of Windows - nothing to turn off
                        continue;
                    }

                    bool running = status != ServiceControllerStatus.Stopped &&
                                   status != ServiceControllerStatus.StopPending;
                    bool startsItself = controller.StartType != ServiceStartMode.Disabled;
                    if (!running && !startsItself) continue;

                    // Stopping a service Windows will start again at the next boot is only half
                    // the job, so the start type goes to Disabled as well.
                    if (running && controller.CanStop)
                    {
                        controller.Stop();
                        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    }
                    if (startsItself && RunTool("sc", $"config {service} start= disabled") != 0)
                    {
                        failed.Add(label);
                        continue;
                    }

                    controller.Refresh();
                    if (controller.Status == ServiceControllerStatus.Stopped)
                    {
                        repaired.Add(label);
                    }
                    else
                    {
                        // Disabled but still up: something else on the machine is holding it open,
                        // and Windows will not stop a service under a running dependent. It will
                        // not come back after a restart, which is worth saying rather than
                        // reporting this as an outright failure.
                        afterRestart.Add(label);
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Warn($"NetworkReadiness: cannot turn off {service}: {ex.Message}");
                    failed.Add(label);
                }
            }

            if (failed.Count > 0)
            {
                check.State = ReadinessState.Fail;
                check.Detail = $"Không tắt được: {string.Join(", ", failed)}.";
                check.Advice = "Hãy chạy TADA Play bằng quyền quản trị viên rồi thử lại.";
            }
            else if (afterRestart.Count > 0)
            {
                check.State = ReadinessState.Warn;
                check.Detail = $"Đã vô hiệu hoá {string.Join(", ", afterRestart)}, nhưng dịch vụ vẫn đang chạy.";
                check.Advice = "Khởi động lại máy để tắt hẳn. Vẫn chơi được bình thường trước khi khởi động lại.";
            }
            else if (repaired.Count > 0)
            {
                check.State = ReadinessState.Repaired;
                check.Detail = $"Đã tắt và vô hiệu hoá: {string.Join(", ", repaired)}.";
            }
            else
            {
                check.State = ReadinessState.Pass;
                check.Detail = absent == UpnpServices.Length
                    ? "Windows trên máy này không có dịch vụ UPnP nào."
                    : "Các dịch vụ UPnP của Windows đã tắt sẵn.";
            }

            DebugLogger.Info($"NetworkReadiness: UPnP services -> {check.State}. {check.Detail}");
        }

        #endregion

        #region UPnP - the router

        private static readonly IPAddress SsdpGroup = IPAddress.Parse("239.255.255.250");
        private const int SsdpPort = 1900;

        /// <summary>
        /// How long to listen for router answers. A router with UPnP on replies in milliseconds;
        /// this is generous for a slow one and short enough that the dialog does not feel stuck.
        /// </summary>
        private static readonly TimeSpan SsdpWindow = TimeSpan.FromSeconds(3);

        private const string MSearch =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 1\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
            "\r\n";

        /// <summary>
        /// Asks the local network whether anything is willing to forward ports on request.
        ///
        /// This is the one check with no repair: a router's UPnP setting lives in the router, and
        /// nothing this app runs on the player's PC will change it. It is still worth reporting,
        /// because a router that answers here is one that any program on any machine in the house
        /// can ask to open a port to the internet - and on a VPN game night nothing needs that.
        /// </summary>
        private static async Task ProbeRouterUpnpAsync(ReadinessCheck check, CancellationToken ct)
        {
            var responders = new HashSet<string>();

            try
            {
                // Sent from each real LAN address rather than from the default route: with the
                // tunnel up the default route is the tunnel, and the router would never hear it.
                List<IPAddress> sources = LanAddresses();
                if (sources.Count == 0)
                {
                    check.State = ReadinessState.Pass;
                    check.Detail = "Không tìm thấy mạng LAN nào để kiểm tra.";
                    return;
                }

                await Task.WhenAll(sources.Select(ip => ProbeFromAsync(ip, responders, ct)));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                DebugLogger.Warn($"NetworkReadiness: SSDP probe failed: {ex.Message}");
                check.State = ReadinessState.Warn;
                check.Detail = "Không kiểm tra được UPnP của router.";
                check.Advice = "Hãy tự kiểm tra mục UPnP trong trang quản trị router.";
                return;
            }

            if (responders.Count == 0)
            {
                check.State = ReadinessState.Pass;
                check.Detail = "Router không trả lời UPnP - đúng như mong muốn.";
            }
            else
            {
                check.State = ReadinessState.Warn;
                check.Detail = $"Router đang bật UPnP ({string.Join(", ", responders)}).";
                check.Advice = "Vào trang quản trị router và tắt UPnP. "
                             + "TADA Play không tự tắt được vì thiết lập này nằm trong router.";
            }

            DebugLogger.Info($"NetworkReadiness: router UPnP -> {check.State}. {check.Detail}");
        }

        private static async Task ProbeFromAsync(IPAddress local, HashSet<string> responders,
                                                 CancellationToken ct)
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            byte[] message = Encoding.ASCII.GetBytes(MSearch);
            await udp.SendAsync(message, message.Length, new IPEndPoint(SsdpGroup, SsdpPort));

            DateTime deadline = DateTime.UtcNow + SsdpWindow;
            while (!ct.IsCancellationRequested)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;

                Task<UdpReceiveResult> receive = udp.ReceiveAsync();
                if (await Task.WhenAny(receive, Task.Delay(left, ct)) != receive)
                {
                    // The socket is about to be disposed under the pending read, which faults it.
                    // Observed here so it does not surface later as an unobserved exception.
                    _ = receive.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                    break;
                }

                UdpReceiveResult result = await receive;
                string reply = Encoding.ASCII.GetString(result.Buffer);
                if (reply.IndexOf("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lock (responders) responders.Add(result.RemoteEndPoint.Address.ToString());
                }
            }
        }

        /// <summary>Real local IPv4 addresses: no loopback, no tunnel, no link-local.</summary>
        private static List<IPAddress> LanAddresses()
        {
            var addresses = new List<IPAddress>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (string.Equals(nic.Name, VpnAdapterName, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] octets = address.Address.GetAddressBytes();
                    if (octets[0] == 127) continue;                     // loopback
                    if (octets[0] == 169 && octets[1] == 254) continue; // APIPA: nothing is there
                    if (octets[0] == 10 && octets[1] == 10) continue;   // another tunnel adapter
                    addresses.Add(address.Address);
                }
            }
            return addresses;
        }

        #endregion

        #region Firewall on the network the game plays over

        /// <summary>
        /// Windows names its three firewall profiles differently in each place that mentions
        /// them: the network category is a number, the registry calls Private "Standard", and
        /// netsh wants a fourth spelling. This is that mapping, once.
        /// </summary>
        private static readonly (int Category, string Display, string RegistryKey, string NetshName)[] Profiles =
        {
            (0, "Công cộng", "PublicProfile", "publicprofile"),
            (1, "Riêng tư", "StandardProfile", "privateprofile"),
            (2, "Miền (domain)", "DomainProfile", "domainprofile"),
        };

        private const string FirewallPolicyKey =
            @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

        private const string FirewallGroupPolicyKey =
            @"SOFTWARE\Policies\Microsoft\WindowsFirewall";

        /// <summary>
        /// Turns the firewall off for the profile the VPN adapter's network is in - and only that
        /// one.
        ///
        /// Windows has no per-adapter firewall switch: the smallest thing that can be turned off
        /// is a profile, and every network in that category goes with it. That is why this does
        /// not simply turn all three off. It finds the category of the network the tunnel is
        /// actually on and touches that alone, so a player whose tunnel is "Công cộng" keeps the
        /// firewall on their home Wi-Fi.
        /// </summary>
        private static void DisableFirewallOnPlayingNetwork(ReadinessCheck check)
        {
            (int Category, string Display, string RegistryKey, string NetshName)? profile;
            string networkName;
            try
            {
                profile = FindPlayingNetworkProfile(out networkName);
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"NetworkReadiness: cannot read the network category: {ex.Message}");
                profile = null;
                networkName = null;
            }

            if (profile == null)
            {
                check.State = ReadinessState.Warn;
                check.Detail = "Chưa xác định được mạng VPN đang dùng, nên không đổi tường lửa.";
                check.Advice = "Hãy kết nối VPN rồi bấm kiểm tra lại.";
                DebugLogger.Info("NetworkReadiness: firewall -> no VPN network found.");
                return;
            }

            (int _, string display, string registryKey, string netshName) = profile.Value;
            string where = string.IsNullOrWhiteSpace(networkName)
                ? $"hồ sơ {display}"
                : $"hồ sơ {display} (mạng \"{networkName}\")";

            if (!IsFirewallEnabled(registryKey))
            {
                check.State = ReadinessState.Pass;
                check.Detail = $"Tường lửa đã tắt sẵn trên {where}.";
                DebugLogger.Info($"NetworkReadiness: firewall already off for {registryKey}.");
                return;
            }

            RunTool("netsh", $"advfirewall set {netshName} state off");

            if (!IsFirewallEnabled(registryKey))
            {
                check.State = ReadinessState.Repaired;
                check.Detail = $"Đã tắt tường lửa trên {where}.";
            }
            else if (IsForcedOnByPolicy(registryKey))
            {
                // netsh reports success and changes nothing when a domain policy owns the
                // setting, so this is checked rather than inferred from the exit code.
                check.State = ReadinessState.Warn;
                check.Detail = $"Tường lửa trên {where} bị khoá bởi chính sách của hệ thống.";
                check.Advice = "Máy này do quản trị mạng quản lý. Cổng game bên dưới vẫn được mở, "
                             + "và thường là đủ để chơi.";
            }
            else
            {
                check.State = ReadinessState.Fail;
                check.Detail = $"Không tắt được tường lửa trên {where}.";
                check.Advice = "Hãy chạy TADA Play bằng quyền quản trị viên rồi thử lại.";
            }

            DebugLogger.Info($"NetworkReadiness: firewall {registryKey} -> {check.State}. {check.Detail}");
        }

        /// <summary>
        /// 1 or missing means on - Windows ships the firewall enabled, so an absent value is not
        /// an absent firewall.
        /// </summary>
        private static bool IsFirewallEnabled(string profileKey)
        {
            using RegistryKey key = Registry.LocalMachine.OpenSubKey($@"{FirewallPolicyKey}\{profileKey}");
            object value = key?.GetValue("EnableFirewall");
            return value is not int enabled || enabled != 0;
        }

        private static bool IsForcedOnByPolicy(string profileKey)
        {
            using RegistryKey key = Registry.LocalMachine.OpenSubKey($@"{FirewallGroupPolicyKey}\{profileKey}");
            return key?.GetValue("EnableFirewall") is int enabled && enabled != 0;
        }

        /// <summary>
        /// Which firewall profile the tunnel's network sits in, via the Network List Manager -
        /// the same component that decides it, so this cannot disagree with Windows.
        ///
        /// Returns null when the tunnel is down, which is the ordinary case for someone pressing
        /// the button before connecting.
        ///
        /// The interfaces below are declared rather than reached through `dynamic`. Late binding
        /// reads a network's name and category perfectly well, but enumerating the connections
        /// that hang off it - the step that says WHICH adapter a network belongs to, and the only
        /// reason this code exists - fails with E_INVALIDARG on both IEnumNetworkConnections and
        /// the manager's own connection list. Measured on Windows 11: the outer network loop
        /// works, the inner one never does. Declaring the vtables makes both work.
        /// </summary>
        private static (int Category, string Display, string RegistryKey, string NetshName)?
            FindPlayingNetworkProfile(out string networkName)
        {
            networkName = null;

            NetworkInterface adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                string.Equals(n.Name, VpnAdapterName, StringComparison.OrdinalIgnoreCase) &&
                n.OperationalStatus == OperationalStatus.Up);
            if (adapter == null || !Guid.TryParse(adapter.Id, out Guid adapterId)) return null;

            Type managerType = Type.GetTypeFromCLSID(NetworkListManagerClsid);
            if (managerType == null) return null;

            object instance = Activator.CreateInstance(managerType);
            if (instance is not INetworkListManager manager) return null;

            try
            {
                IEnumNetworks networks = manager.GetNetworks(NlmEnumNetwork.Connected);
                while (true)
                {
                    networks.Next(1, out INetwork network, out uint gotNetwork);
                    if (gotNetwork == 0) break;

                    IEnumNetworkConnections connections = network.GetNetworkConnections();
                    while (true)
                    {
                        connections.Next(1, out INetworkConnection connection, out uint gotConnection);
                        if (gotConnection == 0) break;
                        if (connection.GetAdapterId() != adapterId) continue;

                        int category = (int)network.GetCategory();
                        networkName = network.GetName();
                        foreach (var profile in Profiles)
                        {
                            if (profile.Category == category) return profile;
                        }
                        return null;
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(manager);
            }

            return null;
        }

        private static readonly Guid NetworkListManagerClsid =
            new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

        private enum NlmEnumNetwork { Connected = 1, Disconnected = 2, All = 3 }

        // Only the leading methods of each interface are declared: a COM vtable is positional, so
        // everything up to the last call used has to be listed in order, and nothing after it
        // does. That is why INetwork carries members this file never calls.

        [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"),
         InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkListManager
        {
            [return: MarshalAs(UnmanagedType.Interface)] IEnumNetworks GetNetworks(NlmEnumNetwork flags);
        }

        [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"),
         InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IEnumNetworks
        {
            [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
            void Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetwork network,
                      out uint fetched);
        }

        [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"),
         InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetwork
        {
            [return: MarshalAs(UnmanagedType.BStr)] string GetName();
            void SetName([MarshalAs(UnmanagedType.BStr)] string name);
            [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
            void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
            Guid GetNetworkId();
            int GetDomainType();
            [return: MarshalAs(UnmanagedType.Interface)] IEnumNetworkConnections GetNetworkConnections();
            void GetTimeCreatedAndConnected(out uint lowCreated, out uint highCreated,
                                            out uint lowConnected, out uint highConnected);
            [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnectedToInternet();
            [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnected();
            int GetConnectivity();
            int GetCategory();
        }

        [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"),
         InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IEnumNetworkConnections
        {
            [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
            void Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection connection,
                      out uint fetched);
        }

        [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"),
         InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkConnection
        {
            [return: MarshalAs(UnmanagedType.Interface)] INetwork GetNetwork();
            [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnectedToInternet();
            [return: MarshalAs(UnmanagedType.VariantBool)] bool GetIsConnected();
            int GetConnectivity();
            Guid GetConnectionId();
            Guid GetAdapterId();
        }

        #endregion

        #region The game's own ports

        /// <summary>
        /// Puts the inbound rules the game needs on the firewall, so a match still connects if
        /// the firewall is ever switched back on - by the player, by an update, or by a policy
        /// that would not let the check above turn it off in the first place.
        /// </summary>
        private static void OpenGamePorts(ReadinessCheck check)
        {
            var added = new List<string>();
            var failed = new List<string>();

            foreach ((string name, string protocol, string ports, string description) in GameRules)
            {
                if (RuleExists(name)) continue;

                int code = RunTool("netsh",
                    $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow "
                    + $"protocol={protocol} localport={ports} remoteip={VpnSubnet} profile=any "
                    + $"description=\"{description}\"");

                if (code == 0 && RuleExists(name)) added.Add($"{protocol} {ports}");
                else failed.Add($"{protocol} {ports}");
            }

            // The ping rule is the same kind of thing and already has an owner, so it is asked
            // for here rather than duplicated.
            VpnFirewall.EnsureVpnPingAllowed();

            if (failed.Count > 0)
            {
                check.State = ReadinessState.Fail;
                check.Detail = $"Không mở được: {string.Join("; ", failed)}.";
                check.Advice = "Hãy chạy TADA Play bằng quyền quản trị viên rồi thử lại.";
            }
            else if (added.Count > 0)
            {
                check.State = ReadinessState.Repaired;
                check.Detail = $"Đã mở cổng game trong dải VPN {VpnSubnet}: {string.Join("; ", added)}.";
            }
            else
            {
                check.State = ReadinessState.Pass;
                check.Detail = $"Cổng game đã mở sẵn trong dải VPN {VpnSubnet}.";
            }

            DebugLogger.Info($"NetworkReadiness: game ports -> {check.State}. {check.Detail}");
        }

        private static bool RuleExists(string name) =>
            RunTool("netsh", $"advfirewall firewall show rule name=\"{name}\"") == 0;

        #endregion

        /// <summary>
        /// Runs a console tool with no window and returns its exit code. What it printed is
        /// discarded, never parsed: see the note on this class about display languages.
        ///
        /// The output is still read to the end, and that is not optional. Redirecting a pipe and
        /// then only waiting blocks the child as soon as it has written a pipe buffer's worth -
        /// it waits for a reader that never comes, this waits out its timeout, and the check
        /// reports a failure that never happened. It is reachable here: `netsh advfirewall
        /// firewall show rule` prints a block per matching rule.
        /// </summary>
        private static int RunTool(string fileName, string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using Process process = Process.Start(startInfo);
                if (process == null) return -1;

                // Both pipes drained at once - reading one to the end while the other fills is
                // the same deadlock in a smaller shape.
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000)) return -1;
                Task.WaitAll(new Task[] { output, error }, 5000);

                return process.ExitCode;
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"NetworkReadiness: {fileName} {arguments} failed: {ex.Message}");
                return -1;
            }
        }
    }
}
