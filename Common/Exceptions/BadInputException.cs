using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Exceptions
{
    public class BadInputException :Exception
    {
        public BadInputException() : base("Only letters/digits/spaces are allowed")
        {
        }

        public BadInputException(string message) : base(message)
        {
        }
    }
}
