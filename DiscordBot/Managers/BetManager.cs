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
        public async Task InitiateBet(ulong userId, string userName, double betAmount)
        {
            CoinAccount coinAccount = await _coinService.Get(userId, userName);
            EnsureGameMoneyInputIsValid(betAmount, coinAccount, null);

            //minus their input money - they will get it back when the game ends (if they don't lose)
            coinAccount.NetWorth -= betAmount;

            bool overFiftyPercentBet = false;
            if (betAmount >= (coinAccount.NetWorth * 0.5) - 1)
                overFiftyPercentBet = true; //if bet made over 50% networth for that day they get the bonus

            await _coinService.Update(coinAccount.UserId, coinAccount.NetWorth, userName, hourlyBonusGranted: overFiftyPercentBet, updateRemote: false);
        }

        /// <summary>
        /// Resolves the bet and updates the user's account based on the result.
        /// </summary>
        /// <returns>True if the hourly bonus was newly granted, otherwise false.</returns>
        public async Task<bool> ResolveBet(ulong userId, string userName, double updatedNetWorth)
        {
            CoinAccount account = await _coinService.Get(userId, userName);
            double oldNetWorth = account.NetWorth;
            double winnings = updatedNetWorth - oldNetWorth;
            account.NetWorth = updatedNetWorth;

            return await _coinService.Update(account.UserId, account.NetWorth, userName);
        }

        public async Task ResolveBet(IEnumerable<IPlayer> players)
        {
            int i = 1;
            foreach (var player in players)
            {
                CoinAccount account = await _coinService.Get(player.UserId, player.Username);
                account.NetWorth += player.Winnings;

                bool overFiftyPercentBet = false;
                if (player.BetAmount >= (account.NetWorth * 0.5) - 1)
                    overFiftyPercentBet = true; //if bet made over 50% networth for that day they get the bonus

                bool updateRemote = false;
                if (i == players.Count())
                    updateRemote = true;

                await _coinService.Update(account.UserId, account.NetWorth, player.Username, hourlyBonusGranted: overFiftyPercentBet, updateRemote: updateRemote);
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
    }
}
