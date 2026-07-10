using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Detects whether a WireGuard tunnel is already up outside this app (e.g. the official
    /// WireGuard desktop client), so TadaPlay doesn't spin up its own adapter on top of it.
    /// </summary>
    public static class ExternalVpnDetector
    {
        // Must match WireguardVpnService.ADATER_NAME - that's TadaPlay's own adapter, not an
        // "external" one, so it's excluded from the search.
        private const string OwnAdapterName = "TADAVPNAdapter";

        /// <summary>
        /// Returns <paramref name="expectedIp"/> if an already-up network interface (that isn't
        /// this app's own adapter) is already carrying it, or null otherwise. <paramref name="expectedIp"/>
        /// must be the logged-in user's own pinned VPN profile IP - matching on that (rather than
        /// on any interface that merely looks like a WireGuard tunnel) makes sure this only fires
        /// for this account's own tunnel, not some unrelated WireGuard connection that happens to
        /// be up on the machine.
        /// </summary>
        public static string TryGetExternalWireGuardIp(string expectedIp)
        {
            if (string.IsNullOrWhiteSpace(expectedIp)) return null;

            string expected = expectedIp.Split('/')[0].Trim();

            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    !string.Equals(n.Name, OwnAdapterName, StringComparison.OrdinalIgnoreCase) &&
                    n.GetIPProperties().UnicastAddresses.Any(a =>
                        a.Address.AddressFamily == AddressFamily.InterNetwork &&
                        string.Equals(a.Address.ToString(), expected, StringComparison.Ordinal)));

                if (nic == null) return null;

                DebugLogger.Info($"ExternalVpnDetector: found external tunnel '{nic.Name}' ({nic.Description}) already carrying this account's pinned IP {expected}.");
                return expected;
            }
            catch (Exception ex)
            {
                DebugLogger.Error($"ExternalVpnDetector: failed to enumerate network interfaces: {ex.Message}");
                return null;
            }
        }
    }
}
