using Common.Helpers;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    //should be singleton
    public abstract class BaseMultiplayerManager<TGame, TPlayer>
        where TPlayer : BasePlayer, new()
        where TGame : BaseMultiplayerGame<TPlayer>, new()
    {
        public abstract string GameName { get; }
        public abstract string BaseCommand { get; }
        public abstract string[] PlayCommands { get; }

        public List<TGame> Games = new List<TGame>();
        protected int SecondsToForceStartAfter = 30;
        protected int SecondsToForceEndAfter = 90;

        protected readonly BetManager _betManager;
        protected readonly DiscordSocketClient _client;
        protected readonly CoinService _coinService;

        public BaseMultiplayerManager(DiscordSocketClient client, BetManager betManager, CoinService coinService)
        {
            _betManager = betManager;
            _client = client;
            _coinService = coinService;
        }

        protected abstract string GetStartMessage(TPlayer player);
        protected abstract string GetEndMessage(TPlayer player, string networthMessage);
        protected virtual string GetDealerEndMessage(TPlayer dealer)
        {
            return "";
        }

        protected virtual void PostStartActions(TGame game, IEnumerable<TPlayer> players)
        {
        }

        protected virtual void PreEndActions(TGame game)
        {
        }

        protected virtual void DealerEndGameActions(TGame game)
        {
        }

        /// <returns>True if new game created, false if joining existing game.</returns>
        public async Task CreateOrJoin(ulong playerId, double inputMoney, SocketMessage message)
        {
            //try to get the player
            if (TryGetPlayer(playerId, out _))
            {
                var joinedGame = GetExisitingGame(playerId); //get the game that the player is in
                if (joinedGame.Started)
                {
                    string playCommands = $"{PlayCommands.CombineListToString(" or ", wordPrefix: $".{BaseCommand} ", wordSurrounder: "`")}";
                    throw new BadInputException($"You are already in a game. Can only play 1 game at once. \nType {playCommands}.");
                }
                else
                    throw new BadInputException($"You are already in a game but it hasn't started yet. \nType `.{BaseCommand} start` to start or wait for the game to start automatically.");
            }


            CoinAccount coinAcc = await _coinService.Get(playerId, message.Author.Username);
            await _betManager.InitiateBet(coinAcc, inputMoney, 10);

            var channel = message.Channel as SocketGuildChannel;
            var guildId = channel.Guild.Id;
            var player = new TPlayer()
            {
                UserId = playerId,
                ChannelId = message.Channel.Id,
                ServerId = guildId,
                BetAmount = inputMoney,
                Username = message.Author.Username,
                CoinAccount = coinAcc
            };

            var openGame = Games.FirstOrDefault(g => g.Started == false);
            if (openGame == null) //if no open games found, create new game
            {
                var newGame = new TGame();
                newGame.Join(player);

                Games.Add(newGame);

                //timer on starting the game
                //_ = Task.Delay(TimeSpan.FromSeconds(_secondsToForceStartAfter)).ContinueWith(t =>
                //{
                //    if (!newGame.Started)
                //        Start(playerId);
                //});
                await message.SendRichEmbedMessage($"{GameName} game created", $"If anyone else wants to join they need to type `.{BaseCommand} betAmount` to join where 'betAmount' is the amount you want to bet. For example `.{BaseCommand} 1000`.\nType `.{BaseCommand} start` to start the game.");
            }
            else
            {
                openGame.Join(player);
                await message.SendRichEmbedMessage($"{GameName} game joined", $"If anyone else wants to join they need to type `.{BaseCommand} betAmount` to join where 'betAmount' is the amount you want to bet. For example `.{BaseCommand} 1000`.\nType `.{BaseCommand} start` to start the game.");
            }

        }

        public async Task Start(ulong playerId, SocketMessage message)
        {
            if (!TryGetPlayer(playerId, out var player))
            {
                throw new NotInGameException();
            }

            if (!IsGameStarted(playerId))
            {
                Guid gameId = Start(playerId);

                //timer on ending the game
                _ = Task.Delay(TimeSpan.FromSeconds(SecondsToForceEndAfter)).ContinueWith(async t =>
                {
                    if (TryGetExisitingGame(gameId, out var game))
                    {
                        game.Players.ForEach(p => p.IsFinishedPlaying = true);
                        await EndGame(game);
                    }
                });

                var game = GetExisitingGame(playerId);
                var players = game.Players.Where(p => !p.IsDealer);

                var serverChannelMappings = players.Select(p => { return new Tuple<ulong, ulong>(p.ServerId, p.ChannelId); });
                var distinctServerChannelMappings = serverChannelMappings.Distinct();

                //normally nothing, but can be overridden in child classes
                PostStartActions(game, players);

                if (await EndGameIfAllPlayersFinished(playerId, message))
                    return;

                string startMsg = GetStartMessage(player);

                if (!string.IsNullOrWhiteSpace(startMsg))
                    await distinctServerChannelMappings.SendMessageToEachChannel($"{GameName} game started", startMsg, _client);

                string playCommands = $"{PlayCommands.CombineListToString(" or ", wordPrefix: $".{BaseCommand} ", wordSurrounder: "`")}";
                await distinctServerChannelMappings.SendMessageToEachChannel(GameName, $"Type {playCommands} to play.", _client);
            }
            else
            {
                string playCommands = $"{PlayCommands.CombineListToString(" or ", wordPrefix: $".{BaseCommand} ", wordSurrounder: "`")}";
                await message.SendRichEmbedMessage("Error", $"The game you are in is already started. Type `.bj hit` or `.bj stay` to play.");
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

        public bool AreAllPlayersInSameGameFinished(TGame game)
        {
            if (game.Players.Where(p => !p.IsDealer).Any(p => !p.IsFinishedPlaying))
                return false;
            else
                return true;
        }

        public bool TryGetPlayer(ulong playerId, out TPlayer player)
        {
            player = default(TPlayer);
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

        public TGame GetExisitingGame(ulong playerId)
        {
            var game = Games.FirstOrDefault(g => g.Players.Select(p => p.UserId).Contains(playerId));
            if (game == null) throw new BadInputException("You are not in a game yet. Type '.commandName \\*betAmount\\*' to create/join an open game.");
            return game;
        }

        public TGame GetExisitingGame(Guid gameGuid)
        {
            var game = Games.FirstOrDefault(g => g.GameGuid == gameGuid);
            if (game == null) throw new BadInputException("Game does not exist.");
            return game;
        }

        public bool TryGetExisitingGame(Guid gameGuid, out TGame game)
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



        /// <returns>True if game ended, false if not.</returns>
        protected async Task<bool> EndGameIfAllPlayersFinished(ulong playerId, SocketMessage message)
        {
            var game = GetExisitingGame(playerId);

            if (!AreAllPlayersInSameGameFinished(game))
                return false;

            await EndGame(game);
            return true;
        }

        private async Task EndGame(TGame game)
        {
            if (AreAllPlayersInSameGameFinished(game) == false)
                throw new Exception($"{nameof(BaseMultiplayerManager<TGame, TPlayer>.CalculateWinnings)}: Something went wrong. All players should have finished the game before this method is called, but they have not.");

            PreEndActions(game);

            string title = $"{GameName} game results:";
            try
            {
                string output = "";
                if (game.GameNeedsDealer)
                {
                    DealerEndGameActions(game);
                    var dealer = game.Players.First(p => p.IsDealer);
                    output += GetDealerEndMessage(dealer);
                }

                CalculateWinnings(game);
                Games.Remove(game);

                var players = game.Players.Where(p => !p.IsDealer);
                await _betManager.ResolveBet(players);
                foreach (var player in players)
                {
                    CoinAccount coinAccount = await _coinService.Get(player.UserId, player.Username);
                    string bonusLine = player.BaseWinnings > 0 ? $"(+ ${FormatHelper.GetCommaNumber(player.BonusWinnings)} bonus)" : string.Empty;
                    string networthLine = $"${FormatHelper.GetCommaNumber(player.BetAmount)} -> ${FormatHelper.GetCommaNumber(player.BaseWinnings)}" +
                        $"\t{bonusLine}" +
                        $"\n\t`Networth is now {FormatHelper.GetCommaNumber(coinAccount.NetWorth)}`";

                    output += GetEndMessage(player, networthLine);
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
        /// Calculates the winnings for each player in the game and sets their BaseWinnings property
        /// </summary>
        private void CalculateWinnings(TGame game)
        {
            foreach (var player in game.Players.Where(p => !p.IsDealer))
            {
                player.BaseWinnings = game.GetWinnings(player);
            }
        }
    }
}
