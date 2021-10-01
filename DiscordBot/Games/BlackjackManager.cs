using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Games.Blackjack;

namespace DiscordBot.Games
{
    public class BlackjackManager //should be singleton
    {
        public List<Blackjack> Games = new List<Blackjack>();

        /// <returns>True if new game created, false if joining existing game.</returns>
        public bool CreateOrJoin(ulong playerId, double inputMoney)
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
                return true;
            }
            else
            {
                openGame.Join(player);
                return false;
            }

        }

        /// <summary>
        /// Starts game and returns the game Guid.
        /// </summary>
        public Guid Start(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            return game.Start();
        }

        public bool IsGameStarted(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            return game.Started;
        }

        /// <summary>
        /// Ends the game that the player is in and returns a list the players in the game.
        /// </summary>
        public List<BlackjackPlayer> End(ulong playerId)
        {
            if (AreAllPlayersInSameGameFinished(playerId) == false)
                throw new Exception($"{nameof(BlackjackManager.End)}: Something went wrong. All players should have finished the game before this method is called, but they have not.");

            var game = GetExisitingGame(playerId);
            game.PlayDealer();

            var playerIdsInGame = game.Players.Where(p => !p.IsDealer).Select(p => p.Id).ToList();

            foreach (ulong playerIdInGame in playerIdsInGame)
            {
                TryGetPlayer(playerIdInGame, out var player);
                player.Winnings = game.GetWinnings(player);
            }

            Games.Remove(game);
            return game.Players;
        }


        public void Hit(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            var player = game.GetPlayer(playerId);

            if (player.IsFinishedPlaying) throw new BadInputException("You have already finished playing. Wait for the game to end and the results will be calculated.");

            game.Hit(player);
        }

        public void Stay(ulong playerId)
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
    }
}
