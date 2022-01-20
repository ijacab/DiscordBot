using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Exceptions
{
    public class NotInGameException : Exception
    {
        public NotInGameException() : base()
        {
        }

        public NotInGameException(string message) : base(message)
        {
        }
    }
}
