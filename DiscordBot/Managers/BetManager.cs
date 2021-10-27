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

        public BetManager(CoinService coinService)
        {
            _coinService = coinService;
        }

        /// <summary>
        /// Resolves the bet and updates the user's account based on the result.
        /// </summary>
        public async Task InitiateBet(ulong userId, string userName, double betAmount, int? minimumPercentBetRequired = null)
        {
            CoinAccount coinAccount = await _coinService.Get(userId, userName);
            EnsureGameMoneyInputIsValid(betAmount, coinAccount, minimumPercentBetRequired);

            UpdateInitiateBetStats(coinAccount, betAmount);

            //minus their input money - they will get it back when the game ends (if they don't lose)
            coinAccount.NetWorth -= betAmount;
            await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, userName, updateRemote: false);
        }

        public async Task CancelBet(ulong userId, string userName, double betAmount)
        {
            CoinAccount coinAccount = await _coinService.Get(userId, userName);
            
            //give back betted money
            coinAccount.NetWorth += betAmount;

            await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, userName);
        }

        public async Task<(double BonusWinnings, double TotalWinnings, double NetWinnings, bool WasBonusGranted)> ResolveBet(ulong userId, string userName, double betAmount, double baseWinnings, bool updateRemote = true)
        {
            CoinAccount coinAccount = await _coinService.Get(userId, userName);
            double netWorthBeforeBet = coinAccount.NetWorth + betAmount;
            
            bool overFiftyPercentBet = false;
            if (betAmount >= netWorthBeforeBet - 1)
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

            double bonusWinnings = baseWinnings * CalculateBonusMultiplier(coinAccount);
            double totalWinnings = baseWinnings + bonusWinnings;

            double netWinnings = Math.Floor(baseWinnings - betAmount);
            if (firstGameOfTheDay)
                coinAccount.NetWinningsToday = netWinnings;
            else
                coinAccount.NetWinningsToday += netWinnings;

            coinAccount.NetWorth += totalWinnings;

            if(Math.Floor(coinAccount.NetWorth) == 0)
                coinAccount.NetWinningsToday = 0;

            UpdateResolveBetStats(coinAccount, betAmount, totalWinnings);

            await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, userName, updateRemote);
            return (bonusWinnings, totalWinnings, netWinnings, bonusGranted);
        }

        public async Task ResolveBet(IEnumerable<IPlayer> players)
        {
            int i = 1;
            bool updateRemote = false;
            foreach (var player in players)
            {
                if (i == players.Count())
                    updateRemote = true;

                await ResolveBet(player.UserId, player.Username, player.BetAmount, player.BaseWinnings, updateRemote);
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


        public double CalculateBonusMultiplier(CoinAccount coinAccount)
        {
            double p = coinAccount.GetAmountRequiredForNextLevel();
            double moneyWon = coinAccount.NetWinningsToday < 0 ? 0 : coinAccount.NetWinningsToday;
            double m = 1;
            double y = (m / p) * moneyWon * 5;
            double multiplier = y; //0 at 0 moneyWon and 2 at p moneyWon
            if (multiplier > 1) multiplier = 1;
            return multiplier;
        }
    }
}
