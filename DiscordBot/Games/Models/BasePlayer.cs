using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class BasePlayer : IPlayer
    {
        public ulong UserId { get; set; }
        public string Username { get; set; }
        public ulong ChannelId { get; set; }
        public ulong ServerId { get; set; }
        public double BetAmount { get; set; }
        public double BaseWinnings { get; set; }
        public double BonusWinnings { get; set; }
        public bool IsDealer { get; set; } = false;
        public bool IsFinishedPlaying { get; set; } = false;

        public BasePlayer()
        {
        }

        public BasePlayer(ulong userId, ulong channelId, ulong serverId, double betAmount, string username)
        {
            UserId = userId;
            ChannelId = channelId;
            ServerId = serverId;
            BetAmount = betAmount;
            Username = username;
        }


    }
}
