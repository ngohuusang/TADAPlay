using System;
using System.Diagnostics;
using TadaPlay.Logger;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Lets other players on the VPN measure their latency to this machine.
    ///
    /// Windows blocks inbound ICMP echo by default, and measurement says that is not a corner
    /// case: of ten connected peers checked from the server, five answered ping and five were
    /// silent while being perfectly connected. Without a rule, a "check ping" button would
    /// report failure for about half the player base and look broken.
    ///
    /// The rule is deliberately narrow. It allows echo requests only from the VPN range, so it
    /// opens nothing to the internet - a player's public interface stays as silent as it was.
    /// </summary>
    public static class VpnFirewall
    {
        private const string PingRuleName = "TadaPlay VPN ping";

        /// <summary>
        /// The tunnel range. Scoping the rule to it is what keeps this from being a blanket
        /// "reply to any ping from anywhere" - if the subnet ever moves, this constant is the
        /// one thing that has to move with it.
        /// </summary>
        private const string VpnSubnet = "10.10.0.0/16";

        /// <summary>
        /// Adds the rule if it is missing. Failure is not fatal: the ping button simply reports
        /// no answer, which is what happened before this existed.
        /// </summary>
        public static void EnsureVpnPingAllowed()
        {
            try
            {
                if (RunNetsh($"advfirewall firewall show rule name=\"{PingRuleName}\"") == 0)
                {
                    return; // already present
                }

                // icmpv4:8,any is echo request specifically - not "all ICMP", which would also
                // accept redirects and timestamp requests this has no use for.
                int code = RunNetsh($"advfirewall firewall add rule name=\"{PingRuleName}\" " +
                                    "dir=in action=allow protocol=icmpv4:8,any " +
                                    $"remoteip={VpnSubnet} profile=any " +
                                    "description=\"Cho phep nguoi choi khac do do tre (ping) qua VPN\"");
                if (code == 0)
                {
                    DebugLogger.Info($"VpnFirewall: allowed inbound ping from {VpnSubnet}.");
                }
                else
                {
                    DebugLogger.Warn($"VpnFirewall: netsh returned {code} adding the ping rule; " +
                                     "other players will see no answer when measuring latency here.");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Warn($"VpnFirewall: cannot configure the ping rule: {ex.Message}");
            }
        }

        private static int RunNetsh(string arguments)
        {
            var startInfo = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process process = Process.Start(startInfo);
            if (process == null) return -1;
            process.WaitForExit(10000);
            return process.HasExited ? process.ExitCode : -1;
        }
    }
}
