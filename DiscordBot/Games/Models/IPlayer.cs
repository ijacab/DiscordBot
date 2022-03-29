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
        public double BaseWinnings { get; set; }
        public double BonusWinnings { get; set; }
        public double TotalWinnings { get { return BaseWinnings + BonusWinnings; } }
        public bool IsDealer { get; set; }
        public bool IsFinishedPlaying { get; set; }
    }
}
