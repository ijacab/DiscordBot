using Common.Helpers;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Services;
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

        public async Task Hit(ulong playerId, SocketMessage message)
        {
            if (!TryGetPlayer(playerId, out var player))
                throw new BadInputException($"You have not joined any games FUCK FACE, you can't stay. Type `.bj betAmount` to join/create a game.");

            if (!IsGameStarted(playerId))
                throw new BadInputException($"Game hasn't started yet. Type `.bj start` to start the game.");

            Hit(playerId);

            if (!await EndGameIfAllPlayersFinished(playerId, message))
            {
                await message.SendRichEmbedMessage("Player standings", GameBlackjackGetFormattedPlayerStanding(player));
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
            if (!TryGetPlayer(playerId, out var player))
                throw new BadInputException($"You have not joined any games FUCK FACE, you can't stay. Type `.bj betAmount` to join/create a game.");

            if (!IsGameStarted(playerId))
                throw new BadInputException($"Game hasn't started yet. Type `.bj start` to start the game.");

            Stay(playerId);

            if (!await EndGameIfAllPlayersFinished(playerId, message))
            {
                await message.SendRichEmbedMessage("Player standings", GameBlackjackGetFormattedPlayerStanding(player));
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
