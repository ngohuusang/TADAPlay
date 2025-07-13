using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TadaPlay.Websockets.Models
{
    public class WebSocketMessageEventArgs : EventArgs
    {
        public string Data { get; }
        public WebSocketMessageEventArgs(string data) { Data = data; }
    }
}
