using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    /// <summary>Result of reporting the player's in-game profile name to the server
    /// (api.php?action=set_in_game_name). <see cref="Conflict"/> is true when another account
    /// already owns that name and the user must rename their in-game profile.</summary>
    public class SetInGameNameResponse : ApiResponse
    {
        [JsonProperty("in_game_name")]
        public string InGameName { get; set; }

        [JsonProperty("conflict")]
        public bool Conflict { get; set; }

        [JsonProperty("updated")]
        public bool Updated { get; set; }
    }
}
