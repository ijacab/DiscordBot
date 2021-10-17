using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot
{
    public class AppSettings
    {
        public List<string> BlackListedWords { get; set; } = new List<string>();
        public List<ulong> BlackListedIds { get; set; } = new List<ulong>();
    }
}
