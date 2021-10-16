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
        /// <returns>True if the hourly bonus was newly granted, otherwise false.</returns>
        public async Task<(bool WasBonusGranted, bool IsFirstGameOfTheDay)> InitiateBet(ulong userId, string userName, double betAmount)
        {
            CoinAccount coinAccount = await _coinService.Get(userId, userName);
            EnsureGameMoneyInputIsValid(betAmount, coinAccount, null);

            //minus their input money - they will get it back when the game ends (if they don't lose)
            coinAccount.NetWorth -= betAmount;

            UpdateInitiateBetStats(coinAccount, betAmount);

            bool overFiftyPercentBet = false;
            if (betAmount >= (coinAccount.NetWorth * 0.5) - 1)
                overFiftyPercentBet = true; //if bet made over 50% networth for that day they get the bonus

            bool bonusGranted = false, firstGameOfTheDay = false;
            var todayString = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            if (coinAccount.MostRecentDatePlayed != todayString)
            {
                firstGameOfTheDay = true;
                if (overFiftyPercentBet)
                {
                    coinAccount.MostRecentDatePlayed = todayString;
                    bonusGranted = true;
                }
            }

            await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, userName, updateRemote: false);
            return (bonusGranted, firstGameOfTheDay);
        }

        public async Task<(double BonusWinnings, double TotalWinnings)> ResolveBet(ulong userId, string userName, double betAmount, double baseWinnings, bool isFirstGameOfTheDay)
        {
            CoinAccount coinAccount = await _coinService.Get(userId, userName);

            double bonusWinnings = baseWinnings * CalculateBonusMultiplier(coinAccount);
            double totalWinnings = baseWinnings + bonusWinnings;
            if (isFirstGameOfTheDay)
                coinAccount.MoneyWonToday = totalWinnings;
            else
                coinAccount.MoneyWonToday += totalWinnings;

            coinAccount.NetWorth += totalWinnings;

            UpdateResolveBetStats(coinAccount, betAmount, totalWinnings);

            await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, userName);
            return (bonusWinnings, totalWinnings);
        }

        public async Task ResolveBet(IEnumerable<IPlayer> players)
        {

            int i = 1;
            foreach (var player in players)
            {
                CoinAccount coinAccount = await _coinService.Get(player.UserId, player.Username);

                double bonusWinnings = player.BaseWinnings * CalculateBonusMultiplier(coinAccount);
                player.BonusWinnings = bonusWinnings;
                
                double totalWinnings = player.BaseWinnings + bonusWinnings;

                bool firstGameOfTheDay = false;
                var todayString = DateTimeOffset.UtcNow.ToString("yyyyMMdd");

                if (coinAccount.MostRecentDatePlayed != todayString)
                    firstGameOfTheDay = true;

                if (firstGameOfTheDay)
                    coinAccount.MoneyWonToday = totalWinnings;
                else
                    coinAccount.MoneyWonToday += totalWinnings;

                coinAccount.NetWorth += totalWinnings;

                UpdateResolveBetStats(coinAccount, player.BetAmount, player.BaseWinnings);

                bool updateRemote = false;
                if (i == players.Count())
                    updateRemote = true;

                await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, player.Username, updateRemote: updateRemote);
            }
        }

        private void EnsureGameMoneyInputIsValid(double inputMoney, CoinAccount account, int? minimumPercentBetRequired = null)
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
                if (coinAccount.Stats.CurrentLossStreak > coinAccount.Stats.MaxLossStreak)
                    coinAccount.Stats.MaxLossStreak = coinAccount.Stats.CurrentLossStreak;

                if (losings > coinAccount.Stats.MaxMoneyLostAtOnce)
                    coinAccount.Stats.MaxMoneyLostAtOnce = losings;
            }
        }


        public double CalculateBonusMultiplier(CoinAccount coinAccount)
        {
            double p = coinAccount.GetAmountRequiredForNextLevel();
            double moneyWon = coinAccount.MoneyWonToday;
            double m = 50;
            double y = (m / p) * moneyWon;
            double multiplier = y / 100; //0 at 0 moneyWon and 2 at p moneyWon
            if (multiplier > 0.5) multiplier = 0.5;
            return multiplier;
        }
    }
}
