using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using static DiscordBot.Games.Blackjack;

namespace DiscordBot.Games
{
    public class BlackjackManager //should be singleton
    {
        public List<Blackjack> Games = new List<Blackjack>();
        public Dictionary<ulong, Guid> PlayerGameMappings = new Dictionary<ulong, Guid>(); //1 to 1 mapping between player and game, i.e. a player cannot be in more than 1 game at once


        /// <returns>True if new game created, false if joining existing game.</returns>
        public Blackjack CreateOrJoin(ulong playerId)
        {
            if (PlayerGameMappings.ContainsKey(playerId))
                throw new BadInputException("You are already in a game. Can only play 1 game at once. \nType '.bj hit' or '.bj stay'.");

            var openGame = Games.FirstOrDefault(g => g.Started == false);
            if (openGame == null) //if no open games found, create new game
            {
                var newGame = new Blackjack(playerId);
                Games.Add(newGame);
                PlayerGameMappings.Add(playerId, newGame.Guid);
                return newGame;
            }
            else
            {
                openGame.Join(new BlackjackPlayer() { Id = playerId });
                PlayerGameMappings.Add(playerId, openGame.Guid);
                return openGame;
            }

        }

        public void Start(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            game.Started = true;
        }

        public void End(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            Games.Remove(game);
            var playerIdsToRemove = PlayerGameMappings.Where(m => m.Value == game.Guid).Select(m => m.Key).ToList(); //tolist to close reader so we can alter dictionary

            foreach (ulong playerIdToRemove in playerIdsToRemove)
            {
                PlayerGameMappings.Remove(playerIdToRemove);
            }
        }

        public bool DoesGameExist(ulong playerId, out Blackjack game)
        {
            game = Games.FirstOrDefault(g => g.Players.Contains(g.GetPlayer(playerId)));
            if (game == null) return false;
            return true;
        }

        /// <returns>True if still in the game, false is player is bust.</returns>
        public bool Hit(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            var player = game.GetPlayer(playerId);
            var result = game.Hit(player);
            if (result == Blackjack.BlackjackResultType.InProgress)
                return true;
            else
                return false;
        }

        public BlackjackResultType Stay(ulong playerId)
        {
            var game = GetExisitingGame(playerId);
            var player = game.GetPlayer(playerId);
            var result = game.Resolve(player, true);
            return result;
        }

        private Blackjack GetExisitingGame(ulong playerId)
        {
            var game = Games.FirstOrDefault(g => g.Players.Contains(g.GetPlayer(playerId)));
            if (game == null) throw new BadInputException("You are not in a game yet. Type '.bj' to create/join an open game.");
            return game;

        }


    }
}
