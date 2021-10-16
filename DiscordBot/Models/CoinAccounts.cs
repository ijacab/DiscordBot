using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot.Models
{
    public class CoinAccounts
    {
        public List<CoinAccount> Accounts { get; set; } = new List<CoinAccount>();
        public string DateDailyIncrementPaidFor { get; set; }
        public List<string> TimesInterestPaidForList { get; set; } = new List<string>();//looks like e.g. "20210202", "20210203"

        public class CoinAccount
        {
            public string Name { get; set; }
            public ulong UserId { get; set; }
            public double NetWorth { get; set; }
            public double NetWinningsToday { get; set; } = 0;
            public string MostRecentDatePlayed { get; set; }
            public int PrestigeLevel { get; set; } = 0;
            public CoinAccountStats Stats { get; set; } = new CoinAccountStats();
            public double GetAmountRequiredForNextLevel()
            {
                const int baseAmount = 100000;
                double amount;
                if (PrestigeLevel == 0)
                {
                    amount = baseAmount;
                }
                else
                {
                    amount = baseAmount * Math.Pow(10, PrestigeLevel);
                }
                return amount;
            }
        }
    }
}
