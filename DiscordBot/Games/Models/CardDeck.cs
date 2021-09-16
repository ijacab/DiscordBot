using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class CardDeck
    {
        public List<Card> Cards { get; }
        public CardDeck(int numberOfDecks)
        {
            InitialiseDeck(numberOfDecks);
        }

        private void InitialiseDeck(int numberofDecks)
        {
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
    }
}
