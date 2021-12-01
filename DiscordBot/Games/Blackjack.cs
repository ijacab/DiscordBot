using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace DiscordBot.Games
{
    public class Blackjack : BaseMultiplayerGame<BlackjackPlayer>
    {
        private readonly CardDeck _deck;
        public Blackjack(BlackjackPlayer startingPlayer)
        {
            _deck = new CardDeck(6);
            var dealer = new BlackjackPlayer() { IsDealer = true };
            Create(startingPlayer, dealer);
        }


        public void Hit(BlackjackPlayer player)
        {
            var card = _deck.Take();
            player.Cards.Add(card);
            var playerValidTotals = player.GetPossibleTotalValues();

            if (playerValidTotals.Count() == 0 || player.Cards.Count >= 5 || playerValidTotals.OrderByDescending(t => t).First() == 21)
                player.IsFinishedPlaying = true;
        }

        public void Stay(BlackjackPlayer player)
        {
            player.IsFinishedPlaying = true;
        }

        public void EndDealerTurn()
        {
            /*When the dealer has served every player, the dealers face - down card is turned up. 
             If the total is 17 or more, it must stand.
             If the total is 16 or under, they must take a card.
             The dealer must continue to take cards until the total is 17 or more, at which point the dealer must stand.
             If the dealer has an ace, and counting it as 11 would bring the total to 17 or more(but not over 21), the dealer must count the ace as 11 and stand. 
             The dealer's decisions, then, are automatic on all plays, whereas the player always has the option of taking one or more cards.
             */

            var dealer = GetDealer();

            var totals = dealer.GetPossibleTotalValues();
            int highestValidTotal = totals.OrderByDescending(v => v).FirstOrDefault();

            while (highestValidTotal <= 16) //get the highest possible total and check if it is 16 or under, if so dealer needs to hit
            {
                Hit(dealer);
                totals = dealer.GetPossibleTotalValues();

                if (totals.Any())
                    highestValidTotal = dealer.GetPossibleTotalValues().OrderByDescending(v => v).First();
                else
                    break;
            }
            Stay(dealer);
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
                    return player.BetAmount * 2.5;
                case BlackjackResultType.WinFiveCard:
                    return player.BetAmount * 3.5;
                case BlackjackResultType.WinFiveCardTwentyOne:
                    return player.BetAmount * 20;
                case BlackjackResultType.WinTwentyToTwentyOne:
                    return player.BetAmount * 30;
                case BlackjackResultType.WinFiveCardTwentyToTwentyOne:
                    return player.BetAmount * 600;
                default:
                    return 0;
            }
        }

        private BlackjackResultType Resolve(BlackjackPlayer player)
        {
            var playerValidTotals = player.GetPossibleTotalValues();

            if (!playerValidTotals.Any())
                return BlackjackResultType.Lose;

            var dealer = GetDealer();
            var dealerValidTotals = dealer.GetPossibleTotalValues();

            int playerHighestTotal = playerValidTotals.OrderByDescending(t => t).First(); //we already checked above that player valid total count is not zero so we expect a result here
            int dealerHighestTotal = dealerValidTotals.OrderByDescending(t => t).FirstOrDefault(); //will return 0 if dealer has no valid results


            if (player.Cards.Count >= 5 && playerHighestTotal == 21
                && player.Cards.ElementAt(player.Cards.Count - 1).Values.Item1 == 1) //5 cards and 21 and last card was an ace so they went 20 -> 21
            {
                return BlackjackResultType.WinFiveCardTwentyToTwentyOne;
            }

            if (playerHighestTotal == 21
                && player.Cards.ElementAt(player.Cards.Count - 1).Values.Item1 == 1) //21 and last card was an ace so they went 20 -> 21
            {
                return BlackjackResultType.WinTwentyToTwentyOne;
            }

            if (player.Cards.Count >= 5 && playerHighestTotal == 21)
            {
                return BlackjackResultType.WinFiveCardTwentyOne;
            }

            if (playerHighestTotal == 21)
                return BlackjackResultType.WinTwentyOne;


            if (player.Cards.Count >= 5)
                return BlackjackResultType.WinFiveCard;

            if (playerHighestTotal == 21)
                return BlackjackResultType.WinTwentyOne;

            if (playerHighestTotal == dealerHighestTotal)
            {
                return BlackjackResultType.Draw;
            }
            else if (playerHighestTotal > dealerHighestTotal)
            {
                return BlackjackResultType.Win;
            }

            return BlackjackResultType.Lose;
        }

        public enum BlackjackResultType
        {
            Win,
            Lose,
            Draw,
            WinTwentyOne,
            WinFiveCard,
            WinFiveCardTwentyOne,
            WinTwentyToTwentyOne,
            WinFiveCardTwentyToTwentyOne
        }
    }
}
