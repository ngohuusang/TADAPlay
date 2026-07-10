using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    /// <summary>Result of syncing the client's current IP with the account's profile IP
    /// (api.php?action=update-ip). See that endpoint for the matched/updated semantics.</summary>
    public class UpdateIpResponse : ApiResponse
    {
        [JsonProperty("ip")]
        public string Ip { get; set; }

        [JsonProperty("profile_ip")]
        public string ProfileIp { get; set; }

        [JsonProperty("matched")]
        public bool Matched { get; set; }

        [JsonProperty("updated")]
        public bool Updated { get; set; }
    }
}
