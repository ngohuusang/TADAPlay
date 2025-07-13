using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TadaPlay.Exceptions
{
    internal class LoginException: Exception
    {
        public LoginException()
        : this(null, null)
        {
        }

        public LoginException(string message)
            : this(message, null)
        {
        }

        public LoginException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
