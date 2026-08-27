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

        /// <summary>
        /// The name this player uses inside the game - what everyone actually sees in the AoE2
        /// lobby, which is often nothing like their TadaPlay username. Broadcast with the user
        /// list; null for a player whose client has never reported one.
        /// </summary>
        [JsonProperty("in_game_name")]
        public string InGameName { get; set; }

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

        // --- Live match sharing, pushed by that player's own client (see the match_status
        // command in GameLobbyServer) and broadcast with the user list.
        //
        // This arrives over the lobby socket rather than by asking each player directly over
        // the VPN, which matters twice: it is one connection instead of one request per player
        // every few seconds, and it keeps working through the VPN dropouts that otherwise make
        // players who are perfectly fine look unreachable.
        //
        // A client older than this simply omits the fields, which deserialise to false/0 -
        // indistinguishable from "not in a match", which is the right answer for a peer whose
        // build cannot tell us.

        [JsonProperty("in_game")]
        public bool InGame { get; set; }

        [JsonProperty("has_match")]
        public bool HasMatch { get; set; }

        /// <summary>Match clock in milliseconds of game time; 0 when nothing is shareable.</summary>
        [JsonProperty("game_ms")]
        public long GameMs { get; set; }

        /// <summary>That player's game is paused - their match clock has stopped advancing.</summary>
        [JsonProperty("paused")]
        public bool Paused { get; set; }

        /// <summary>Seconds until the host's next capture makes more of the match available.</summary>
        [JsonProperty("wait_seconds")]
        public int WaitSeconds { get; set; }

        /// <summary>How far into their match this player is.</summary>
        public TimeSpan GameTime => TimeSpan.FromMilliseconds(GameMs);

        /// <summary>Whether this player's match can be watched right now.</summary>
        public bool IsWatchable => InGame && HasMatch;

        public bool IsHost => Status == "host";
        public bool IsInRoom => !string.IsNullOrEmpty(CurrentRoomId);
    }
}
