using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Models
{
    public class Reminder
    {
        public Guid Id { get; set; }
        public ulong ChannelId { get; set; }
        public string AuthorMention { get; set; }
        public string Message { get; set; }
        public DateTimeOffset TimeToRemind { get; set; }
    }
}
