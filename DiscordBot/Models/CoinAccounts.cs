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
        /// <summary>The 30 minute raffle slot that was last drawn, so each slot is drawn once.</summary>
        public long LastRaffleSlot { get; set; }
        /// <summary>The last power-up anyone used, and who used it, for Copycat.</summary>
        public string LastUsedPowerUpId { get; set; }
        public ulong LastUsedPowerUpUserId { get; set; }
        /// <summary>The channel of the last money command, where raffle winners are announced.</summary>
        public ulong LastActiveChannelId { get; set; }
        /// <summary>The shared property market index. 1.0 is the base national median; see <see cref="PropertyCatalog"/>.</summary>
        public double PropertyIndex { get; set; } = 1.0;
        /// <summary>+1 for a boom, -1 for a bust.</summary>
        public int PropertyTrendDirection { get; set; } = 1;
        public int PropertyTrendDaysLeft { get; set; }
        /// <summary>The UTC day (days since 1970) the market was last stepped. Stepped once a day.</summary>
        public long LastPropertyDay { get; set; }
        /// <summary>The UTC day (days since 1970) holding costs were last charged up to.</summary>
        public long LastRentDay { get; set; }
        /// <summary>The UTC hour (hours since 1970) rent was last paid up to.</summary>
        public long LastRentHour { get; set; }
        /// <summary>Where the market is in a rare crash. None is normal trading. See <see cref="MarketCycle"/>.</summary>
        public CrisisPhase MarketPhase { get; set; } = CrisisPhase.None;
        public int MarketPhaseDaysLeft { get; set; }
        /// <summary>The first UTC day another crash may start (days since 1970).</summary>
        public long NextCrisisAllowedDay { get; set; }
        /// <summary>Set when a crash ends, so stocks take their new level as their base on the next tick.</summary>
        public bool CrisisResetPending { get; set; }

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

            /// <summary>When (UTC ticks) the player last did something with money. Within the hour before a draw enters the raffle.</summary>
            public long LastActiveUtcTicks { get; set; }
            /// <summary>Power-ups won in the raffle and not used yet.</summary>
            public List<HeldPowerUp> HeldPowerUps { get; set; } = new List<HeldPowerUp>();
            /// <summary>Power-ups that have been used and are running.</summary>
            public List<ActivePowerUp> ActivePowerUps { get; set; } = new List<ActivePowerUp>();
            /// <summary>What the player lost on their last losing bet, and when (UTC ticks), for Time Machine.</summary>
            public double LastLossAmount { get; set; }
            public long LastLossUtcTicks { get; set; }
            /// <summary>Houses and apartments the player owns. Bought with cash only.</summary>
            public List<PropertyHolding> Properties { get; set; } = new List<PropertyHolding>();

            /// <summary>Money owed, from power-ups that take more than the player's cash. Counts against net worth and grows with interest.</summary>
            public double Debt { get; set; }
            /// <summary>When on (`.autosell on`), stocks and then property are sold every minute to pay off any debt (<see cref="Services.DebtService"/>).</summary>
            public bool AutoSellForDebt { get; set; }
            /// <summary>Interest on debt per day, compounded hourly. High on purpose: pay it off.</summary>
            public const double DailyDebtInterest = 0.01;

            /// <summary>Debt after one hour of interest.</summary>
            public static double GrowDebtOneHour(double debt) =>
                debt <= 0 ? 0 : Math.Round(debt * Math.Pow(1 + DailyDebtInterest, 1.0 / 24), 2);

            /// <summary>Pays off as much debt as the spare cash covers. Returns how much was paid.</summary>
            public double RepayFromCash()
            {
                if (Debt <= 0 || NetWorth <= 0) return 0;
                double paid = Math.Round(Math.Min(Debt, Math.Floor(NetWorth * 100) / 100), 2);
                if (paid <= 0) return 0;
                Debt = Math.Round(Debt - paid, 2);
                NetWorth -= paid;
                return paid;
            }

            /// <summary>Sends incoming money to the debt first. Returns what is left over for cash.</summary>
            public double PayDebtFrom(double amount)
            {
                if (amount <= 0 || Debt <= 0) return Math.Max(0, amount);
                double paid = Math.Min(Debt, amount);
                Debt = Math.Round(Debt - paid, 2);
                return amount - paid;
            }

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
