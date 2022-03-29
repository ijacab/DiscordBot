using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class BlackjackPlayer : BasePlayer
    {
        public List<Card> Cards { get; set; } = new List<Card>();


        public BlackjackPlayer(ulong userId, ulong channelId, ulong serverId, double betAmount, string username)
            : base(userId, channelId, serverId, betAmount, username)
        {
            UserId = userId;
            ChannelId = channelId;
            ServerId = serverId;
            BetAmount = betAmount;
            Username = username;
        }
        public BlackjackPlayer() : base()
        {
        }

        public IEnumerable<int> GetPossibleTotalValues(bool returnOnlyNonBustValues = true)
        {
            return BlackjackPlayer.GetPossibleTotalValues(Cards, returnOnlyNonBustValues);
        }

        public static IEnumerable<int> GetPossibleTotalValues(List<Card> cards, bool returnOnlyNonBustValues = true)
        {
            var totals = new List<int>();
            foreach (var card in cards)
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
                cardsStr += $"{card.Name}{card.GetSuitSymbol()}, ";
            }
            cardsStr = cardsStr.TrimEnd(' ').TrimEnd(',');

            var possibleTotals = GetPossibleTotalValues().ToList();
            int highestValidValue = possibleTotals.OrderByDescending(t => t).FirstOrDefault();
            string valueStr = "";
            if (highestValidValue == default(int))
                valueStr = "(Bust)";
            else
            {
                foreach (int total in possibleTotals)
                {
                    valueStr += $"**{total}**, ";
                }
            }
            valueStr = valueStr.TrimEnd(' ').TrimEnd(',');

            cardsStr += $":\t\t{valueStr}";
            if (IsFinishedPlaying)
                cardsStr += "\t\t~";
            return cardsStr;
        }



    }
}
