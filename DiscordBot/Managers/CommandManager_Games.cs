using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Managers;
using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Games.Models;
using DiscordBot.Games.Models.BattleArena;
using DiscordBot.Services;
using Common.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
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

            CoinAccount coinAccount = await _coinService.Get(userId, message.Author.Username);

            string[] inputs = args[0].Split(',');
            var inputBets = new List<RouletteBet>();

            int minPercentBetRequired = 10;
            double tenPercentNw = coinAccount.NetWorth * ((double)minPercentBetRequired / 100);
            foreach (var input in inputs)
            {
                //each 'input' looks like e.g. red-1000
                string[] inputParams = input.Split('-');

                if (!TryExtractBetAmount(inputParams, coinAccount, out double betAmount, betAmountIndex: 1))
                    throw new BadSyntaxException();

                inputBets.Add(new RouletteBet(userId, inputParams[0], betAmount));
            }

            double inputMoney = 0;
            inputBets.Where(b => b.RoulleteBetType != BetType.NotValid).ToList().ForEach(b => inputMoney += b.Amount);

            await _betManager.InitiateBet(coinAccount, inputMoney, 10);

            string resultString = "";
            List<RouletteBet> winningBets;
            try
            {
                var resultTuple = new Roulette().Play(inputBets);
                for (int i = 0; i < resultTuple.Item1.Count; i++)
                {
                    string value = resultTuple.Item1[i];
                    string str = value == "-1" ? "00" : value;

                    resultString += i == 0 ? $"**{str}**, " : $"{str}, ";
                }

                resultString = resultString.TrimEnd(' ').TrimEnd(',');

                winningBets = resultTuple.Item2;
            }
            catch (Exception)
            {
                await _betManager.CancelBet(userId, message.Author.Username, inputMoney);
                throw;
            }

            double baseWinnings = 0;
            string output = $"Winning results were {resultString}.\n";
            (double BonusWinnings, double TotalWinnings, double NetWinnings, bool WasBonusGranted) betResolve;
            if (winningBets.Count == 0)
            {
                output += $"{message.Author.Username} you did not make any successful bets. Your net worth is now ${FormatHelper.GetCommaNumber(coinAccount.NetWorth)}.";
                betResolve = await _betManager.ResolveBet(userId, message.Author.Username, inputMoney, baseWinnings);
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

                betResolve = await _betManager.ResolveBet(userId, message.Author.Username, inputMoney, baseWinnings);
                output += $"(+ bonus of ${ FormatHelper.GetCommaNumber(betResolve.BonusWinnings)})\n";
                output += $"{message.Author.Mention} `Your networth is now ${FormatHelper.GetCommaNumber(coinAccount.NetWorth)}`";
            }

            if (betResolve.WasBonusGranted)
                output += _appSettings.PrestigeDisabled
                    ? $"\n\n*You will get a bonus {Constants.InterestPercentage}% net worth each hour for the rest of the day (UTC).*"
                    : $"\n\n*You will get a bonus $1000 + {Constants.InterestPercentage}% net worth each hour for the rest of the day (UTC).*";

            var dailyRoll = _betManager.TakeDailyRoll(userId);
            if (dailyRoll != null)
                output += $"\n\n{dailyRoll}";

            string jackpotWin = _betManager.TakeJackpotWin(userId);
            if (jackpotWin != null)
                output += $"\n\n{jackpotWin}";

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

            if (_blackjackManager.TryGetPlayer(playerId, out var player)
                && player.IsFinishedPlaying) //IsFinishedPlaying will be set to true if player is in a game but it has not finished. 
            {
                await message.SendRichEmbedMessage($"You are still in a game but your turn is over. Once it ends and bets are calculated you can join another game.");
                return;
            }

            CoinAccount coinAccount = await _coinService.Get(playerId, message.Author.Username);
            if (args.Count() > 0)
            {
                if (args[0].StartsWith("start"))//'.bj start'
                {
                    try
                    {
                        await _blackjackManager.Start(playerId, message);
                    }
                    catch (NotInGameException)
                    {
                        if (TryExtractBetAmount(new List<string>(), coinAccount, out double minBetAmount)) //'.bj 1000'
                        {
                            await _blackjackManager.CreateOrJoin(playerId, minBetAmount, message); //will throw an exception if player already in a game, don't need to check
                            await _blackjackManager.Start(playerId, message);
                        }
                    }

                    return;
                }
                else if (args[0].StartsWith("stay"))//'.bj stay'
                {
                    await _blackjackManager.Stay(playerId, message);
                    return;
                }
                else if (args[0].StartsWith("hit"))//'.bj hit'
                {
                    await _blackjackManager.Hit(playerId, message);
                    return;
                }
            }

            if (TryExtractBetAmount(args, coinAccount, out double betAmount)) //'.bj 1000'
            {
                await _blackjackManager.CreateOrJoin(playerId, betAmount, message); //will throw an exception if player already in a game, don't need to check
            }
        }

        private async Task GameFaceOff(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            //valid inputs:
            //1000|max|25%     - create or join the lobby with that bet
            //start            - start the lobby (joins with the minimum first if you're not in it)
            //attack|defend|focus|gamble [name] - pick this round's action, optionally aimed at an opponent
            //ability | leave | back name amount | status
            ulong playerId = message.Author.Id;
            IMessageChannel channel = message.Channel;
            string action = args.Count > 0 ? args[0].ToLowerInvariant() : "";
            ulong guildId = (channel as SocketGuildChannel)?.Guild.Id ?? 0;

            (bool Ok, string Message) result;
            if (BattleActionInfo.TryParse(action, out BattleAction battleAction))
            {
                //the dashboard shows who has picked; deleting the command keeps the pick itself hidden where the bot can
                await TryDeleteMessageAsync(message);
                ulong? targetId = null;
                if (args.Count > 1)
                {
                    string wanted = string.Join(" ", args.Skip(1)).Trim().TrimStart('@').ToLowerInvariant();
                    var match = _battleArenaManager.OpponentsOf(playerId).FirstOrDefault(o => o.Name.ToLowerInvariant().StartsWith(wanted));
                    if (match.Name == null)
                    {
                        _ = PostTransientAsync(channel, $"{message.Author.Mention} No opponent called {wanted} is still in your fight.");
                        return;
                    }
                    targetId = match.Id;
                }
                result = await _battleArenaManager.Pick(playerId, battleAction, targetId);
                if (result.Ok)
                    result.Message = $"{message.Author.Username} has picked.";
            }
            else
            {
                switch (action)
                {
                    case "ability":
                        result = await _battleArenaManager.UseAbility(playerId);
                        break;
                    case "leave":
                        result = await _battleArenaManager.Leave(playerId);
                        break;
                    case "status":
                        result = await _battleArenaManager.Bump(playerId, channel);
                        break;
                    case "back":
                        result = await TypedBack(message, args);
                        break;
                    case "start":
                        if (_battleArenaManager.GameOf(playerId) == null)
                        {
                            CoinAccount starter = await _coinService.Get(playerId, message.Author.Username);
                            TryExtractBetAmount(new List<string>(), starter, out double minBet);
                            result = await _battleArenaManager.Join(starter, minBet, channel, guildId);
                            if (!result.Ok)
                                break;
                        }
                        result = await _battleArenaManager.Start(playerId);
                        break;
                    default:
                        CoinAccount account = await _coinService.Get(playerId, message.Author.Username);
                        if (!TryExtractBetAmount(args, account, out double bet))
                            throw new BadSyntaxException();
                        result = await _battleArenaManager.Join(account, bet, channel, guildId);
                        break;
                }
            }

            if (!string.IsNullOrEmpty(result.Message))
                _ = PostTransientAsync(channel, $"{message.Author.Mention} {result.Message}");
        }

        /// <summary>`.fo back name amount`: backs a fighter in the open fight by name.</summary>
        private async Task<(bool Ok, string Message)> TypedBack(SocketMessage message, List<string> args)
        {
            if (args.Count < 3)
                return (false, "Use `.fo back name amount`, for example `.fo back sam 1000`.");

            string wanted = args[1].TrimStart('@').ToLowerInvariant();
            Guid? gameId = null;
            ulong fighterId = 0;
            foreach (Guid id in _battleArenaManager.Games.Select(g => g.GameGuid).ToList())
            {
                var fighter = _battleArenaManager.FightersOf(id).FirstOrDefault(f => f.Name.ToLowerInvariant().StartsWith(wanted));
                if (fighter.Name != null)
                {
                    gameId = id;
                    fighterId = fighter.Id;
                    break;
                }
            }
            if (gameId == null)
                return (false, $"No fighter called {wanted} is in a Face Off right now.");

            CoinAccount backer = await _coinService.Get(message.Author.Id, message.Author.Username);
            if (!TryExtractBetAmount(args, backer, out double amount, betAmountIndex: 2))
                return (false, "That amount doesn't look right. Use a number, a percentage like 10%, or max.");
            return await _battleArenaManager.Back(backer, gameId.Value, fighterId, amount);
        }

        private async Task GameDuckRace(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var playerId = message.Author.Id;

            //args valid inputs:
            //1000|any numbers - create or join race with 1000 as your bet
            //start - start the race without waiting

            CoinAccount coinAccount = await _coinService.Get(playerId, message.Author.Username);
            if (args.Count() > 0 && args[0].StartsWith("start"))//'.duck start'
            {
                try
                {
                    await _duckRaceManager.Start(playerId, message);
                }
                catch (NotInGameException)
                {
                    if (TryExtractBetAmount(new List<string>(), coinAccount, out double minBetAmount)) //'.duck start' with no game yet
                    {
                        await _duckRaceManager.CreateOrJoin(playerId, minBetAmount, message); //will throw an exception if player already in a game, don't need to check
                        await _duckRaceManager.Start(playerId, message);
                    }
                }

                return;
            }

            if (TryExtractBetAmount(args, coinAccount, out double betAmount)) //'.duck 1000'
            {
                await _duckRaceManager.CreateOrJoin(playerId, betAmount, message); //will throw an exception if player already in a game, don't need to check
            }
        }

        private async Task GameDungeon(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            ulong userId = message.Author.Id;
            string name = message.Author.Username;
            IMessageChannel channel = message.Channel;
            string action = args.Count > 0 ? args[0].ToLowerInvariant() : "";

            //the dashboard shows the result, so the command itself is just noise in the channel
            await TryDeleteMessageAsync(message);

            string refusal;
            switch (action)
            {
                case "":
                case "status":
                    refusal = await _dungeonManager.Status(channel);
                    break;
                case "join":
                    refusal = await _dungeonManager.Join(await _coinService.Get(userId, name));
                    break;
                case "leave":
                    refusal = await _dungeonManager.Leave(userId);
                    break;
                case "start":
                    refusal = await _dungeonManager.Start(userId);
                    break;
                case "go":
                    refusal = await _dungeonManager.Go(userId);
                    break;
                case "cash":
                    refusal = await _dungeonManager.Cash(userId);
                    break;
                case "fight":
                case "pvp":
                    refusal = await _dungeonManager.EnterPool(userId);
                    break;
                case "boss":
                    refusal = await _dungeonManager.Boss(userId, BossChoice.Fight);
                    break;
                case "sneak":
                    refusal = await _dungeonManager.Boss(userId, BossChoice.Sneak);
                    break;
                case "potion":
                    refusal = await _dungeonManager.Potion(userId);
                    break;
                default:
                    CoinAccount host = await _coinService.Get(userId, name);
                    if (!TryExtractBetAmount(args, host, out double buyIn))
                        throw new BadSyntaxException();
                    refusal = await _dungeonManager.Open(host, buyIn, channel);
                    break;
            }

            if (refusal != null)
                _ = PostTransientAsync(channel, $"{message.Author.Mention} {refusal}");
        }

        private static readonly TimeSpan TransientLifetime = TimeSpan.FromSeconds(15);

        /// <summary>Posts a short message, then deletes it, so refusals to typed game commands don't stay in the channel.</summary>
        private static async Task PostTransientAsync(IMessageChannel channel, string text)
        {
            try
            {
                IUserMessage note = await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None);
                await Task.Delay(TransientLifetime);
                await note.DeleteAsync();
            }
            catch (Exception)
            {
                //the note is only a hint, so failing to post or delete it doesn't matter
            }
        }

        /// <summary>Needs the Manage Messages permission to delete other people's messages. Without it the command stays, and the dashboard still updates.</summary>
        private static async Task TryDeleteMessageAsync(SocketMessage message)
        {
            try
            {
                await message.DeleteAsync();
            }
            catch (Exception)
            {
                //missing permission or the message is already gone, so there's nothing else to do
            }
        }

        private bool TryExtractBetAmount(IEnumerable<string> args, CoinAccount coinAccount, out double betAmount, int betAmountIndex = 0)
        {
            betAmount = 0;
            try
            {
                int minPercentBetRequired = 10;
                double tenPercentNw = coinAccount.NetWorth * ((double)minPercentBetRequired / 100);

                if (args.Count() == betAmountIndex || args.ElementAt(betAmountIndex).StartsWith("min"))
                    betAmount = tenPercentNw;
                else if (args.ElementAt(betAmountIndex).StartsWith("max"))
                    betAmount = coinAccount.NetWorth;
                else if (args.ElementAt(betAmountIndex).EndsWith('%'))
                {
                    double amountMultiple = Convert.ToDouble(args.ElementAt(betAmountIndex).TrimEnd('%')) / 100;
                    betAmount = coinAccount.NetWorth * amountMultiple;
                }
                else
                    betAmount = Convert.ToDouble(args.ElementAt(betAmountIndex));

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
