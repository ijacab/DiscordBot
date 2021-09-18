using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class BlackjackPlayer
    {
        public ulong Id { get; set; }
        public List<Card> Cards {get;set;}
        public double BetAmount { get; set; }
    }
}
