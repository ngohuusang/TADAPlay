using System;
using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    /// <summary>
    /// Metadata describing a finished game, uploaded to the server alongside the
    /// recorded game (.mgz / .mgx) file.
    /// </summary>
    public class GameRecordMetadata
    {
        [JsonProperty("room_id")]
        public string RoomId { get; set; }

        [JsonProperty("room_name")]
        public string RoomName { get; set; }

        [JsonProperty("host_username")]
        public string HostUsername { get; set; }

        /// <summary>Username of the client uploading the record.</summary>
        [JsonProperty("uploaded_by")]
        public string UploadedBy { get; set; }

        /// <summary>Usernames of the players that were in the room when the game finished.</summary>
        [JsonProperty("players")]
        public string[] Players { get; set; }

        /// <summary>UTC time the game was reported as finished.</summary>
        [JsonProperty("finished_at")]
        public DateTime FinishedAt { get; set; }

        /// <summary>Original file name of the recorded game on the client.</summary>
        [JsonProperty("record_file_name")]
        public string RecordFileName { get; set; }

        [JsonProperty("client_version")]
        public string ClientVersion { get; set; }
    }
}
