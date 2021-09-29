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
            if (IsPlayerInGame(playerId))
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

        public void Start(ulong playerId, int secondsToPlay = 90)
        {
            var game = GetExisitingGame(playerId);
            game.Started = true;

            _ = Task.Delay(TimeSpan.FromSeconds(secondsToPlay))
                .ContinueWith(t =>
            {
                foreach(BlackjackPlayer player in game.Players)
                {
                    game.Stay(player);
                }
            }
            );
        }

        public bool IsGameStarted(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            return game.Started;
        }

        /// <summary>
        /// Ends the game that the player is in and returns a list of mappings of each player in that game and their winnings.
        /// </summary>
        public List<Tuple<ulong,double>> End(ulong playerId)
        {
            if (AreAllPlayersInSameGameFinished(playerId) == false)
                throw new Exception($"{nameof(BlackjackManager.End)}: Something went wrong. All players should have finished the game before this method is called, but they have not.");

            var game = GetExisitingGame(playerId);
            game.PlayDealer();

            Games.Remove(game);
            var playerIdsInGame = game.Players.Select(p => p.Id).ToList();
            
            var playerWinnings = new List<Tuple<ulong, double>>();
            foreach (ulong playerIdInGame in playerIdsInGame)
            {
                var player = GetPlayer(playerIdInGame);
                double winnings = game.GetWinnings(player);
                playerWinnings.Add(new Tuple<ulong, double>(playerIdInGame, winnings));
            }

            return playerWinnings;
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
            var player = GetPlayer(playerId);
            
            if(player.IsFinishedPlaying) throw new BadInputException("You have already finished playing. Wait for the game to end and the results will be calculated.");

            game.Stay(player);
        }

        public bool AreAllPlayersInSameGameFinished(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            if (game.Players.Where(p=>!p.IsDealer).Any(p => !p.IsFinishedPlaying))
                return false;
            else
                return true;
        }

        public bool IsPlayerInGame(ulong playerId)
        {
            var game = Games.FirstOrDefault(g => g.Players.Contains(g.GetPlayer(playerId)));
            if (game == null) return false;
            return true;
        }

        public BlackjackPlayer GetPlayer(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            return game.GetPlayer(playerId);
        }

        public Blackjack GetExisitingGame(ulong playerId)
        {
            var game = Games.FirstOrDefault(g => g.Players.Contains(g.GetPlayer(playerId)));
            if (game == null) throw new BadInputException("You are not in a game yet. Type '.bj \\*betAmount\\*' to create/join an open game.");
            return game;
        }
    }
}
