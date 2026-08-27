using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using WireGuardNT_PInvoke.WireGuard;

namespace WireGuardNT_PInvoke.WireGuard
{
    public class WgConfig
    {
        public loctlWireGuardConfig LoctlWireGuardConfig;
        public IPAddress InterfaceAddress { get; set; }
        public IPNetwork2 InterfaceNetwork { get; set; }
        // Empty, never null: a config with no DNS key (the normal case for our split tunnel,
        // which resolves nothing by name) must leave the machine's DNS settings untouched
        // rather than NullReference its way out of adapter setup.
        public IPAddress[] DnsAddresses { get; set; } = new IPAddress[0];

        public ushort InterfaceMtu = 1420;
        public ushort InterfaceListenPort { get; set; }

        public ConfigBuffer ConfigBuffer;
        public WgConfig()
        {
            LoctlWireGuardConfig = new loctlWireGuardConfig();
        }
    }
}
