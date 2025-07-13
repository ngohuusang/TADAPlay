using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    public class ClientRoom
    {
        public string Id { get; set; }
        public string Name { get; set; }

        [JsonProperty("host_user_id")]
        public string HostUserId { get; set; }

        [JsonProperty("host_username")]
        public string HostUsername { get; set; }
        public string Status { get; set; } // e.g., "open", "playing", "full", "closed"
        public User[] Users { get; set; }
        public int UserCount { get; set; }
    }
}
