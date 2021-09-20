using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games
{
    public class BlackjackManager //should be singleton
    {
        public List<Blackjack> Games = new List<Blackjack>();
        public Dictionary<ulong, Guid> PlayerGameMappings = new Dictionary<ulong, Guid>(); //1 to 1 mapping between player and game, i.e. a player cannot be in more than 1 game at once

        public void JoinOrCreate(ulong playerId)
        {
            if (PlayerGameMappings.ContainsKey(playerId))
                throw new BadInputException("You are already in a game. Can only play 1 game at once.");

            var openGame = Games.FirstOrDefault(g => g.Started == false);
            if (openGame == null) //if no open games found, create new game
            {
                var newGame = new Blackjack(playerId);
                Games.Add(newGame);
                PlayerGameMappings.Add(playerId, newGame.Guid);
            }
            else
            {
                openGame.Join(new BlackjackPlayer() { Id = playerId});
                PlayerGameMappings.Add(playerId, openGame.Guid);
            }
        }

        public void End(Guid guid)
        {
            Games.RemoveAll(g => g.Guid == guid);
            var playerIdsToRemove = PlayerGameMappings.Where(m => m.Value == guid).Select(m => m.Key).ToList(); //tolist to close reader so we can alter dictionary
            
            foreach(ulong playerId in playerIdsToRemove)
            {
                PlayerGameMappings.Remove(playerId);
            }
        }
    }
}
