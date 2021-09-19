using DiscordBot.Games.Models;
using System.Collections.Generic;

namespace DiscordBot.Games
{
    public abstract class BaseMultiplayerGame<TPlayer>
    {
        protected List<TPlayer> _players;

        public void Create(params TPlayer[] players)
        {
            _players = new List<TPlayer>();
            _players.AddRange(players);
        }
        public void Join(TPlayer player)
        {
            _players.Add(player);
        }
    }
}
