using Common.Helpers;
using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

        /// <summary>
        /// One change to the games at a time. Typed commands run on the gateway task and button clicks run off it, so without
        /// this a click and a command could change the same game at once.
        /// </summary>
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

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
        protected virtual Task AnimateGame(TGame game, IEnumerable<TPlayer> players)
        {
            return Task.CompletedTask;
        }

        protected virtual void PostStartActions(TGame game, IEnumerable<TPlayer> players)
        {
        }

        /// <summary>Runs after the game is animated and before the first turn. Can be overridden to post extra messages.</summary>
        protected virtual Task PostStartAsync(TGame game, IEnumerable<TPlayer> players)
        {
            return Task.CompletedTask;
        }

        protected virtual void PreEndActions(TGame game)
        {
        }

        protected virtual void DealerEndGameActions(TGame game)
        {
        }

        /// <summary>Component id prefix for this game's buttons: the base command, like "bj".</summary>
        public string ButtonPrefix => BaseCommand;

        /// <summary>Join… and Start, under a lobby message, and Play again… under the results.</summary>
        public MessageComponent LobbyButtons(string joinLabel = "Join…") => new ComponentBuilder()
            .WithButton(joinLabel, DiscordBot.Models.ComponentId.Build(ButtonPrefix, "join"), ButtonStyle.Success)
            .WithButton("Start", DiscordBot.Models.ComponentId.Build(ButtonPrefix, "start"), ButtonStyle.Primary)
            .Build();

        /// <summary>Buttons for a turn, like Hit and Stay. None by default.</summary>
        public virtual MessageComponent PlayButtons => null;

        /// <summary>Buttons under the results. None by default (Face Off has its own dashboard).</summary>
        protected virtual MessageComponent ResultButtons => null;

        /// <summary>Whether money lost in this game goes into the jackpot. Face Off turns it off: its losers' money is paid to the winner.</summary>
        protected virtual bool LossesFeedJackpot => true;

        /// <summary>How one player came out of a game, for the results.</summary>
        public sealed class PlayerResult
        {
            public TPlayer Player { get; init; }
            /// <summary>Cash after the bet was settled.</summary>
            public double NetWorth { get; init; }
            /// <summary>The first-gamble-of-the-day roll, or null.</summary>
            public string DailyRoll { get; init; }
            /// <summary>A jackpot win, or null.</summary>
            public string JackpotWin { get; init; }
        }

        /// <summary>Someone created or joined the lobby. By default a new message each time.</summary>
        protected virtual Task AnnounceLobby(TGame game, TPlayer player, GameContext context, bool created)
        {
            string how = $"If anyone else wants to join they need to press Join… or type `.{BaseCommand} betAmount` to join where 'betAmount' is the amount you want to bet. For example `.{BaseCommand} 1000`.\nPress Start or type `.{BaseCommand} start` to start the game.";
            return context.SendEmbed($"{GameName} game {(created ? "created" : "joined")}", how, LobbyButtons());
        }

        /// <summary>The game started and is waiting on the players. By default the start message and how to play, in each channel.</summary>
        protected virtual async Task AnnounceStart(TGame game, IEnumerable<TPlayer> players, TPlayer starter)
        {
            var channels = players.Select(p => new Tuple<ulong, ulong>(p.ServerId, p.ChannelId)).Distinct().ToList();
            string startMsg = GetStartMessage(starter);
            if (!string.IsNullOrWhiteSpace(startMsg))
                await channels.SendMessageToEachChannel($"{GameName} game started", startMsg, _client);

            string playCommands = $"{PlayCommands.CombineListToString(" or ", wordPrefix: $".{BaseCommand} ", wordSurrounder: "`")}";
            string howToPlay = PlayButtons == null ? $"Type {playCommands} to play." : $"Press the buttons or type {playCommands} to play.";
            await channels.SendMessageToEachChannel(GameName, howToPlay, _client, PlayButtons);
        }

        /// <summary>The game is over and paid out. By default one results message in each channel.</summary>
        protected virtual async Task AnnounceResults(TGame game, IReadOnlyList<PlayerResult> results)
        {
            string output = "";
            if (game.GameNeedsDealer)
                output += GetDealerEndMessage(game.Players.First(p => p.IsDealer));
            foreach (var result in results)
            {
                var player = result.Player;
                string bonusLine = player.BaseWinnings > 0 ? $"(+ ${FormatHelper.GetCommaNumber(player.BonusWinnings)} bonus)" : string.Empty;
                string networthLine = $"${FormatHelper.GetCommaNumber(player.BetAmount)} -> ${FormatHelper.GetCommaNumber(player.BaseWinnings)}" +
                    $"\t{bonusLine}" +
                    $"\n\t`Networth is now {FormatHelper.GetCommaNumber(result.NetWorth)}`";
                if (result.DailyRoll != null)
                    networthLine += $"\n\t{result.DailyRoll}";
                if (result.JackpotWin != null)
                    networthLine += $"\n\t{result.JackpotWin}";
                output += GetEndMessage(player, networthLine);
            }

            var channels = results.Select(r => new Tuple<ulong, ulong>(r.Player.ServerId, r.Player.ChannelId)).Distinct();
            await channels.SendMessageToEachChannel($"{GameName} game results:", output, _client, ResultButtons);
        }

        /// <summary>Runs one change to the games while holding the lock.</summary>
        protected async Task Locked(Func<Task> change)
        {
            await _gate.WaitAsync();
            try
            {
                await change();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <returns>True if new game created, false if joining existing game.</returns>
        public Task CreateOrJoin(ulong playerId, double inputMoney, GameContext context) =>
            Locked(() => CreateOrJoinLocked(playerId, inputMoney, context));

        private async Task CreateOrJoinLocked(ulong playerId, double inputMoney, GameContext context)
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


            if (context.GuildId == 0)
                throw new BadInputException($"{GameName} is played in a server channel.");
            CoinAccount coinAcc = await _coinService.Get(playerId, context.Username);
            await _betManager.InitiateBet(coinAcc, inputMoney, 10);

            var player = new TPlayer()
            {
                UserId = playerId,
                ChannelId = context.Channel.Id,
                ServerId = context.GuildId,
                BetAmount = inputMoney,
                Username = context.Username,
                CoinAccount = coinAcc
            };

            var openGame = Games.FirstOrDefault(g => g.Started == false);
            if (openGame == null) //if no open games found, create new game
            {
                var newGame = new TGame();
                newGame.Join(player);

                Games.Add(newGame);
                await AnnounceLobby(newGame, player, context, created: true);
            }
            else
            {
                openGame.Join(player);
                await AnnounceLobby(openGame, player, context, created: false);
            }

        }

        public Task Start(ulong playerId, GameContext context) => Locked(() => StartLocked(playerId, context));

        private async Task StartLocked(ulong playerId, GameContext context)
        {
            if (!TryGetPlayer(playerId, out var player))
            {
                throw new NotInGameException();
            }

            if (!IsGameStarted(playerId))
            {
                Guid gameId = Start(playerId);

                //timer on ending the game
                _ = Task.Delay(TimeSpan.FromSeconds(SecondsToForceEndAfter)).ContinueWith(t => Locked(async () =>
                {
                    if (TryGetExisitingGame(gameId, out var game))
                    {
                        game.Players.ForEach(p => p.IsFinishedPlaying = true);
                        await EndGame(game);
                    }
                }));

                var game = GetExisitingGame(playerId);
                var players = game.Players.Where(p => !p.IsDealer);

                //normally nothing, but can be overridden in child classes
                PostStartActions(game, players);
                await AnimateGame(game, players);
                await PostStartAsync(game, players);

                if (await EndGameIfAllPlayersFinished(playerId))
                    return;

                await AnnounceStart(game, players, player);
            }
            else
            {
                string playCommands = $"{PlayCommands.CombineListToString(" or ", wordPrefix: $".{BaseCommand} ", wordSurrounder: "`")}";
                await context.SendEmbed("Error", $"The game you are in is already started. Type {playCommands} to play.", PlayButtons);
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
        protected async Task<bool> EndGameIfAllPlayersFinished(ulong playerId)
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

            try
            {
                if (game.GameNeedsDealer)
                    DealerEndGameActions(game);

                CalculateWinnings(game);
                Games.Remove(game);

                var players = game.Players.Where(p => !p.IsDealer).ToList();
                await _betManager.ResolveBet(players, LossesFeedJackpot);
                var results = new List<PlayerResult>();
                foreach (var player in players)
                {
                    CoinAccount coinAccount = await _coinService.Get(player.UserId, player.Username);
                    results.Add(new PlayerResult
                    {
                        Player = player,
                        NetWorth = coinAccount.NetWorth,
                        DailyRoll = _betManager.TakeDailyRoll(player.UserId)?.ToString(),
                        JackpotWin = _betManager.TakeJackpotWin(player.UserId),
                    });
                }
                await AnnounceResults(game, results);
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
