using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Models
{
    public class CoinAccountStats
    {
        //total/max money lost
        //        total/max money won
        //total/max money bet
        //games played
        //amount of bets won/lost
        //total/max amount donated
        //donation bonuses won
        //donations received
        //donation stats for each person to/from
        //winstreak stat
        public double TotalMoneyBet { get; set; }
        public double TotalMoneyWon { get; set; }
        public double TotalMoneyLost { get; set; }
        public double MaxMoneyBetAtOnce { get; set; }
        public double MaxMoneyWonAtOnce { get; set; }
        public double MaxMoneyLostAtOnce { get; set; }
        public long GamesPlayed { get; set; }
        public long BetsWon { get; set; }
        public long BetsLost { get; set; }
        public long CurrentWinStreak { get; set; }
        public long MaxWinStreak { get; set; }
        public long CurrentLossStreak { get; set; }
        public long MaxLossStreak { get; set; }
        public double TotalMoneyDonated { get; set; }
        public double TotalMoneyReceivedFromDonations { get; set; }
        public double MaxMoneyReceivedFromDonationAtOnce { get; set; }
        public double MaxMoneyDonatedAtOnce { get; set; }
        public double DonationBonusesEncountered { get; set; }
        public Dictionary<ulong, double> DonationAmountsToDict { get; set; } = new Dictionary<ulong, double>();
        public Dictionary<ulong, double> DonationAmountsFromDict { get; set; } = new Dictionary<ulong, double>();
        

    }
}
