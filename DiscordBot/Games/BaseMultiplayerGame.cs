using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games
{
    public abstract class BaseMultiplayerGame<TPlayer> where TPlayer : IPlayer, new()
    {
        public Guid GameGuid { get; } = Guid.NewGuid();
        public List<TPlayer> Players { get; protected set; }
        public bool Started = false;
        public bool GameNeedsDealer = false;

        public BaseMultiplayerGame()
        {
        }

        public abstract double GetWinnings(TPlayer player);

        public void Join(params TPlayer[] players)
        {
            if (Players == null)
                Players = new List<TPlayer>();

            if (GameNeedsDealer && !Players.Any(p => p.IsDealer))
                Players.Add(new TPlayer() { IsDealer = true });

            Players.AddRange(players);
        }
        public Guid Start()
        {
            if (!Players.Any()) throw new Exception($"No players in the game {nameof(BaseMultiplayerGame<TPlayer>)}:${GameGuid}, it cannot start!");

            Started = true;
            return GameGuid;
        }
        public TPlayer GetPlayer(ulong playerId)
        {
            return Players.First(p => p.UserId == playerId);
        }
        public TPlayer GetDealer()
        {
            return Players.First(p => p.IsDealer);
        }


    }
}
