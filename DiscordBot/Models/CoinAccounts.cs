using Discord.WebSocket;
using DiscordBot.Games.Models.BattleArena;
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
        public string LastNetWorthSampleHour { get; set; } //yyyyMMddHH (UTC) of the most recent net worth history sample
        /// <summary>Every tax collected (sell tax, donation tax). An all-in bet has a small chance to win the whole pool.</summary>
        public double JackpotPool { get; set; }
        /// <summary>The 5 hour raffle slot that was last drawn, so each slot is drawn once.</summary>
        public long LastRaffleSlot { get; set; }
        /// <summary>The channel of the last money command, where raffle winners are announced.</summary>
        public ulong LastActiveChannelId { get; set; }
        /// <summary>The shared property market index. 1.0 is the base national median; see <see cref="PropertyCatalog"/>.</summary>
        public double PropertyIndex { get; set; } = 1.0;
        /// <summary>+1 for a boom, -1 for a bust.</summary>
        public int PropertyTrendDirection { get; set; } = 1;
        public int PropertyTrendDaysLeft { get; set; }
        /// <summary>The UTC day (days since 1970) the market was last stepped. Stepped once a day.</summary>
        public long LastPropertyDay { get; set; }
        /// <summary>The UTC day (days since 1970) rent was last paid up to.</summary>
        public long LastRentDay { get; set; }

        public class CoinAccount
        {
            public string Name { get; set; }
            public ulong UserId { get; set; }
            public double NetWorth { get; set; }
            public double NetWinningsToday { get; set; } = 0;
            public string MostRecentDateBonusMet { get; set; }
            public int PrestigeLevel { get; set; } = 0;
            public CoinAccountStats Stats { get; set; } = new CoinAccountStats();
            public BattlePerson BattlePerson { get; set; }
            public string LastGambleDate { get; set; } //yyyyMMdd (UTC) of the most recent resolved gamble
            public string LastFreePullDate { get; set; } //yyyyMMdd (UTC) of the day the free daily card pull was used

            /// <summary>When (UTC ticks) the player last did something with money. Players active in the last 24h enter the raffle.</summary>
            public long LastActiveUtcTicks { get; set; }
            /// <summary>Power-ups won in the raffle and not used yet.</summary>
            public List<HeldPowerUp> HeldPowerUps { get; set; } = new List<HeldPowerUp>();
            /// <summary>Power-ups that have been used and are running.</summary>
            public List<ActivePowerUp> ActivePowerUps { get; set; } = new List<ActivePowerUp>();
            /// <summary>Houses and apartments the player owns. Bought with cash only.</summary>
            public List<PropertyHolding> Properties { get; set; } = new List<PropertyHolding>();

            public const int MaxNetWorthHistoryPoints = 30 * 24; //30 days of hourly samples
            /// <summary>Total worth (cash + stocks) sampled once an hour, oldest first. Used for the .networth graph.</summary>
            public List<double> NetWorthHistory { get; set; } = new List<double>();

            /// <summary>
            /// The net worth used as the "ceiling" for bonuses and interest scaling.
            /// With prestige disabled this is always the level 0 amount.
            /// </summary>
            public double GetPrestigeThreshold(bool prestigeDisabled)
            {
                if (!prestigeDisabled) return GetAmountRequiredForNextLevel();
                return new CoinAccount().GetAmountRequiredForNextLevel();
            }

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
