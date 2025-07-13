using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    public class IpAddressResponse : ApiResponse
    {
        [JsonProperty("ip")]
        public string Ip { get; set; }
    }
}
