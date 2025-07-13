using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TadaPlay.Websockets.Models
{
    public class PingUpdateEventArgs : EventArgs
    {
        public long PingMs { get; }
        public PingUpdateEventArgs(long pingMs) { PingMs = pingMs; }
    }
}
