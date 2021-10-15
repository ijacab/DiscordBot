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
using static DiscordBot.Games.Blackjack;

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

            int minPercentBetRequired = 10;
            double tenPercentNw = account.NetWorth * ((double)minPercentBetRequired / 100);
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

            var bet = await _betManager.InitiateBet(userId, message.Author.Username, inputMoney);
            bool wasBonusGranted = bet.WasBonusGranted;
            bool isFirstGameOfTheDay = bet.IsFirstGameOfTheDay;

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


            double baseWinnings = 0;
            string output = $"Winning results were {resultString}.\n";
            if (winningBets.Count == 0)
            {
                output += $"{message.Author.Username} you did not make any successful bets. Your net worth is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.";
                var betResolve = await _betManager.ResolveBet(userId, message.Author.Username, inputMoney, baseWinnings, isFirstGameOfTheDay);
            }
            else
            {
                output += "`Your winning bets were:`\n";
                foreach (var winningBet in winningBets)
                {
                    double amountBack = winningBet.Amount + (winningBet.PayoutMultiple * winningBet.Amount);
                    baseWinnings += amountBack;
                    output += winningBet.BetNumberChoice != null
                        ? $"{winningBet.BetNumberChoice.ToString().Replace("-1", "00")}: ${FormatHelper.GetCommaNumber(winningBet.Amount)} -> ${FormatHelper.GetCommaNumber(amountBack)}\n"
                        : $"{winningBet.RoulleteBetType}: ${FormatHelper.GetCommaNumber(winningBet.Amount)} -> ${FormatHelper.GetCommaNumber(amountBack)}\n";
                }

                var betResolve = await _betManager.ResolveBet(userId, message.Author.Username, inputMoney, baseWinnings, isFirstGameOfTheDay);
                output += $"(+ bonus of ${ FormatHelper.GetCommaNumber(betResolve.BonusWinnings)})\n";
                output += $"{message.Author.Mention} `Your networth is now ${FormatHelper.GetCommaNumber(account.NetWorth)}`";
            }



            if (wasBonusGranted)
                output += $"\n\n*You will get a bonus $1000 + 10% net worth each hour for the rest of the day (UTC).*";

            await message.Channel.SendMessageAsync(output);

        }

        private async Task GameBlackjack(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var playerId = message.Author.Id;

            //args valid inputs:
            //1000|any numbers - create or join game with 1000 as your bet
            //s|stay - blackjack stay action
            //h|hit - blackjack hit action
            //start - start the game without waiting

            if (args.Count != 1)
                throw new BadSyntaxException();

            if (_blackjackManager.TryGetPlayer(playerId, out var player)
                && player.IsFinishedPlaying) //IsFinishedPlaying will be set to true if player is in a game but it has not finished. 
            {
                await message.Channel.SendMessageAsync($"{message.Author.Mention} You are still in a game but your turn is over. Once it ends and bets are calculated you can join another game.");
                return;
            }

            string input = args[0];

            if (double.TryParse(input, out double inputMoney)) //'.bj 1000'
            {
                await _blackjackManager.CreateOrJoin(playerId, inputMoney, message); //will throw an exception if player already in a game, don't need to check
            }

            if (args[0].StartsWith("start"))//'.bj start'
            {
                await _blackjackManager.Start(playerId, message);
            }
            else if (args[0].StartsWith("stay"))//'.bj stay'
            {
                await _blackjackManager.Stay(playerId, message);
            }
            else if (args[0].StartsWith("hit"))//'.bj hit'
            {
                await _blackjackManager.Hit(playerId, message);
            }
        }


    }
}
