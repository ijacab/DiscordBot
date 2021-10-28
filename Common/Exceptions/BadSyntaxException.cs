using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Exceptions
{
    public class BadSyntaxException : Exception
    {
        public BadSyntaxException() : base()
        {
        }

        public BadSyntaxException(string message) : base(message)
        {
        }
    }
}
