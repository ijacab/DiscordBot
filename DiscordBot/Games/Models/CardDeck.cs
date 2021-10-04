using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class CardDeck
    {
        public List<Card> Cards { get; private set; }
        public CardDeck(int numberOfDecks)
        {
            Reshuffle(numberOfDecks);
        }

        public void Reshuffle(int numberofDecks)
        {
            Cards = new List<Card>();
            var cardArray = new string[] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A" };
            for (int i = 0; i < numberofDecks; i++)
            {
                foreach (var suit in Enum.GetValues(typeof(Suit)).Cast<Suit>()) 
                {
                    foreach(string cardName in cardArray)
                    {
                        var card = new Card(cardName, suit);
                        Cards.Add(card);
                    }
                }
            }
        }

        public Card Take()
        {
            int randomIndex = -11111;
            Card card;
            try
            {
                randomIndex = new Random().Next(0, Cards.Count);
                card = Cards[randomIndex];
                Cards.RemoveAt(randomIndex);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed in Card Take method. Card count: {Cards.Count} RandomIndex: {randomIndex}\n{ex.Message}");
            }

            return card;
        }
    }
}
