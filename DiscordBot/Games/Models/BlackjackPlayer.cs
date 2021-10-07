using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class BlackjackPlayer : IPlayer
    {
        public bool IsDealer { get; set; } = false;
        public ulong UserId { get; set; }
        public ulong ChannelId { get; set; }
        public ulong ServerId { get; set; }

        public List<Card> Cards { get; set; } = new List<Card>();
        public double BetAmount { get; set; }
        public double Winnings { get; set; }
        public bool IsFinishedPlaying { get; set; } = false;

        public BlackjackPlayer(ulong userId, ulong channelId, ulong serverId, double betAmount)
        {
            UserId = userId;
            ChannelId = channelId;
            ServerId = serverId;
            BetAmount = betAmount;
        }
        public BlackjackPlayer()
        {
        }
        public IEnumerable<int> GetPossibleTotalValues(bool returnOnlyNonBustValues = true)
        {
            var totals = new List<int>();
            foreach (var card in Cards)
            {
                int defaultValue = 0;

                if (card.Values.Item2 == null) //when not an Ace, i.e. only has one possible value
                {
                    int cardValue = card.Values.Item1;
                    if (!totals.Any())
                    {
                        totals.Add(cardValue);
                    }
                    else
                    {
                        for (int i = 0; i < totals.Count; i++)
                        {
                            totals[i] += cardValue;
                        }
                    }
                }
                else //when ace, it has 2 possible values 1 and 11
                {
                    if (!totals.Any()) totals.Add(defaultValue);
                    int lastValue = totals[totals.Count - 1];

                    for (int i = 0; i < totals.Count; i++)
                    {
                        totals[i] += card.Values.Item1;
                    }
                    totals.Add(lastValue + (int)card.Values.Item2);
                }
            }

            if (returnOnlyNonBustValues)
                return totals.Where(t => t <= 21);
            else
                return totals;
        }

        public string GetFormattedCards()
        {
            string cardsStr = "";
            foreach (var card in Cards)
            {
                cardsStr += $"{card.Name}{GetSuitSymbol(card.Suit)}, ";
            }
            cardsStr = cardsStr.TrimEnd(' ').TrimEnd(',');

            var possibleTotals = GetPossibleTotalValues().ToList();
            int highestValidValue = possibleTotals.OrderByDescending(t => t).FirstOrDefault();
            string valueStr = "";
            if (highestValidValue == default(int))
                valueStr = "(No valid values)";
            else
            {
                foreach (int total in possibleTotals)
                {
                    valueStr += $"{total}, ";
                }
            }
            valueStr = valueStr.TrimEnd(' ').TrimEnd(',');

            cardsStr += $":\t{valueStr}";
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
