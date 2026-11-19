using Common.Helpers;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Managers
{
    public class BetManager
    {
        private readonly CoinService _coinService;
        private readonly StockService _stockService;
        private readonly AppSettings _appSettings;
        /// <summary>Players with a bet open. Thread-safe: typed commands and button clicks place bets on different threads.</summary>
        public readonly SyncSet<ulong> InitiatedBetUserIds = new SyncSet<ulong>();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DailyRollResult> _pendingDailyRolls = new System.Collections.Concurrent.ConcurrentDictionary<ulong, DailyRollResult>();
        private const int StealRollNeeded = 10;
        private const double StealPercent = 0.10;
        /// <summary>Chance that a qualifying bet (see <see cref="QualifiesForJackpot"/>) wins the jackpot pool, per roll.</summary>
        public const double JackpotWinChance = 0.03;
        /// <summary>A player gets this many jackpot rolls in each UTC hour.</summary>
        public const int JackpotRollsPerHour = 3;
        /// <summary>A bet up to this size qualifies only if it is all the player's cash; a bigger one needs <see cref="JackpotBigBetShare"/> of it.</summary>
        public const double JackpotSmallBetLimit = 10_000;
        public const double JackpotBigBetShare = 0.5;

        /// <summary>
        /// Whether a bet can roll for the jackpot, from its size and the player's cash before it was placed. A bet of
        /// <see cref="JackpotSmallBetLimit"/> or less must be all their cash; a bigger bet needs half their cash or more
        /// (a dollar of slack for rounding).
        /// </summary>
        public static bool QualifiesForJackpot(double betAmount, double cashBeforeBet) =>
            betAmount > JackpotSmallBetLimit
                ? betAmount >= JackpotBigBetShare * cashBeforeBet - 1
                : betAmount >= cashBeforeBet - 1;
        /// <summary>The share of money lost on a bet that goes into the jackpot.</summary>
        public const double JackpotLossShare = 0.75;
        private static readonly Random _random = new Random();
        /// <summary>The random source for the jackpot roll (0 to 1). Settable so tests aren't random.</summary>
        public Func<double> NextJackpotRoll { get; set; } = () => _random.NextDouble();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, double> _pendingJackpotWins = new System.Collections.Concurrent.ConcurrentDictionary<ulong, double>();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> _pendingJackpotNotes = new System.Collections.Concurrent.ConcurrentDictionary<ulong, string>();

        public class DailyRollResult
        {
            public int Roll { get; set; }
            public int Needed { get; set; } = StealRollNeeded;
            public double StolenAmount { get; set; }
            public string StolenFromName { get; set; }
            public ulong StolenFromUserId { get; set; }

            public override string ToString()
            {
                if (StolenAmount > 0)
                    return $"🎲 First gamble of the day! Rolled a {Roll} and robbed {StolenFromName} of {Money.Format(StolenAmount)}. Get fucked, {StolenFromName}.";
                if (Roll >= Needed)
                    return $"🎲 First gamble of the day! Rolled a {Roll}... but there was nobody to rob (you're on top, or they've got a Shield).";
                return $"🎲 First gamble of the day! Rolled a {Roll}. Needed {Needed}+ to rob the top player of {StealPercent:P0} of their cash. Unlucky.";
            }
        }

        /// <summary>
        /// Returns (and clears) the daily d10 roll made when the user's gamble was last resolved, if any.
        /// </summary>
        public DailyRollResult TakeDailyRoll(ulong userId)
        {
            if (!_pendingDailyRolls.TryRemove(userId, out var result))
                return null;
            return result;
        }

        /// <summary>True when a jackpot roll lands. Kept separate from the random source so the odds can be tested.</summary>
        public static bool RollsJackpot(double roll, double chance = JackpotWinChance) => roll < chance;

        /// <summary>Returns (and clears) the announcement for a jackpot won when the user's bet was last resolved, if any.</summary>
        public string TakeJackpotWin(ulong userId)
        {
            if (_pendingJackpotWins.TryRemove(userId, out double won))
            {
                _pendingJackpotNotes.TryRemove(userId, out _);
                return $"💰 **JACKPOT!** You bet big and won the ${Common.Helpers.FormatHelper.GetCommaNumber(won)} jackpot!";
            }
            //a qualifying bet that couldn't roll because the hour's rolls were used up
            return _pendingJackpotNotes.TryRemove(userId, out string note) ? note : null;
        }

        /// <summary>
        /// Uses one of the player's jackpot rolls for this UTC hour. False when they have already used
        /// <see cref="JackpotRollsPerHour"/>. A new hour starts the count again.
        /// </summary>
        public static bool TryUseJackpotRoll(CoinAccount account, DateTime utcNow)
        {
            long hour = utcNow.Ticks / TimeSpan.TicksPerHour;
            if (account.JackpotRollHour != hour)
            {
                account.JackpotRollHour = hour;
                account.JackpotRollsThisHour = 0;
            }
            if (account.JackpotRollsThisHour >= JackpotRollsPerHour)
                return false;
            account.JackpotRollsThisHour++;
            return true;
        }

        /// <summary>Money lost on a bet that goes into the jackpot (whole cents).</summary>
        public static double JackpotShareOfLoss(double lost) =>
            lost > 0 ? Math.Floor(lost * JackpotLossShare * 100) / 100 : 0;

        /// <summary>
        /// A qualifying bet has a small chance to take the whole pool, up to <see cref="JackpotRollsPerHour"/> rolls an hour.
        /// The win is paid to the gambler.
        /// </summary>
        private void RollJackpot(CoinAccount gambler)
        {
            if (_coinService.JackpotPool <= 0)
                return;

            var now = DateTime.UtcNow;
            if (!TryUseJackpotRoll(gambler, now))
            {
                _pendingJackpotNotes[gambler.UserId] = $"🎰 No jackpot rolls left this hour ({JackpotRollsPerHour} an hour).";
                return;
            }

            gambler.Stats.JackpotRolls++;
            double chance = gambler.IsActive(PowerUpIds.JackpotFever, now) ? PowerUpCatalog.JackpotFeverChance : JackpotWinChance;
            if (!RollsJackpot(NextJackpotRoll(), chance))
                return;

            double won = _coinService.TakeJackpot();
            Money.Credit(_coinService.GetAll(), gambler, won);
            _pendingJackpotWins[gambler.UserId] = won;
            gambler.Stats.JackpotsWon++;
            gambler.Stats.JackpotMoneyWon += won;
        }

        public BetManager(CoinService coinService, StockService stockService, AppSettings appSettings)
        {
            _coinService = coinService;
            _stockService = stockService;
            _appSettings = appSettings;
        }

        /// <summary>
        /// Resolves the bet and updates the user's account based on the result.
        /// </summary>
        public async Task InitiateBet(ulong userId, string userName, double betAmount, int? minimumPercentBetRequired = null)
        {
            CoinAccount coinAccount = await _coinService.Get(userId, userName);
            await InitiateBet(coinAccount, betAmount, minimumPercentBetRequired);
        }

        public async Task InitiateBet(CoinAccount coinAccount, double betAmount, int? minimumPercentBetRequired = null)
        {
            if (!double.IsFinite(betAmount) || betAmount < 1)
                throw new BadInputException("You need to bet at least a dollar to play bro... what do think this is? A casino for FUCKING poor people huh? FUCK HEAD FUCK OFF");
            //claimed first, atomically, so a typed bet and a button click at the same moment can't both get through
            if (!InitiatedBetUserIds.TryAdd(coinAccount.UserId))
                throw new BadInputException("You've already got a bet going, you degenerate. Finish that one first.");
            try
            {
                EnsureGameMoneyInputIsValid(betAmount, coinAccount, minimumPercentBetRequired);
                UpdateInitiateBetStats(coinAccount, betAmount);

                //minus their input money - they will get it back when the game ends (if they don't lose)
                Money.Debit(coinAccount, betAmount);
                await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, coinAccount.Name, updateRemote: false);
            }
            catch
            {
                InitiatedBetUserIds.Remove(coinAccount.UserId);
                throw;
            }
        }

        public async Task CancelBet(ulong userId, string userName, double betAmount)
        {
            if (!InitiatedBetUserIds.Contains(userId))
                return;

            CoinAccount coinAccount = await _coinService.Get(userId, userName);
            
            //give back betted money
            coinAccount.NetWorth += betAmount;

            await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, userName);
            InitiatedBetUserIds.Remove(userId);
        }

        public async Task<(double BonusWinnings, double TotalWinnings, double NetWinnings, bool WasBonusGranted)> 
            ResolveBet(ulong userId, string userName, double betAmount, double baseWinnings, bool updateRemote = true, bool feedJackpot = true, string game = null)
        {
            if (!InitiatedBetUserIds.Contains(userId))
                throw new Exception("Something's gone wrong here. Somehow it is trying to resolve a bet which was not initiated.");

            try
            {
                return await ResolveBetCore(userId, userName, betAmount, baseWinnings, updateRemote, feedJackpot, game);
            }
            finally
            {
                InitiatedBetUserIds.Remove(userId); //even if saving fails, they're never stuck with a bet "open"
            }
        }

        private async Task<(double BonusWinnings, double TotalWinnings, double NetWinnings, bool WasBonusGranted)>
            ResolveBetCore(ulong userId, string userName, double betAmount, double baseWinnings, bool updateRemote, bool feedJackpot, string game)
        {
            CoinAccount coinAccount = await _coinService.Get(userId, userName);
            double netWorthBeforeBet = coinAccount.NetWorth + betAmount;
            var worth = new WorthService(_coinService, _stockService, _appSettings);
            //everything they own counts, plus money held in a dungeon run or a Face Off backing, so parking cash elsewhere
            //and betting a dollar doesn't count as betting big
            double everythingBeforeBet = worth.TotalWorth(coinAccount) + betAmount + Money.Held(userId);

            //the interest bonus needs a bet of half of everything they're worth
            bool overFiftyPercentBet = betAmount >= (0.5 * everythingBeforeBet) - 1;

            bool bonusGranted = false, firstGameOfTheDay = false;
            var todayString = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            if (coinAccount.MostRecentDateBonusMet != todayString)
            {
                firstGameOfTheDay = true;
                if (overFiftyPercentBet)
                {
                    coinAccount.MostRecentDateBonusMet = todayString;
                    bonusGranted = true;
                }
            }

            bool firstGambleOfTheDay = coinAccount.LastGambleDate != todayString;
            coinAccount.LastGambleDate = todayString;

            var now = DateTime.UtcNow;
            double bonusWinnings = GetBonus(coinAccount, baseWinnings, betAmount);
            bonusWinnings += coinAccount.BetBonus(baseWinnings, betAmount, now);
            //an Audit, a Leech or someone's Toll Booth takes a cut of a win; it comes off the bonus line
            bonusWinnings -= PowerUps.SettleWin(_coinService.GetAll(), coinAccount, baseWinnings + bonusWinnings - betAmount, now);
            double totalWinnings = baseWinnings + bonusWinnings;
            if (totalWinnings < betAmount)
            {
                //remembered for a Time Machine
                coinAccount.LastLossAmount = betAmount - totalWinnings;
                coinAccount.LastLossUtcTicks = now.Ticks;
            }

            double netWinnings = Math.Floor(baseWinnings - betAmount);
            if (firstGameOfTheDay)
                coinAccount.NetWinningsToday = netWinnings;
            else
                coinAccount.NetWinningsToday += netWinnings;

            Money.Credit(_coinService.GetAll(), coinAccount, totalWinnings);

            if(coinAccount.NetWinningsToday < 0 
                || Math.Floor(coinAccount.NetWorth) == 0)
                coinAccount.NetWinningsToday = 0;

            UpdateResolveBetStats(coinAccount, betAmount, totalWinnings);
            RecordGame(coinAccount, game, betAmount, totalWinnings, bonusWinnings);

            if (firstGambleOfTheDay)
            {
                int rollNeeded = coinAccount.TryConsumeCharge(PowerUpIds.LoadedDice, now) ? PowerUpCatalog.LoadedDiceRollNeeded : StealRollNeeded;
                _pendingDailyRolls[userId] = RollDailySteal(coinAccount, rollNeeded, now);
                coinAccount.Stats.DailyRolls++;
            }

            //a qualifying bet is all their cash up to $10,000, or half their cash or more above that, whether it wins or loses.
            //trading is blocked while a bet is open, so their holdings are the same as when the bet was placed
            //property counts the same as stocks: owning any blocks the jackpot
            bool allInWithNothingElseInvested = QualifiesForJackpot(betAmount, netWorthBeforeBet)
                && Money.Held(userId) <= 0
                && !_stockService.HasPositions(userId)
                && coinAccount.Properties.Count == 0;
            if (allInWithNothingElseInvested)
                RollJackpot(coinAccount);

            //most of what is lost goes into the jackpot (after the roll, so you can't win your own loss straight back).
            //Face Off passes false: its losers' money is paid to the winner already
            if (feedJackpot)
            {
                double fed = JackpotShareOfLoss(betAmount - totalWinnings);
                _coinService.AddToJackpot(fed);
                coinAccount.Stats.MoneyFedToJackpot += fed;
            }

            await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, userName, updateRemote);
            return (bonusWinnings, totalWinnings, netWinnings, bonusGranted);
        }

        /// <summary>
        /// Accounts in leaderboard order (1st first). Matches the ordering in the Leaderboard command: cash + stocks,
        /// with prestige taking priority when prestige is enabled.
        /// </summary>
        private IEnumerable<CoinAccount> RankedAccounts()
        {
            return new WorthService(_coinService, _stockService, _appSettings).Ranked();
        }

        /// <summary>
        /// Rolls a d10; on a 10 and if the roller isn't 1st on the leaderboard, moves 10% of 1st place's cash to the roller.
        /// Mutates both accounts in memory (the caller persists via CoinService.Update, which serializes every account).
        /// </summary>
        private DailyRollResult RollDailySteal(CoinAccount roller, int rollNeeded, DateTime now)
        {
            var result = new DailyRollResult { Roll = DiceRoller.RollDice(1, 10)[0], Needed = rollNeeded };
            if (result.Roll < rollNeeded)
                return result;

            var leader = RankedAccounts().FirstOrDefault();
            if (leader == null || leader.UserId == roller.UserId)
                return result; //roller is already 1st on the leaderboard
            if (leader.IsShielded(now))
                return result; //a Shield power-up protects them

            double amount = Math.Floor(leader.NetWorth * StealPercent);
            if (amount <= 0)
                return result;

            Money.Seize(leader, roller, amount, debtRoom: 0); //cash only
            roller.Stats.DailyStealsWon++;
            roller.Stats.MoneyStolenByDailyRoll += amount;
            leader.Stats.TimesRobbedByDailyRoll++;
            leader.Stats.MoneyLostToDailyRoll += amount;
            result.StolenAmount = amount;
            result.StolenFromName = leader.Name;
            result.StolenFromUserId = leader.UserId;
            return result;
        }

        public async Task ResolveBet(IEnumerable<IPlayer> players, bool feedJackpot = true, string game = null)
        {
            //every player is settled even if one of them fails, so nobody's stake is left hanging
            var list = players.ToList();
            Exception failure = null;
            for (int i = 0; i < list.Count; i++)
            {
                var player = list[i];
                try
                {
                    var (BonusWinnings, _, _, _) = await ResolveBet(player.UserId, player.Username, player.BetAmount, player.BaseWinnings, i == list.Count - 1, feedJackpot, game);
                    player.BonusWinnings = BonusWinnings; //this gets used outside of the method
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }
            }
            if (failure != null) throw failure;
        }

        private void EnsureGameMoneyInputIsValid(double inputMoney, CoinAccount account, int? minimumPercentBetRequired)
        {
            if (!double.IsFinite(inputMoney) || inputMoney <= 0)
                throw new BadInputException($"You didn't place any valid bets FUCK HEAD");

            if (inputMoney > account.NetWorth)
                throw new BadInputException($"CAN'T BET WITH MORE MONEY THAN YOU HAVE DUMBASS. YOU HAVE ${FormatHelper.GetCommaNumber(account.NetWorth)}");

            if (minimumPercentBetRequired != null)
            {
                double betRequired = account.NetWorth * ((double)minimumPercentBetRequired / 100);
                if (inputMoney < betRequired - 1)
                    throw new BadInputException($"Bet at least {minimumPercentBetRequired}% of your cash, you tight-arse. That's {Money.Format(Math.Ceiling(betRequired))} or more.");
            }
        }

        /// <summary>Adds the bet to the player's record for its game ("Other" when the caller didn't say which).</summary>
        private static void RecordGame(CoinAccount account, string game, double betAmount, double totalWinnings, double bonusWinnings)
        {
            game = string.IsNullOrWhiteSpace(game) ? "Other" : game;
            if (!account.Stats.Games.TryGetValue(game, out var record))
                account.Stats.Games[game] = record = new GameRecord();
            record.Add(betAmount, totalWinnings);
            if (bonusWinnings > 0)
                account.Stats.TotalBonusWinnings += bonusWinnings;
        }

        public void UpdateInitiateBetStats(CoinAccount coinAccount, double betAmount)
        {
            if (betAmount > coinAccount.Stats.MaxMoneyBetAtOnce)
                coinAccount.Stats.MaxMoneyBetAtOnce = betAmount;

            coinAccount.Stats.TotalMoneyBet += betAmount;
            coinAccount.Stats.GamesPlayed += 1;
        }

        public void UpdateResolveBetStats(CoinAccount coinAccount, double betAmount, double winnings)
        {
            double netWinnings = Math.Floor(winnings - betAmount);
            //won
            if (netWinnings > 0)
            {
                coinAccount.Stats.TotalMoneyWon += netWinnings;
                coinAccount.Stats.BetsWon += 1;
                coinAccount.Stats.CurrentWinStreak += 1;
                coinAccount.Stats.CurrentLossStreak = 0;
                if (coinAccount.Stats.CurrentWinStreak > coinAccount.Stats.MaxWinStreak)
                    coinAccount.Stats.MaxWinStreak = coinAccount.Stats.CurrentWinStreak;

                if (netWinnings > coinAccount.Stats.MaxMoneyWonAtOnce)
                    coinAccount.Stats.MaxMoneyWonAtOnce = netWinnings;
            }

            //lost
            if (netWinnings < 0)
            {
                double losings = netWinnings * -1;
                coinAccount.Stats.TotalMoneyLost += losings;
                coinAccount.Stats.BetsLost += 1;
                coinAccount.Stats.CurrentLossStreak += 1;
                coinAccount.Stats.CurrentWinStreak = 0;
                if (coinAccount.Stats.CurrentLossStreak > coinAccount.Stats.MaxLossStreak)
                    coinAccount.Stats.MaxLossStreak = coinAccount.Stats.CurrentLossStreak;

                if (losings > coinAccount.Stats.MaxMoneyLostAtOnce)
                    coinAccount.Stats.MaxMoneyLostAtOnce = losings;
            }
        }


        /// <summary>The comeback bonus tops up a win by at most this share of the profit.</summary>
        public const double MaxComebackBonus = 0.10;

        /// <summary>
        /// The comeback bonus: a player under half the prestige threshold in cash who's up on the day gets up to
        /// <see cref="MaxComebackBonus"/> more on the profit of a win, growing with how much they've won today. Small enough
        /// that it softens the house edge without making betting a money printer. Nothing on a loss.
        /// </summary>
        public double GetBonus(CoinAccount coinAccount, double baseWinnings, double betAmount)
        {
            double profit = baseWinnings - betAmount;
            if (profit <= 0) return 0;
            double p = coinAccount.GetPrestigeThreshold(_appSettings.PrestigeDisabled) / 2;
            if (coinAccount.NetWorth > p) return 0;
            double moneyWon = Math.Max(0, coinAccount.NetWinningsToday);
            if (moneyWon <= 0) return 0;
            double multiplier = Math.Clamp(moneyWon / p * 14 * MaxComebackBonus / 0.75, 0.01, MaxComebackBonus);
            return Math.Floor(multiplier * profit * 100) / 100;
        }
    }
}
