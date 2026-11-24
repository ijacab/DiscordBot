using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Models
{
    public class BasePlayer : IPlayer
    {
        public ulong UserId { get; set; }
        public string Username { get; set; }
        public ulong ChannelId { get; set; }
        public ulong ServerId { get; set; }
        public double BetAmount { get; set; }
        /// <summary>The bet as a share of the player's cash when they joined (0 to 1), which is what Play again repeats.</summary>
        public double BetShare { get; set; }
        public double BaseWinnings { get; set; }
        public double BonusWinnings { get; set; }
        public bool IsDealer { get; set; } = false;
        public bool IsFinishedPlaying { get; set; } = false;
        public CoinAccount CoinAccount { get; set; }

        public BasePlayer()
        {
        }
    }
}
