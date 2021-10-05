using Common.Helpers;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
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

        private readonly CoinService _coinService;
        private readonly DiscordSocketClient _client;

        public BlackjackManager(DiscordSocketClient client, CoinService coinService)
        {
            _coinService = coinService;
            _client = client;
        }

        /// <returns>True if new game created, false if joining existing game.</returns>
        public async Task CreateOrJoin(ulong playerId, double inputMoney, SocketMessage message)
        {
            if (TryGetPlayer(playerId, out _))
            {
                var joinedGame = GetExisitingGame(playerId); //get the game that the player is in
                if (joinedGame.Started)
                    throw new BadInputException("You are already in a game. Can only play 1 game at once. \nType `.bj hit` or `.bj stay`.");
                else
                    throw new BadInputException("You are already in a game but it hasn't started yet. \nType `.bj start` to start or wait for the game to start automatically.");
            }

            var player = new BlackjackPlayer(playerId, inputMoney);

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
                await message.Channel.SendMessageAsync($"{message.Author.Mention} Blackjack game created and starting in 30 seconds... if anyone else wants to join they need to type `.bj betAmount` to join where 'betAmount' is the amount you want to bet. For example `.bj 1000`.");
            }
            else
            {
                openGame.Join(player);
                await message.Channel.SendMessageAsync($"{message.Author.Mention} Joined existing Blackjack game. It will start soon... If anyone else wants to join they need to type `.bj betAmount` to join where 'betAmount' is the amount you want to bet. For example `.bj 1000`.");
            }

        }

        public async Task Start(ulong playerId, SocketMessage message)
        {
            if (!TryGetPlayer(playerId, out _))
            {
                await message.Channel.SendMessageAsync($"{message.Author.Mention} You have not joined any games FUCK FACE, you can't start someone else's game. Type `.bj betAmount` to join/create a game.");
                return;
            }

            if (!IsGameStarted(playerId))
            {
                Start(playerId);

                //timer on ending the game
                //_ = Task.Delay(TimeSpan.FromSeconds(_secondsToForceEndAfter)).ContinueWith(async t =>
                //{
                //    if(TryGetExisitingGame(playerId, out var game))
                //    {
                //        await EndGameIfAllPlayersFinished(playerId, _client, message, forceEnd: true);
                //    }
                //});

                await message.Channel.SendMessageAsync($"{message.Author.Mention} Blackjack game started. No one else can join this game now.");
                var game = GetExisitingGame(playerId);
                foreach (var gamePlayer in game.Players.Where(p => !p.IsDealer))
                {
                    Hit(gamePlayer.Id);
                    Hit(gamePlayer.Id);
                }
                var dealer = game.GetDealer();
                game.Hit(dealer);
                await message.Channel.SendMessageAsync(GameBlackjackGetFormattedPlayerStanding(playerId, _client));
                await message.Channel.SendMessageAsync("Type `.bj hit` or `.bj stay` to play.");

            }
            else
            {
                await message.Channel.SendMessageAsync($"{message.Author.Mention} The game you are in is already started. Type `.bj hit` or `.bj stay` to play.");
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

        public async Task Hit(ulong playerId, SocketMessage message)
        {
            if (!TryGetPlayer(playerId, out _))
                throw new BadInputException($"You have not joined any games FUCK FACE, you can't stay. Type `.bj betAmount` to join/create a game.");

            if (!IsGameStarted(playerId))
                throw new BadInputException($"Game hasn't started yet. Type `.bj start` to start the game.");

            Hit(playerId);

            if (!await EndGameIfAllPlayersFinished(playerId, _client, message))
            {
                await message.Channel.SendMessageAsync(GameBlackjackGetFormattedPlayerStanding(playerId, _client));
            }
        }

        private void Hit(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            var player = game.GetPlayer(playerId);

            if (player.IsFinishedPlaying) throw new BadInputException("You have already finished playing. Wait for the game to end and the results will be calculated.");

            game.Hit(player);
        }

        public async Task Stay(ulong playerId, SocketMessage message)
        {
            if (!TryGetPlayer(playerId, out _))
                throw new BadInputException($"{message.Author.Mention} You have not joined any games FUCK FACE, you can't stay. Type `.bj betAmount` to join/create a game.");

            if(!IsGameStarted(playerId))
                throw new BadInputException($"{message.Author.Mention} Game hasn't started yet. Type `.bj start` to start the game.");

            Stay(playerId);

            if (!await EndGameIfAllPlayersFinished(playerId, _client, message))
            {
                await message.Channel.SendMessageAsync(GameBlackjackGetFormattedPlayerStanding(playerId, _client));
            }
        }

        private void Stay(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            TryGetPlayer(playerId, out var player);

            if (player.IsFinishedPlaying) throw new BadInputException("You have already finished playing. Wait for the game to end and the results will be calculated.");

            game.Stay(player);
        }

        public bool AreAllPlayersInSameGameFinished(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
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
            var game = Games.FirstOrDefault(g => g.Players.Select(p => p.Id).Contains(playerId));
            if (game == null) throw new BadInputException("You are not in a game yet. Type '.bj \\*betAmount\\*' to create/join an open game.");
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


        private string GameBlackjackGetFormattedPlayerStanding(ulong playerId, DiscordSocketClient client)
        {
            string output = "";
            var game = GetExisitingGame(playerId);

            var dealer = game.GetDealer();
            output += $"**Dealer**: {dealer.GetFormattedCards()}\n\n";

            foreach (var player in game.Players.Where(p => !p.IsDealer))
            {
                output += $"{client.GetUser(player.Id)}: {player.GetFormattedCards()}\n";
            }

            return output;
        }

        /// <returns>True if game ended, false if not.</returns>
        private async Task<bool> EndGameIfAllPlayersFinished(ulong playerId, DiscordSocketClient client, SocketMessage message, bool forceEnd = false)
        {
            var game = GetExisitingGame(playerId);
            if (forceEnd)
            {
                foreach (var player in game.Players)
                {
                    game.Stay(player);
                }
            }

            bool isGameEnded = AreAllPlayersInSameGameFinished(playerId);
            if (isGameEnded)
            {
                string output = "`Blackjack game results:`";

                var playersInGame = PlayDealerAndCalculateWinnings(playerId);
                var dealer = playersInGame.First(p => p.IsDealer);

                output += $"\n**Dealer**: {dealer.GetFormattedCards()}\n";
                foreach (var player in playersInGame.Where(p => !p.IsDealer))
                {
                    CoinAccount account = await _coinService.Get(player.Id, _client.GetUser(player.Id).Username);
                    account.NetWorth += player.Winnings;
                    _coinService.UpdateLocal(account.UserId, account.NetWorth, _client.GetUser(player.Id).Username);

                    output += $"\n{client.GetUser(player.Id).Username}: {player.GetFormattedCards()}" +
                        $"\n\t${FormatHelper.GetCommaNumber(player.BetAmount)} -> ${FormatHelper.GetCommaNumber(player.Winnings)}" +
                        $"\n\t`Networth is now {FormatHelper.GetCommaNumber(account.NetWorth)}`";
                }

                await _coinService.UpdateRemoteWithLocal();

                await message.Channel.SendMessageAsync(output);
            }

            return isGameEnded;
        }


        /// <summary>
        /// Ends the game that the player is in and returns a list the players in the game.
        /// </summary>
        public List<BlackjackPlayer> PlayDealerAndCalculateWinnings(ulong playerId)
        {
            if (AreAllPlayersInSameGameFinished(playerId) == false)
                throw new Exception($"{nameof(BlackjackManager.PlayDealerAndCalculateWinnings)}: Something went wrong. All players should have finished the game before this method is called, but they have not.");

            var game = GetExisitingGame(playerId);
            game.EndDealerTurn();

            var playerIdsInGame = game.Players.Where(p => !p.IsDealer).Select(p => p.Id).ToList();

            foreach (ulong playerIdInGame in playerIdsInGame)
            {
                TryGetPlayer(playerIdInGame, out var player);
                player.Winnings = game.GetWinnings(player);
            }

            Games.Remove(game);
            return game.Players;
        }
    }
}
