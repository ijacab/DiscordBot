using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models
{
    public static class DiceRoller
    {
        private static Random _random = new Random();
        public static List<int> RollDice(int numberOfDice, int diceMaxNumber = 6)
        {
            var diceResults = new List<int>();
            for (int i = 1; i <= numberOfDice; i++)
            {
                int roll = _random.Next(1, diceMaxNumber + 1);
                diceResults.Add(roll);
            }
            return diceResults;
        }
    }
}
