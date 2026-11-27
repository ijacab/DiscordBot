using Discord.WebSocket;
using DiscordBot.Games.Models.Cards;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot.Models
{
    public class WorthRecord
    {
        public string Name { get; set; }
        public double Worth { get; set; }
        public long UtcTicks { get; set; }
    }

    public class CoinAccounts
    {
        public List<CoinAccount> Accounts { get; set; } = new List<CoinAccount>();
        public string DateDailyIncrementPaidFor { get; set; }
        public List<string> TimesInterestPaidForList { get; set; } = new List<string>();//looks like e.g. "20210202", "20210203"
        public string LastNetWorthSampleHour { get; set; } //yyyyMMddHH (UTC) of the most recent net worth history sample
        /// <summary>Every tax collected (sell tax, donation tax) and every debt repayment. An all-in bet has a small chance to win the whole pool.</summary>
        public double JackpotPool { get; set; }

        /// <summary>Sends incoming money to the account's debt first; what pays the debt goes into the jackpot. Returns what's left for cash.</summary>
        public double PayDebtFrom(CoinAccount account, double amount)
        {
            double before = account.Debt;
            double left = account.PayDebtFrom(amount);
            account.Stats.DebtRepaid += before - account.Debt;
            AddDebtRepaymentToJackpot(before - account.Debt);
            return left;
        }

        /// <summary>Pays off as much of the account's debt as its spare cash covers, into the jackpot. Returns how much was paid.</summary>
        public double RepayFromCash(CoinAccount account)
        {
            double paid = account.RepayFromCash();
            account.Stats.DebtRepaid += paid;
            AddDebtRepaymentToJackpot(paid);
            return paid;
        }

        /// <summary>Debt paid off goes into the jackpot.</summary>
        public void AddDebtRepaymentToJackpot(double paid)
        {
            if (paid > 0) JackpotPool = Math.Round(JackpotPool + paid, 2);
        }

        /// <summary>The hourly raffle slot that was last drawn, so each slot is drawn once.</summary>
        public long LastRaffleSlot { get; set; }
        /// <summary>The last power-up anyone used, and who used it, for Copycat.</summary>
        public string LastUsedPowerUpId { get; set; }
        public ulong LastUsedPowerUpUserId { get; set; }
        /// <summary>The channel of the last money command, where raffle winners are announced.</summary>
        public ulong LastActiveChannelId { get; set; }
        /// <summary>The last few channels money was used in, newest first. Market crash news and patch notes go to all of them.</summary>
        public List<ulong> RecentActiveChannelIds { get; set; } = new List<ulong>();
        public const int RecentChannelCount = 3;
        /// <summary>
        /// The highest total worth each player has ever reached, kept across seasons (`.clear coin` keeps it). Checked every hour and
        /// whenever the all-time leaderboard is shown.
        /// </summary>
        public Dictionary<ulong, WorthRecord> AllTimeBest { get; set; } = new Dictionary<ulong, WorthRecord>();

        /// <summary>Raises the player's record if <paramref name="worth"/> beats it. Returns true when it did.</summary>
        public bool NoteWorth(ulong userId, string name, double worth, DateTime nowUtc)
        {
            if (!double.IsFinite(worth) || worth <= 0) return false;
            lock (AllTimeBest)
            {
                if (AllTimeBest.TryGetValue(userId, out var record))
                {
                    record.Name = name;
                    if (worth <= record.Worth) return false;
                    record.Worth = worth;
                    record.UtcTicks = nowUtc.Ticks;
                    return true;
                }
                AllTimeBest[userId] = new WorthRecord { Name = name, Worth = worth, UtcTicks = nowUtc.Ticks };
                return true;
            }
        }

        /// <summary>When money was last used in each channel (UTC ticks), kept for <see cref="ChannelMemory"/>. Raffle results go to every channel used in the last week.</summary>
        public Dictionary<ulong, long> ChannelLastActiveUtcTicks { get; set; } = new Dictionary<ulong, long>();
        public static readonly TimeSpan ChannelMemory = TimeSpan.FromDays(30);
        /// <summary>The most channels one raffle message goes to.</summary>
        public const int MaxAnnounceChannels = 20;
        /// <summary>The newest patch note (<see cref="PatchNotes.KeyOf"/>) already announced after a restart.</summary>
        public string LastAnnouncedPatch { get; set; }
        /// <summary>Set once the old patch note posts in threads have been deleted (<c>CommandManager.CleanPatchNotesFromThreadsOnce</c>), so it only scans once.</summary>
        public bool PatchNotesThreadCleanupDone { get; set; }
        /// <summary>How bad the running market crash is, 0 (mild) to 1 (brutal). Rolled when it starts.</summary>
        public double CrisisSeverity { get; set; }
        /// <summary>The shared property market index. 1.0 is the base national median; see <see cref="PropertyCatalog"/>.</summary>
        public double PropertyIndex { get; set; } = 1.0;
        /// <summary>+1 for a boom, -1 for a bust.</summary>
        public int PropertyTrendDirection { get; set; } = 1;
        public int PropertyTrendDaysLeft { get; set; }
        /// <summary>The UTC day (days since 1970) the market was last stepped. Stepped once a day.</summary>
        public long LastPropertyDay { get; set; }
        /// <summary>No longer used: holding costs are charged hourly, with the rent. Kept so old saves load.</summary>
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
        /// <summary>Every player's gang kept from last season, given back (at level 0) when their account is made again.</summary>
        public Dictionary<ulong, List<BattlePerson>> CarriedGangs { get; set; } = new Dictionary<ulong, List<BattlePerson>>();
        /// <summary>Set once every gang's cards that shared a face have been given new ones (<c>CommandManager.RepairDuplicateFacesOnce</c>).</summary>
        public bool CardFaceRepairDone { get; set; }
        /// <summary>Old saves: the single card each player kept from last season. Read and moved into <see cref="CarriedGangs"/>, never written.</summary>
        public Dictionary<ulong, BattlePerson> CarriedCards { get; set; } = new Dictionary<ulong, BattlePerson>();
        public bool ShouldSerializeCarriedCards() => CarriedCards.Count > 0;

        [System.Runtime.Serialization.OnDeserialized]
        private void MoveOldCarriedCards(System.Runtime.Serialization.StreamingContext context)
        {
            foreach (var pair in CarriedCards)
                if (pair.Value != null && !CarriedGangs.ContainsKey(pair.Key))
                    CarriedGangs[pair.Key] = new List<BattlePerson> { pair.Value };
            CarriedCards.Clear();
        }

        public class CoinAccount
        {
            public string Name { get; set; }
            public ulong UserId { get; set; }
            public double NetWorth { get; set; }
            public double NetWinningsToday { get; set; } = 0;
            public string MostRecentDateBonusMet { get; set; }
            public int PrestigeLevel { get; set; } = 0;
            public CoinAccountStats Stats { get; set; } = new CoinAccountStats();
            /// <summary>The player's gang: up to <see cref="GangRules.MaxCards"/> cards, each with its own level (<see cref="GangRules"/>).</summary>
            public List<BattlePerson> Cards { get; set; } = new List<BattlePerson>();
            /// <summary>Old saves: the one card a player had. Moved into <see cref="Cards"/> when loaded, never written.</summary>
            [Newtonsoft.Json.JsonProperty("BattlePerson")]
            public BattlePerson LegacyCard { get; set; }
            public bool ShouldSerializeLegacyCard() => false;

            [System.Runtime.Serialization.OnDeserialized]
            private void MoveOldCard(System.Runtime.Serialization.StreamingContext context)
            {
                if (LegacyCard != null && !Cards.Exists(c => c.Id == LegacyCard.Id))
                    Cards.Add(LegacyCard);
                LegacyCard = null;
            }

            /// <summary>Glitch votes cast today (<see cref="GangRules.VoteGlitch"/>), and the UTC day (yyyyMMdd) they're for.</summary>
            public string GlitchVoteDate { get; set; }
            public int GlitchVotesToday { get; set; }
            public string LastGambleDate { get; set; } //yyyyMMdd (UTC) of the most recent resolved gamble
            public string LastFreePullDate { get; set; } //yyyyMMdd (UTC) of the day the free daily street pack was opened
            /// <summary>The UTC hour (hours since 1970) of the player's last jackpot roll, and how many they've used in it. See <see cref="Managers.BetManager.TryUseJackpotRoll"/>.</summary>
            public long JackpotRollHour { get; set; }
            public int JackpotRollsThisHour { get; set; }

            /// <summary>When (UTC ticks) the player last did something with money. Within 6 hours before a draw enters the raffle (RaffleService.EntryWindow).</summary>
            public long LastActiveUtcTicks { get; set; }
            /// <summary>Power-ups won in the raffle and not used yet.</summary>
            public List<HeldPowerUp> HeldPowerUps { get; set; } = new List<HeldPowerUp>();
            /// <summary>Power-ups that have been used and are running.</summary>
            public List<ActivePowerUp> ActivePowerUps { get; set; } = new List<ActivePowerUp>();
            /// <summary>What the player lost on their last losing bet, and when (UTC ticks), for Time Machine.</summary>
            public double LastLossAmount { get; set; }
            public long LastLossUtcTicks { get; set; }
            /// <summary>Houses the player owns. Bought with cash only. Old saves may still hold apartments until they're bought back.</summary>
            public List<PropertyHolding> Properties { get; set; } = new List<PropertyHolding>();

            /// <summary>Money owed, from power-ups that take more than the player's cash. Counts against net worth and grows with interest.</summary>
            public double Debt { get; set; }
            /// <summary>When on (`.autosell on`), stocks and then property are sold every minute to pay off any debt (<see cref="Services.DebtService"/>).</summary>
            public bool AutoSellForDebt { get; set; }
            /// <summary>The latest attacks on this player, newest first (<see cref="AttackHistory"/>), for `.attacks`.</summary>
            public List<AttackRecord> RecentAttacks { get; set; } = new List<AttackRecord>();

            //raids (`.raid`, Services/RaidService.cs)
            /// <summary>Raids won in a row. A lost raid resets it.</summary>
            public int RaidStreak { get; set; }
            /// <summary>Hired guards (`.guards`) defend until then (UTC ticks).</summary>
            public long GuardsUntilUtcTicks { get; set; }
            /// <summary>When the player's own raids happened (UTC ticks), for the hourly limit.</summary>
            public List<long> RecentRaidUtcTicks { get; set; } = new List<long>();
            /// <summary>The last time this player raided each target (UTC ticks), for the once-per-6-hours rule.</summary>
            public Dictionary<ulong, long> LastRaidOnUtcTicks { get; set; } = new Dictionary<ulong, long>();
            /// <summary>Players who raided this one and can be raided back once, with revenge's bonus.</summary>
            public List<RevengeToken> RevengeTokens { get; set; } = new List<RevengeToken>();
            /// <summary>Money set aside from this player's raid loot, paid to whoever beats them next.</summary>
            public double Bounty { get; set; }

            /// <summary>Interest on debt every hour (it nearly doubles every 7 hours). High on purpose: pay it off.</summary>
            public const double HourlyDebtInterest = 0.10;

            /// <summary>Debt after one hour of interest.</summary>
            public static double GrowDebtOneHour(double debt) =>
                debt <= 0 ? 0 : Math.Round(debt * (1 + HourlyDebtInterest), 2);

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
