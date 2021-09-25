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
        public BlackjackResultType Hit(BlackjackPlayer player)
        {
            var card = _deck.Take();
            player.Cards.Add(card);

            var nonBustResults = player.GetPossibleTotalValues().Where(t => t <= 21); //return value combinations which are not bust

            if (nonBustResults.Count() == 0)
                return BlackjackResultType.Lose;
            else
                return BlackjackResultType.InProgress;
        }

        /// <returns>True if the player won. Otherwise false.</returns>
        public BlackjackResultType Resolve(BlackjackPlayer player, bool isStaying)
        {

            var playerValidTotals = player.GetPossibleTotalValues().Where(t => t <= 21);
            if (!isStaying)
            {
                if (playerValidTotals.Count() == 0)
                    return BlackjackResultType.Lose;
                else
                    return BlackjackResultType.InProgress;
            }

            var dealer = GetDealer();
            var dealerValidTotals = dealer.GetPossibleTotalValues().Where(t => t <= 21);

            if (playerValidTotals.Count() == 0)
                return BlackjackResultType.Lose;

            int playerHighestTotal = playerValidTotals.OrderByDescending(t => t).First(); //we already checked above that player valid total count is not zero so we expect a result here
            int dealerHighestTotal = dealerValidTotals.OrderByDescending(t => t).FirstOrDefault(); //will return 0 if dealer has no valid results

            if (playerHighestTotal == dealerHighestTotal)
            {
                return BlackjackResultType.Draw;
            }
            else if (playerHighestTotal > dealerHighestTotal)
            {
                if (playerHighestTotal == 21)
                    return BlackjackResultType.WinTwentyOne;

                return BlackjackResultType.Win;
            }

            return BlackjackResultType.Lose;
        }

        public enum BlackjackResultType
        {
            InProgress,
            Win,
            Lose,
            Draw,
            WinTwentyOne
        }
    }
}
