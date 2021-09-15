using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class Card
    {
        public readonly char Name; //A,2,3,J,K,etc
        public readonly int[] Values; //2,3,10,10,1/11etc
        public readonly Face Face;

        public Card(char name, Face face)
        {
            Name = name;
            switch (name)
            {
                case 'A':
                    Values = new int[] { 1, 11 };
                    break;
                case 'J':
                case 'Q':
                case 'K':
                    Values = new int[] { 10 };
                    break;
                default:
                    Values = new int[] { Convert.ToInt32(name) };
                    break;
            }
        }
    }

    public enum Face
    {
        Hearts,
        Diamonds,
        Clubs,
        Spades
    }
}
