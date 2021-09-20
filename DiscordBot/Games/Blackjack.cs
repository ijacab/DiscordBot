using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace DiscordBot.Games
{
    public class Blackjack : BaseMultiplayerGame<BlackjackPlayer>
    {
        public readonly Guid Guid;
        private readonly CardDeck _deck;
        public Blackjack(ulong startingPlayerId)
        {
            Guid = Guid.NewGuid();
            _deck = new CardDeck(6);
            var dealer = new BlackjackPlayer() { IsDealer = true };
            var player = new BlackjackPlayer() { Id = startingPlayerId };
            Create(player, dealer);
        }


        /// <returns>The list of possible total values not over 21. If the returned list is empty it means player has gone over 21 in value combinations.</returns>
        public IEnumerable<int> Hit(ulong playerId)
        {
            var card = _deck.Take();
            var player = _players.First(p => p.Id == playerId);
            player.Cards.Add(card);

            return player.GetPossibleTotalValues().Where(t => t <= 21); //return value combinations which are not bust
        }

        /// <returns>True if the player won. Otherwise false.</returns>
        public BlackjackWinType Resolve(ulong playerId)
        {
            var player = _players.First(p => p.Id == playerId);
            var dealer = _players.First(p => p.IsDealer);

            var playerValidTotals = player.GetPossibleTotalValues().Where(t => t <= 21);
            var dealerValidTotals = player.GetPossibleTotalValues().Where(t => t <= 21);

            if (playerValidTotals.Count() == 0)
                return BlackjackWinType.Lose;

            int playerHighestTotal = playerValidTotals.OrderByDescending(t => t).First();
            int dealerHighestTotal = dealerValidTotals.OrderByDescending(t => t).First();

            if (playerHighestTotal == dealerHighestTotal)
            {
                return BlackjackWinType.Draw;
            }
            else if (playerHighestTotal > dealerHighestTotal)
            {
                if (playerHighestTotal == 21)
                    return BlackjackWinType.WinTwentyOne;

                return BlackjackWinType.Win;
            }

            return BlackjackWinType.Lose;

        }

        public enum BlackjackWinType
        {
            Win,
            Lose,
            Draw,
            WinTwentyOne
        }

    }
}
