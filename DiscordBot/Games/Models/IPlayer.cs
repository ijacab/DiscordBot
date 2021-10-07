using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models
{
    public interface IPlayer
    {
        public ulong UserId { get; set; }
        public ulong ChannelId { get; set; }
        public ulong ServerId { get; set; }
        public bool IsDealer { get; set; }
    }
}
