﻿using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games
{
    public abstract class BaseMultiplayerGame<TPlayer> where TPlayer : IPlayer, new()
    {
        public Guid GameGuid { get; } = Guid.NewGuid();
        public List<TPlayer> Players { get; protected set; } = new List<TPlayer>();
        public bool Started = false;
        public bool Ended = false;
        public bool GameNeedsDealer = false;
        public int MinimumRequiredPlayers = 1;

        public BaseMultiplayerGame()
        {
        }

        public abstract double GetWinnings(TPlayer player);

        public void Join(params TPlayer[] players)
        {
            if (GameNeedsDealer && !Players.Any(p => p.IsDealer))
                Players.Add(new TPlayer() { IsDealer = true });

            Players.AddRange(players);
        }
        public Guid Start()
        {
            if (Players.Count() < MinimumRequiredPlayers) throw new BadInputException($"Not enough players in the game {GameGuid}, it cannot start! Need at least {MinimumRequiredPlayers} players.");

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
