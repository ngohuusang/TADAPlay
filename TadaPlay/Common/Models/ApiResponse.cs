using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    public class ApiResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        // Set by the server's client-version gate (tadaserver/includes/version.php) when this
        // build is too old to be allowed in. Absent on every other response, so it defaults to
        // false and costs nothing to carry here.
        [JsonProperty("update_required")]
        public bool UpdateRequired { get; set; }

        [JsonProperty("min_version")]
        public string MinVersion { get; set; }

        [JsonProperty("download_url")]
        public string DownloadUrl { get; set; }
    }
}
