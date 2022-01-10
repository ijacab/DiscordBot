﻿using Common.Helpers;
using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Games.Blackjack;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games
{
    public class BlackjackManager //should be singleton
    {
        public List<Blackjack> Games = new List<Blackjack>();
        private const int _secondsToForceStartAfter = 30;
        private const int _secondsToForceEndAfter = 90;

        private readonly BetManager _betManager;
        private readonly DiscordSocketClient _client;
        private readonly CoinService _coinService;

        public BlackjackManager(DiscordSocketClient client, BetManager betManager, CoinService coinService)
        {
            _betManager = betManager;
            _client = client;
            _coinService = coinService;
        }

        /// <returns>True if new game created, false if joining existing game.</returns>
        public async Task<IUserMessage> CreateOrJoin(ulong playerId, double inputMoney, IMessage message)
        {
            if (TryGetPlayer(playerId, out _))
            {
                var joinedGame = GetExisitingGame(playerId); //get the game that the player is in
                if (joinedGame.Started)
                    throw new BadInputException("You are already in a game. Can only play 1 game at once. \nType `.bj hit` or `.bj stay`.");
                else
                    throw new BadInputException("You are already in a game but it hasn't started yet. \nType `.bj start` to start or wait for the game to start automatically.");
            }

            await _betManager.InitiateBet(playerId, message.Author.Username, inputMoney, 10);

            var channel = message.Channel as SocketGuildChannel;
            var guildId = channel.Guild.Id;
            var player = new BlackjackPlayer(playerId, message.Channel.Id, guildId, inputMoney, message.Author.Username);

            var openGame = Games.FirstOrDefault(g => g.Started == false);
            if (openGame == null) //if no open games found, create new game
            {
                var newGame = new Blackjack(player);
                Games.Add(newGame);

                //timer on starting the game
                //_ = Task.Delay(TimeSpan.FromSeconds(_secondsToForceStartAfter)).ContinueWith(t =>
                //{
                //    if (!newGame.Started)
                //        Start(playerId);
                //});
                return await message.SendRichEmbedMessage("Blackjack game created", $"If anyone else wants to join they need to type `.bj betAmount` to join where 'betAmount' is the amount you want to bet. For example `.bj 1000`.\nType `.bj start` to start the game.");
                
            }
            else
            {
                openGame.Join(player);
                return await message.SendRichEmbedMessage("Blackjack game joined", $"If anyone else wants to join they need to type `.bj betAmount` to join where 'betAmount' is the amount you want to bet. For example `.bj 1000`.\nType `.bj start` to start the game.");
            }

        }

        public async Task<List<IUserMessage>> Start(ulong playerId, IMessage message)
        {
            if (!TryGetPlayer(playerId, out _))
            {
                await message.SendRichEmbedMessage("Error", $"You have not joined any games FUCK FACE, you can't start someone else's game. Type `.bj betAmount` to join/create a game.");
                return null;
            }

            if (!IsGameStarted(playerId))
            {
                Guid gameId = Start(playerId);

                //timer on ending the game
                _ = Task.Delay(TimeSpan.FromSeconds(_secondsToForceEndAfter)).ContinueWith(async t =>
                {
                    if (TryGetExisitingGame(gameId, out _))
                    {
                        await EndGameForced(gameId);
                    }
                });

                var game = GetExisitingGame(playerId);
                var players = game.Players.Where(p => !p.IsDealer);
                var serverChannelMappings = players.Select(p => { return new Tuple<ulong, ulong>(p.ServerId, p.ChannelId); });
                var distinctServerChannelMappings = serverChannelMappings.Distinct();

                await distinctServerChannelMappings.SendMessageToEachChannel("Blackjack game started", $"No one else can join this game now.", _client);

                foreach (var gamePlayer in players)
                {
                    Hit(gamePlayer.UserId);
                    Hit(gamePlayer.UserId);
                }
                var dealer = game.GetDealer();
                game.Hit(dealer);

                if (await EndGameIfAllPlayersFinished(playerId))
                    return null;

                await distinctServerChannelMappings.SendMessageToEachChannel("Player standings", GameBlackjackGetFormattedPlayerStanding(playerId, _client), _client);
                return await distinctServerChannelMappings.SendMessageToEachChannel("Blackjack", "Type `.bj hit` or `.bj stay` to play.", _client);
            }
            else
            {
                var messageSent = await message.SendRichEmbedMessage("Error", $"The game you are in is already started. Type `.bj hit` or `.bj stay` to play.");
                return new List<IUserMessage>() { messageSent };
            }
        }

        /// <summary>
        /// Starts game and returns the game Guid.
        /// </summary>
        private Guid Start(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            return game.Start();
        }

        public bool IsGameStarted(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            return game.Started;
        }

        public async Task<IUserMessage> Hit(ulong playerId, IMessage message)
        {
            if (!TryGetPlayer(playerId, out _))
                throw new BadInputException($"You have not joined any games FUCK FACE, you can't stay. Type `.bj betAmount` to join/create a game.");

            if (!IsGameStarted(playerId))
                throw new BadInputException($"Game hasn't started yet. Type `.bj start` to start the game.");

            Hit(playerId);

            if (!await EndGameIfAllPlayersFinished(playerId))
            {
                return await message.SendRichEmbedMessage("Player standings", GameBlackjackGetFormattedPlayerStanding(playerId, _client));
            }

            return null;
        }

        private void Hit(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            var player = game.GetPlayer(playerId);

            if (player.IsFinishedPlaying) throw new BadInputException("You have already finished playing. Wait for the game to end and the results will be calculated.");

            game.Hit(player);
        }

        public async Task<IUserMessage> Stay(ulong playerId, IMessage message)
        {
            if (!TryGetPlayer(playerId, out _))
                throw new BadInputException($"You have not joined any games FUCK FACE, you can't stay. Type `.bj betAmount` to join/create a game.");

            if (!IsGameStarted(playerId))
                throw new BadInputException($"Game hasn't started yet. Type `.bj start` to start the game.");

            Stay(playerId);

            if (!await EndGameIfAllPlayersFinished(playerId))
            {
                return await message.SendRichEmbedMessage("Player standings", GameBlackjackGetFormattedPlayerStanding(playerId, _client));
            }

            return null;
        }

        private void Stay(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            TryGetPlayer(playerId, out var player);

            if (player.IsFinishedPlaying) throw new BadInputException("You have already finished playing. Wait for the game to end and the results will be calculated.");

            game.Stay(player);
        }

        public bool AreAllPlayersInSameGameFinished(Blackjack game)
        {
            if (game.Players.Where(p => !p.IsDealer).Any(p => !p.IsFinishedPlaying))
                return false;
            else
                return true;
        }

        public bool TryGetPlayer(ulong playerId, out BlackjackPlayer player)
        {
            player = null;
            try
            {
                var game = GetExisitingGame(playerId);
                player = game.GetPlayer(playerId);
            }
            catch (Exception)
            {
                return false;
            }

            if (player != null)
                return true;
            else
                return false;
        }

        public Blackjack GetExisitingGame(ulong playerId)
        {
            var game = Games.FirstOrDefault(g => g.Players.Select(p => p.UserId).Contains(playerId));
            if (game == null) throw new BadInputException("You are not in a game yet. Type '.bj \\*betAmount\\*' to create/join an open game.");
            return game;
        }

        public Blackjack GetExisitingGame(Guid gameGuid)
        {
            var game = Games.FirstOrDefault(g => g.GameGuid == gameGuid);
            if (game == null) throw new BadInputException("Game does not exist.");
            return game;
        }

        public bool TryGetExisitingGame(ulong playerId, out Blackjack game)
        {
            game = null;
            try
            {
                game = GetExisitingGame(playerId);
            }
            catch (Exception)
            {
                return false;
            }

            if (game != null)
                return true;
            else
                return false;
        }

        public bool TryGetExisitingGame(Guid gameGuid, out Blackjack game)
        {
            game = null;
            try
            {
                game = GetExisitingGame(gameGuid);
            }
            catch (Exception)
            {
                return false;
            }

            if (game != null)
                return true;
            else
                return false;
        }

        private string GameBlackjackGetFormattedPlayerStanding(ulong playerId, DiscordSocketClient client)
        {
            string output = "";
            var game = GetExisitingGame(playerId);

            var dealer = game.GetDealer();
            output += $"**Dealer**: {dealer.GetFormattedCards()}\n\n";

            foreach (var player in game.Players.Where(p => !p.IsDealer))
            {
                output += $"{client.GetUser(player.UserId).Username}: {player.GetFormattedCards()}\n";
            }

            return output;
        }

        /// <returns>True if game ended, false if not.</returns>
        private async Task<bool> EndGameIfAllPlayersFinished(ulong playerId)
        {
            var game = GetExisitingGame(playerId);

            if(!AreAllPlayersInSameGameFinished(game))
                return false;

            await EndGame(game);
            return true;
        }

        private async Task EndGameForced(Guid gameGuid)
        {
            var game = GetExisitingGame(gameGuid);
            foreach (var player in game.Players)
            {
                game.Stay(player);
            }

            await EndGame(game);
        }

        private async Task EndGame(Blackjack game)
        {
            string title = "Blackjack game results:";
            try
            {
                var playersInGame = PlayDealerAndCalculateWinnings(game);
                Games.Remove(game);
                var dealer = playersInGame.First(p => p.IsDealer);

                string output = $"\n**Dealer**: {dealer.GetFormattedCards()}\n";

                var players = game.Players.Where(p => !p.IsDealer);
                await _betManager.ResolveBet(players);
                foreach (var player in players)
                {
                    CoinAccount coinAccount = await _coinService.Get(player.UserId, player.Username);
                    string bonusLine = player.BaseWinnings > 0 ? $"(+ ${FormatHelper.GetCommaNumber(player.BonusWinnings)} bonus)" : string.Empty;
                    output += $"\n{player.Username}: {player.GetFormattedCards()}" +
                        $"\n\t${FormatHelper.GetCommaNumber(player.BetAmount)} -> ${FormatHelper.GetCommaNumber(player.BaseWinnings)}" +
                        $"\t{bonusLine}" +
                        $"\n\t`Networth is now {FormatHelper.GetCommaNumber(coinAccount.NetWorth)}`";
                }

                var serverChannelMappings = players.Select(p => { return new Tuple<ulong, ulong>(p.ServerId, p.ChannelId); });
                var distinctServerChannelMappings = serverChannelMappings.Distinct();

                await distinctServerChannelMappings.SendMessageToEachChannel(title, output, _client);
            }
            catch (Exception)
            {
                Games.Remove(game);
                throw;
            }
        }

        /// <summary>
        /// Ends the game that the player is in and returns a list the players in the game.
        /// </summary>
        private List<BlackjackPlayer> PlayDealerAndCalculateWinnings(Blackjack game)
        {
            if (AreAllPlayersInSameGameFinished(game) == false)
                throw new Exception($"{nameof(BlackjackManager.PlayDealerAndCalculateWinnings)}: Something went wrong. All players should have finished the game before this method is called, but they have not.");

            game.EndDealerTurn();

            var playerIdsInGame = game.Players.Where(p => !p.IsDealer).Select(p => p.UserId).ToList();

            foreach (ulong playerIdInGame in playerIdsInGame)
            {
                TryGetPlayer(playerIdInGame, out var player);
                player.BaseWinnings = game.GetWinnings(player);
            }

            return game.Players;
        }
    }
}
