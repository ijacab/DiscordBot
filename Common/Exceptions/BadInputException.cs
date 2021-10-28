using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Exceptions
{
    public class BadInputException :Exception
    {
        public BadInputException() : base("INTPU CONRTAIN FUCKY CHARACTER ---  I NOT LIKE THIS I NOT ALLOW THIS")
        {
        }

        public BadInputException(string message) : base(message)
        {
        }
    }
}
