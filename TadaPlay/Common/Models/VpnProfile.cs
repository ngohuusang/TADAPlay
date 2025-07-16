using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    public class VpnProfile
    {
        public int id { get; set; }

        [JsonProperty("config_content")]
        public string ConfigContent { get; set; }

        [JsonProperty("ip_address")]
        public string IpAddress { get; set; }
    }
}
