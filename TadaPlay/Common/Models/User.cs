using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    public class User
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("full_name")]
        public string FullName { get; set; }

        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("ip_address")]
        public string IpAddress { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("nick_name")]
        public string NickName { get; set; }

        [JsonProperty("is_online")]
        public bool IsOnline { get; set; }

        [JsonProperty("last_seen")]
        public string LastSeen { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("current_room_id")]
        public string CurrentRoomId { get; set; } // The room ID this user is currently in

        [JsonProperty("vpn_profile")]
        public VpnProfile VpnProfile { get; set; }

        [JsonProperty("ranking")]
        public string Ranking { get; set; }

        public bool IsHost => Status == "host";
        public bool IsInRoom => !string.IsNullOrEmpty(CurrentRoomId);
    }
}
