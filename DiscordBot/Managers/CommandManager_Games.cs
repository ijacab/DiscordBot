using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Models;
using DiscordBot.Games.Managers;
using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Games.Models;
using DiscordBot.Games.Models.Cards;
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
                        await _blackjackManager.Start(playerId, GameContext.From(message));
                    }
                    catch (NotInGameException)
                    {
                        if (TryExtractBetAmount(new List<string>(), coinAccount, out double minBetAmount)) //'.bj 1000'
                        {
                            await _blackjackManager.CreateOrJoin(playerId, minBetAmount, GameContext.From(message)); //will throw an exception if player already in a game, don't need to check
                            await _blackjackManager.Start(playerId, GameContext.From(message));
                        }
                    }

                    await TryDeleteMessageAsync(message); //the table shows what happened, so the typed command is only clutter
                    return;
                }
                else if (args[0].StartsWith("stay"))//'.bj stay'
                {
                    await _blackjackManager.Stay(playerId, GameContext.From(message));
                    await TryDeleteMessageAsync(message);
                    return;
                }
                else if (args[0].StartsWith("hit"))//'.bj hit'
                {
                    await _blackjackManager.Hit(playerId, GameContext.From(message));
                    await TryDeleteMessageAsync(message);
                    return;
                }
            }

            if (TryExtractBetAmount(args, coinAccount, out double betAmount)) //'.bj 1000'
            {
                await _blackjackManager.CreateOrJoin(playerId, betAmount, GameContext.From(message)); //will throw an exception if player already in a game, don't need to check
                await TryDeleteMessageAsync(message);
            }
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
                    await _duckRaceManager.Start(playerId, GameContext.From(message));
                }
                catch (NotInGameException)
                {
                    if (TryExtractBetAmount(new List<string>(), coinAccount, out double minBetAmount)) //'.duck start' with no game yet
                    {
                        await _duckRaceManager.CreateOrJoin(playerId, minBetAmount, GameContext.From(message)); //will throw an exception if player already in a game, don't need to check
                        await _duckRaceManager.Start(playerId, GameContext.From(message));
                    }
                }

                return;
            }

            if (TryExtractBetAmount(args, coinAccount, out double betAmount)) //'.duck 1000'
            {
                await _duckRaceManager.CreateOrJoin(playerId, betAmount, GameContext.From(message)); //will throw an exception if player already in a game, don't need to check
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

        /// <summary>
        /// Reads a bet amount from <paramref name="args"/> at <paramref name="betAmountIndex"/> with <see cref="Money.TryParse"/>
        /// (1000, 2.5k, 25%, max, min...). A missing amount means the minimum bet. False for junk, NaN, 0 or negatives.
        /// </summary>
        private static bool TryExtractBetAmount(IEnumerable<string> args, CoinAccount coinAccount, out double betAmount, int betAmountIndex = 0)
        {
            var list = args.ToList();
            string text = list.Count <= betAmountIndex ? "min" : list[betAmountIndex];
            return Money.TryParse(text, coinAccount.NetWorth, out betAmount);
        }
    }
}
