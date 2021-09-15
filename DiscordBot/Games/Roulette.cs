using DiscordBot.Games.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace DiscordBot.Games
{
    public class Roulette
    {
        private static Dictionary<int, string> _outcomes;
        public Roulette()
        {
            _outcomes = GetPossibleOutcomes();
        }
        public Tuple<List<string>, List<RouletteBet>> Play(List<RouletteBet> inputs)
        {
            var result = new Random().Next(-1, 36 + 1);

            List<string> winningChoices = _outcomes[result].Split(',').ToList();
            var winningInputs = inputs.Where(
                input =>
                    input.RoulleteBetType != BetType.NotValid //valid
                        && (winningChoices.Contains(input.RoulleteBetType.ToString()) //winning bet is the same bet type as the one chosen
                            || (input.RoulleteBetType == BetType.Number && input.BetNumberChoice == result) //or if it's a number then it is winning if the number is correct
                        )
                );

            return new Tuple<List<string>, List<RouletteBet>>(winningChoices, winningInputs.ToList());
        }

        private static Dictionary<int, string> GetPossibleOutcomes()
        {
            var outcomes = new Dictionary<int, string>();

            for (int num = -1; num <= 36; num++)
            {
                outcomes.Add(num, num.ToString());
            }

            int[] reds = new int[] { 32, 19, 21, 25, 34, 27, 36, 30, 23, 5, 16, 1, 14, 9, 18, 7, 12, 3 };
            int[] blacks = new int[] { 15, 4, 2, 17, 6, 13, 11, 8, 10, 24, 33, 20, 31, 22, 29, 28, 35, 26 };

            foreach (int red in reds)
            {
                outcomes[red] += ",Red";
            }

            foreach (int black in blacks)
            {
                outcomes[black] += ",Black";
            }

            outcomes[-1] += ",None"; //this is "00" choice
            outcomes[0] += ",None";

            int[] firstColumn = { 1, 4, 7, 10, 13, 16, 19, 22, 25, 28, 31, 34 };
            int[] secondColumn = { 2, 5, 8, 11, 14, 17, 20, 23, 26, 29, 32, 35 };
            int[] thirdColumn = { 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 33, 36 };


            foreach (int num in firstColumn)
            {
                outcomes[num] += ",FirstColumn";
            }

            foreach (int num in secondColumn)
            {
                outcomes[num] += ",SecondColumn";
            }

            foreach (int num in thirdColumn)
            {
                outcomes[num] += ",ThirdColumn";
            }

            for (int num = 1; num <= 36; num++)
            {
                if (num >= 1 && num <= 12)
                    outcomes[num] += ",FirstDozen";

                if (num >= 13 && num <= 24)
                    outcomes[num] += ",SecondDozen";

                if (num >= 25 && num <= 36)
                    outcomes[num] += ",ThirdDozen";
            }

            for (int num = 1; num <= 36; num++)
            {
                if (num >= 1 && num <= 36 && num % 2 == 0)
                    outcomes[num] += ",Even";

                if (num >= 1 && num <= 36 && num % 2 != 0)
                    outcomes[num] += ",Odd";
            }

            return outcomes;
        }
    }
}
