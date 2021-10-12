using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models
{
    public interface IPlayer
    {
        public ulong UserId { get; set; }
        public string Username { get; set; }
        public ulong ChannelId { get; set; }
        public ulong ServerId { get; set; }
        public double BetAmount { get; set; }
        public double Winnings { get; set; }
        public bool IsDealer { get; set; }
    }
}
