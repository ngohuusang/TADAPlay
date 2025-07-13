using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TadaPlay.Common.Models
{
    public class LoginResponse : ApiResponse
    {
        [JsonProperty("token")]
        public string Token { get; set; }

        [JsonProperty("user")]
        public User User { get; set; }
    }
}
