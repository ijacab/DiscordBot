using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class BlackjackPlayer : IPlayer
    {
        public bool IsDealer { get; set; } = false;
        public ulong Id { get; set; }
        public List<Card> Cards { get; set; }
        public double BetAmount { get; set; }
        public bool IsFinishedPlaying { get; set; } = false;
        public BlackjackPlayer(ulong id, double betAmount)
        {
            Id = id;
            BetAmount = betAmount;
            Cards = new List<Card>();
        }
        public BlackjackPlayer()
        {
        }
        public List<int> GetPossibleTotalValues()
        {
            var totals = new List<int>();
            foreach (var card in Cards)
            {
                totals.Add(0);

                if (card.Values.Item2 == null)
                {
                    totals[totals.Count - 1] += card.Values.Item1;
                }
                else
                {
                    int lastValue = totals[totals.Count - 1];
                    for (int i = 0; i < totals.Count; i++)
                    {
                        totals[i] += card.Values.Item1;
                    }
                    totals.Add(lastValue + (int)card.Values.Item2);
                }
            }
            return totals;
        }

        public string GetFormattedCards()
        {
            string cardsStr = "";
            foreach (var card in Cards)
            {
                cardsStr += $"{card.Name}{GetSuitSymbol(card.Suit)},";
            }
            cardsStr.TrimEnd(',');

            int highestValidValue = GetPossibleTotalValues().Where(t => t <= 21).OrderByDescending(t => t).FirstOrDefault();
            string valueStr = "";
            if (highestValidValue == default(int))
                valueStr = "(No non bust values)";
            else
                valueStr = $"{highestValidValue}";

            cardsStr += $" - {valueStr}";
            return cardsStr;
        }

        private char GetSuitSymbol(Suit suit)
        {
            switch (suit)
            {
                case Suit.Hearts:
                    return '♥';
                case Suit.Diamonds:
                    return '♦';
                case Suit.Clubs:
                    return '♣';
                case Suit.Spades:
                    return '♠';
                default:
                    throw new Exception($"No symbol found for this suit.");
            }
        }

    }
}
