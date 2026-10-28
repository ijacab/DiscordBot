using Common.Helpers;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
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
        private readonly AppSettings _appSettings;
        public List<ulong> InitiatedBetUserIds = new List<ulong>();
        private readonly Dictionary<ulong, DailyRollResult> _pendingDailyRolls = new Dictionary<ulong, DailyRollResult>();
        private const int StealRollNeeded = 10;
        private const double StealPercent = 0.10;

        public class DailyRollResult
        {
            public int Roll { get; set; }
            public double StolenAmount { get; set; }
            public string StolenFromName { get; set; }
            public ulong StolenFromUserId { get; set; }

            public override string ToString()
            {
                if (StolenAmount > 0)
                    return $"🎲 First gamble of the day! Rolled a {Roll} and stole ${Common.Helpers.FormatHelper.GetCommaNumber(StolenAmount)} from {StolenFromName}!";
                return $"🎲 First gamble of the day! Rolled a {Roll}. (Roll a {StealRollNeeded} to steal {StealPercent * 100}% of the top player's net worth.)";
            }
        }

        /// <summary>
        /// Returns (and clears) the daily d10 roll made when the user's gamble was last resolved, if any.
        /// </summary>
        public DailyRollResult TakeDailyRoll(ulong userId)
        {
            if (!_pendingDailyRolls.Remove(userId, out var result))
                return null;
            return result;
        }

        public BetManager(CoinService coinService, AppSettings appSettings)
        {
            _coinService = coinService;
            _appSettings = appSettings;
        }

        /// <summary>
        /// Resolves the bet and updates the user's account based on the result.
        /// </summary>
        public async Task InitiateBet(ulong userId, string userName, double betAmount, int? minimumPercentBetRequired = null)
        {
            if(InitiatedBetUserIds.Contains(userId))
                throw new BadInputException("Can't initiate a bet while another is in progress");
            if (betAmount < 1)
                throw new BadInputException("You need to bet at least a dollar to play bro... what do think this is? A casino for FUCKING poor people huh? FUCK HEAD FUCK OFF");

            CoinAccount coinAccount = await _coinService.Get(userId, userName);
            await InitiateBet(coinAccount, betAmount, minimumPercentBetRequired);
        }

        public async Task InitiateBet(CoinAccount coinAccount, double betAmount, int? minimumPercentBetRequired = null)
        {
            if (InitiatedBetUserIds.Contains(coinAccount.UserId))
                throw new BadInputException("Can't initiate a bet while another is in progress");
            if (betAmount < 1)
                throw new BadInputException("You need to bet at least a dollar to play bro... what do think this is? A casino for FUCKING poor people huh? FUCK HEAD FUCK OFF");

            EnsureGameMoneyInputIsValid(betAmount, coinAccount, minimumPercentBetRequired);

            UpdateInitiateBetStats(coinAccount, betAmount);

            //minus their input money - they will get it back when the game ends (if they don't lose)
            coinAccount.NetWorth -= betAmount;
            await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, coinAccount.Name, updateRemote: false);
            InitiatedBetUserIds.Add(coinAccount.UserId);
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
            ResolveBet(ulong userId, string userName, double betAmount, double baseWinnings, bool updateRemote = true)
        {
            if (!InitiatedBetUserIds.Contains(userId))
                throw new Exception("Something's gone wrong here. Somehow it is trying to resolve a bet which was not initiated.");

            CoinAccount coinAccount = await _coinService.Get(userId, userName);
            double netWorthBeforeBet = coinAccount.NetWorth + betAmount;
            
            bool overFiftyPercentBet = false;
            if (betAmount >= (0.5 * netWorthBeforeBet) - 1)
                overFiftyPercentBet = true; //if bet made over 50% networth for that day they get the bonus

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

            double bonusWinnings = GetBonus(coinAccount, baseWinnings);
            double totalWinnings = baseWinnings + bonusWinnings;

            double netWinnings = Math.Floor(baseWinnings - betAmount);
            if (firstGameOfTheDay)
                coinAccount.NetWinningsToday = netWinnings;
            else
                coinAccount.NetWinningsToday += netWinnings;

            coinAccount.NetWorth += totalWinnings;

            if(coinAccount.NetWinningsToday < 0 
                || Math.Floor(coinAccount.NetWorth) == 0)
                coinAccount.NetWinningsToday = 0;

            UpdateResolveBetStats(coinAccount, betAmount, totalWinnings);

            if (firstGambleOfTheDay)
                _pendingDailyRolls[userId] = RollDailySteal(coinAccount);

            await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, userName, updateRemote);
            InitiatedBetUserIds.Remove(userId);
            return (bonusWinnings, totalWinnings, netWinnings, bonusGranted);
        }

        /// <summary>
        /// Rolls a d10; on a 10 moves 10% of the top player's cash net worth to the roller. Mutates both accounts in memory
        /// (the caller persists via CoinService.Update, which serializes every account).
        /// </summary>
        private DailyRollResult RollDailySteal(CoinAccount roller)
        {
            var result = new DailyRollResult { Roll = DiceRoller.RollDice(1, 10)[0] };
            if (result.Roll != StealRollNeeded)
                return result;

            var top = _coinService.GetTopAccount(excludeUserId: roller.UserId);
            if (top == null || top.NetWorth < roller.NetWorth)
                return result; //roller is already the top player (or there's nobody to steal from)

            double amount = Math.Floor(top.NetWorth * StealPercent);
            if (amount <= 0)
                return result;

            top.NetWorth -= amount;
            roller.NetWorth += amount;
            result.StolenAmount = amount;
            result.StolenFromName = top.Name;
            result.StolenFromUserId = top.UserId;
            return result;
        }

        public async Task ResolveBet(IEnumerable<IPlayer> players)
        {
            int i = 1;
            bool updateRemote = false;
            foreach (var player in players)
            {
                if (i == players.Count())
                    updateRemote = true;

                var (BonusWinnings, TotalWinnings, NetWinnings, WasBonusGranted) = await ResolveBet(player.UserId, player.Username, player.BetAmount, player.BaseWinnings, updateRemote);
                player.BonusWinnings = BonusWinnings; //this gets used outside of the method
                i++;
            }
        }

        private void EnsureGameMoneyInputIsValid(double inputMoney, CoinAccount account, int? minimumPercentBetRequired)
        {
            if (inputMoney <= 0)
                throw new BadInputException($"You didn't place any valid bets FUCK HEAD");

            if (inputMoney > account.NetWorth)
                throw new BadInputException($"CAN'T BET WITH MORE MONEY THAN YOU HAVE DUMBASS. YOU HAVE ${FormatHelper.GetCommaNumber(account.NetWorth)}");

            if (minimumPercentBetRequired != null)
            {
                double betRequired = account.NetWorth * ((double)minimumPercentBetRequired / 100);
                if (inputMoney < betRequired - 1)
                    throw new BadInputException($"Total bet amount must be at least {minimumPercentBetRequired}% of your net worth. Bet at least ${FormatHelper.GetCommaNumber(betRequired + 1)} or higher.");
            }
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


        public double GetBonus(CoinAccount coinAccount, double baseWinnings)
        {
            double p = coinAccount.GetPrestigeThreshold(_appSettings.PrestigeDisabled) / 2;
            if (coinAccount.NetWorth > p) return 0;
            double moneyWon = coinAccount.NetWinningsToday < 0 ? 0 : coinAccount.NetWinningsToday;
            double m = 1;
            double y = (m / p) * moneyWon * 14;
            double multiplier = y;
            if (multiplier == 0) return 0;
            if (multiplier > 0.75) multiplier = 0.75; //0 at 0 moneyWon and 0.75 at max
            if (multiplier < 0.05) multiplier = 0.05;

            double bonus = multiplier * baseWinnings;
            double nw = coinAccount.NetWorth + baseWinnings;
            if (bonus > nw * 0.1)
                bonus = nw * 0.1;

            return bonus;
        }
    }
}
