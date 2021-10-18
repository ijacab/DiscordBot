using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class Card
    {
        public readonly string Name; //A,2,3,J,K,etc
        public readonly Tuple<int,int?> Values; //2,3,10,10,1/11etc
        public readonly Suit Suit;

        public Card(string name, Suit suit)
        {
            Name = name;
            Suit = suit;
            switch (name)
            {
                case "A":
                    Values = new Tuple<int, int?>(1, 11);
                    break;
                case "J":
                case "Q":
                case "K":
                    Values = new Tuple<int, int?>(10, null);
                    break;
                default:
                    Values = new Tuple<int, int?>(Convert.ToInt32(name), null);
                    break;
            }
        }

        public char GetSuitSymbol()
        {
            switch (Suit)
            {
                case Suit.Hearts:
                    return '♡';
                case Suit.Diamonds:
                    return '♢';
                case Suit.Clubs:
                    return '♧';
                case Suit.Spades:
                    return '♤';
                default:
                    throw new Exception($"No symbol found for this suit.");
            }
        }
    }

    public enum Suit
    {
        Hearts,
        Diamonds,
        Clubs,
        Spades
    }
}
