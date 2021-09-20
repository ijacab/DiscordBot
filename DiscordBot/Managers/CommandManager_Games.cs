using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Models;
using Common.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Managers
{
    public partial class CommandManager
    {
        private async Task GameRoulette(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 1)
                throw new BadSyntaxException();

            ulong userId = message.Author.Id;

            CoinAccount account = await _coinService.Get(userId, message.Author.Username);

            string[] inputs = args[0].Split(',');
            var inputBets = new List<RouletteBet>();

            double tenPercentNw = (account.NetWorth * 0.1);
            try
            {

                foreach (var input in inputs)
                {
                    //each 'input' looks like e.g. red-1000
                    string[] inputParams = input.Split('-');

                    double value = 0;
                    if (inputParams.Length == 1 || inputParams[1].StartsWith("min"))
                        value = tenPercentNw;
                    else if (inputParams[1].StartsWith("max"))
                        value = account.NetWorth;
                    else if (inputParams[1].EndsWith('%'))
                    {
                        double amountMultiple = Convert.ToDouble(inputParams[1].Substring(0, inputParams[1].Length - 1)) / 100;
                        value = account.NetWorth * amountMultiple;
                    }
                    else
                        value = Convert.ToDouble(inputParams[1]);

                    inputBets.Add(new RouletteBet(userId, inputParams[0], value));
                }
            }
            catch (Exception)
            {
                throw new BadSyntaxException();
            }

            double inputMoney = 0;
            inputBets.Where(b => b.RoulleteBetType != BetType.NotValid).ToList().ForEach(b => inputMoney += b.Amount);

            if (inputMoney == 0)
                throw new BadInputException($"You didn't place any valid bets FUCK HEAD");

            if (inputMoney > account.NetWorth)
                throw new BadInputException($"CAN'T BET WITH MORE MONEY THAN YOU HAVE DUMBASS. YOU HAVE ${FormatHelper.GetCommaNumber(account.NetWorth)}");

            if (inputMoney < tenPercentNw - 1)
                throw new BadInputException($"Total bet amount must be at least 10% of your net worth. Bet at least ${FormatHelper.GetCommaNumber(tenPercentNw + 1)} or higher.");

            bool overFiftyPercentBet = false;
            if (inputMoney >= (account.NetWorth * 0.5) - 1)
                overFiftyPercentBet = true; //if bet made over 50% networth for that day they get the bonus


            account.NetWorth -= inputMoney;

            var resultTuple = new Roulette().Play(inputBets);
            string resultString = "";
            for (int i = 0; i < resultTuple.Item1.Count; i++)
            {
                string value = resultTuple.Item1[i];
                string str = value == "-1" ? "00" : value;

                resultString += i == 0 ? $"**{str}**, " : $"{str}, ";
            }

            resultString = resultString.TrimEnd(' ').TrimEnd(',');

            List<RouletteBet> winningBets = resultTuple.Item2;

            string output = $"Winning results were {resultString}.\n";
            if (winningBets.Count == 0)
                output += $"{message.Author.Username} you did not make any successful bets. Your net worth is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.";
            else
            {
                output += "`Your winning bets were:`\n";
                foreach (var winningBet in winningBets)
                {
                    double amountBack = winningBet.Amount + (winningBet.PayoutMultiple * winningBet.Amount);
                    account.NetWorth += amountBack;
                    output += winningBet.BetNumberChoice != null
                        ? $"{winningBet.BetNumberChoice.ToString().Replace("-1", "00")}: ${FormatHelper.GetCommaNumber(winningBet.Amount)} -> ${FormatHelper.GetCommaNumber(amountBack)}\n"
                        : $"{winningBet.RoulleteBetType}: ${FormatHelper.GetCommaNumber(winningBet.Amount)} -> ${FormatHelper.GetCommaNumber(amountBack)}\n";
                }
                output += $"{message.Author.Mention} `Your networth is now {FormatHelper.GetCommaNumber(account.NetWorth)}`";
            }

            bool bonusGranted = await _coinService.Update(account.UserId, account.NetWorth, message.Author.Username, overFiftyPercentBet);

            if (overFiftyPercentBet && bonusGranted)
                output += $"\n\n*You will get a bonus $1000 + 10% net worth each hour for the rest of the day (UTC).*";

            await message.Channel.SendMessageAsync(output);

        }

        //private async Task GameBlackjack(DiscordSocketClient client, SocketMessage message, List<string> args)
        //{
        //    if (args.Count == 0)
        //        throw new BadSyntaxException();

        //    var blackjack = new Blackjack(message.Author.Id);

        //    if(args)

        //    message.

        //    ulong userId = message.Author.Id;

        //    CoinAccount account = await _coinService.Get(userId, message.Author.Username);

        //    await message.Channel.SendMessageAsync(output);

        //}
    }
}
