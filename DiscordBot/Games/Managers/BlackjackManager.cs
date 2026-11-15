using Common.Helpers;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Games.Managers
{
    public class BlackjackManager : BaseMultiplayerManager<Blackjack, BlackjackPlayer> //should be singleton
    {
        public override string GameName => "Blackjack";
        public override string BaseCommand => "bj";
        public override string[] PlayCommands => new string[] { "hit", "stay" };

        public BlackjackManager(DiscordSocketClient client, BetManager betManager, CoinService coinService)
            : base(client, betManager, coinService)
        {
        }

        protected override MessageComponent ResultButtons => LobbyButtons("Play again…");

        /// <summary>
        /// Each game's table: one message per channel the players joined from, edited in place from the lobby to the results
        /// (<see cref="BlackjackView"/>). Only touched under the base manager's lock.
        /// </summary>
        private sealed class Table
        {
            public Dictionary<ulong, IMessageChannel> Channels { get; } = new Dictionary<ulong, IMessageChannel>();
            public Dictionary<ulong, IUserMessage> Messages { get; } = new Dictionary<ulong, IUserMessage>();
        }

        private readonly Dictionary<Guid, Table> _tables = new Dictionary<Guid, Table>();

        protected override Task AnnounceLobby(Blackjack game, BlackjackPlayer player, GameContext context, bool created)
        {
            if (!_tables.TryGetValue(game.GameGuid, out var table))
                _tables[game.GameGuid] = table = new Table();
            table.Channels[context.Channel.Id] = context.Channel;
            return Publish(game, BlackjackView.Lobby(game), LobbyButtons());
        }

        protected override Task AnnounceStart(Blackjack game, IEnumerable<BlackjackPlayer> players, BlackjackPlayer starter) =>
            Publish(game, BlackjackView.InPlay(game), PlayButtons);

        protected override async Task AnnounceResults(Blackjack game, IReadOnlyList<PlayerResult> results)
        {
            var lines = results.Select(r => new BlackjackView.ResultLine
            {
                Player = r.Player,
                Result = game.ResultOf(r.Player),
                NetWorth = r.NetWorth,
                Notes = new[] { r.DailyRoll, r.JackpotWin }.Where(n => n != null).ToList(),
            });
            await Publish(game, BlackjackView.Results(game, lines), ResultButtons);
            _tables.Remove(game.GameGuid);
        }

        /// <summary>Shows the table in every channel the game is in: edits the message there, or posts one if it's missing.</summary>
        private async Task Publish(Blackjack game, Embed embed, MessageComponent components)
        {
            if (!_tables.TryGetValue(game.GameGuid, out var table))
                return;
            foreach (var (channelId, channel) in table.Channels)
            {
                try
                {
                    if (table.Messages.TryGetValue(channelId, out var message))
                    {
                        try
                        {
                            await message.ModifyAsync(m => { m.Embed = embed; m.Components = components ?? new ComponentBuilder().Build(); });
                            continue;
                        }
                        catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.UnknownMessage)
                        {
                            //someone deleted the table, so post it again
                        }
                    }
                    table.Messages[channelId] = await channel.SendMessageAsync(embed: embed, components: components);
                }
                catch (Exception)
                {
                    //the channel may have gone; the other channels still get the table
                }
            }
        }

        public override MessageComponent PlayButtons => new ComponentBuilder()
            .WithButton("Hit", DiscordBot.Models.ComponentId.Build(ButtonPrefix, "hit"), ButtonStyle.Primary)
            .WithButton("Stay", DiscordBot.Models.ComponentId.Build(ButtonPrefix, "stay"), ButtonStyle.Secondary)
            .Build();

        protected override void PostStartActions(Blackjack game, IEnumerable<BlackjackPlayer> players)
        {
            foreach (var gamePlayer in players)
            {
                Hit(gamePlayer.UserId);
                Hit(gamePlayer.UserId);
            }
            var dealer = game.GetDealer();
            game.Hit(dealer);
        }

        protected override void PreEndActions(Blackjack game)
        {
            foreach (var player in game.Players)
            {
                game.Stay(player);
            }
        }
        
        protected override void DealerEndGameActions(Blackjack game)
        {
            game.EndDealerTurn();
        }

        protected override string GetStartMessage(BlackjackPlayer player)
        {
            return GameBlackjackGetFormattedPlayerStanding(player);
        }

        protected override string GetEndMessage(BlackjackPlayer player, string networthMessage)
        {
            return $"\n{player.Username}: {player.GetFormattedCards()}\n\t{networthMessage}";
        }

        protected override string GetDealerEndMessage(BlackjackPlayer dealer)
        {
            return $"\n**Dealer**: {dealer.GetFormattedCards()}\n";
        }

        public Task Hit(ulong playerId, GameContext context) => Locked(() => HitLocked(playerId, context));

        private async Task HitLocked(ulong playerId, GameContext context)
        {
            if (!TryGetPlayer(playerId, out var player))
                throw new BadInputException($"You have not joined any games FUCK FACE, you can't stay. Type `.bj betAmount` to join/create a game.");

            if (!IsGameStarted(playerId))
                throw new BadInputException($"Game hasn't started yet. Type `.bj start` to start the game.");

            Hit(playerId);

            if (!await EndGameIfAllPlayersFinished(playerId))
            {
                var game = GetExisitingGame(playerId);
                await Publish(game, BlackjackView.InPlay(game), PlayButtons);
            }
        }

        private void Hit(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            var player = game.GetPlayer(playerId);

            if (player.IsFinishedPlaying) throw new BadInputException("You have already finished playing. Wait for the game to end and the results will be calculated.");

            game.Hit(player);
        }

        public Task Stay(ulong playerId, GameContext context) => Locked(() => StayLocked(playerId, context));

        private async Task StayLocked(ulong playerId, GameContext context)
        {
            if (!TryGetPlayer(playerId, out var player))
                throw new BadInputException($"You have not joined any games FUCK FACE, you can't stay. Type `.bj betAmount` to join/create a game.");

            if (!IsGameStarted(playerId))
                throw new BadInputException($"Game hasn't started yet. Type `.bj start` to start the game.");

            Stay(playerId);

            if (!await EndGameIfAllPlayersFinished(playerId))
            {
                var game = GetExisitingGame(playerId);
                await Publish(game, BlackjackView.InPlay(game), PlayButtons);
            }
        }

        private void Stay(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            TryGetPlayer(playerId, out var player);

            if (player.IsFinishedPlaying) throw new BadInputException("You have already finished playing. Wait for the game to end and the results will be calculated.");

            game.Stay(player);
        }

        private string GameBlackjackGetFormattedPlayerStanding(BlackjackPlayer player)
        {
            string output = "";
            var game = GetExisitingGame(player.UserId);

            var dealer = game.GetDealer();
            output += $"**Dealer**: {dealer.GetFormattedCards()}\n\n";

            foreach (var bjPlayers in game.Players.Where(p => !p.IsDealer))
            {
                output += $"{bjPlayers.Username}: {bjPlayers.GetFormattedCards()}\n";
            }

            return output;
        }


    }
}
