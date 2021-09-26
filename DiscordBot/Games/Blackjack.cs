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
        public Blackjack(BlackjackPlayer startingPlayer)
        {
            Guid = Guid.NewGuid();
            _deck = new CardDeck(6);
            var dealer = new BlackjackPlayer() { IsDealer = true };
            Create(startingPlayer, dealer);
        }


        /// <returns>True if player is still in the game. False if they are bust.</returns>
        public bool Hit(BlackjackPlayer player)
        {
            var card = _deck.Take();
            player.Cards.Add(card);
            var playerValidTotals = player.GetPossibleTotalValues().Where(t => t <= 21);

            if (playerValidTotals.Count() == 0)
            {
                player.IsFinishedPlaying = true;
                return false;
            }

            return true;
        }

        public void Stay(BlackjackPlayer player)
        {
            player.IsFinishedPlaying = true;
        }

        public double GetWinnings(BlackjackPlayer player)
        {
            BlackjackResultType result = Resolve(player);

            switch (result)
            {
                case BlackjackResultType.Draw:
                    return player.BetAmount;
                case BlackjackResultType.Win:
                    return player.BetAmount * 2;
                case BlackjackResultType.WinTwentyOne:
                    return player.BetAmount * 3;
                default:
                    return 0;
            }


        }

        private BlackjackResultType Resolve(BlackjackPlayer player)
        {
            var playerValidTotals = player.GetPossibleTotalValues().Where(t => t <= 21);
            if (playerValidTotals.Count() == 0)
                return BlackjackResultType.Lose;

            var dealer = GetDealer();
            var dealerValidTotals = dealer.GetPossibleTotalValues().Where(t => t <= 21);

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
            Win,
            Lose,
            Draw,
            WinTwentyOne
        }
    }
}
