using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games
{
    public abstract class BaseMultiplayerGame<TPlayer> where TPlayer : IPlayer
    {
        public Guid GameGuid { get; set; } = Guid.NewGuid();
        public List<TPlayer> Players { get; protected set; }
        public bool Started = false;

        public void Create(params TPlayer[] players)
        {
            Players = new List<TPlayer>();
            Players.AddRange(players);
        }
        public void Join(TPlayer player)
        {
            Players.Add(player);
        }
        public Guid Start()
        {
            Started = true;
            return GameGuid;
        }
        public TPlayer GetPlayer(ulong playerId)
        {
            return Players.First(p => p.Id == playerId);
        }
        public TPlayer GetDealer()
        {
            return Players.First(p => p.IsDealer);
        }
    }
}
