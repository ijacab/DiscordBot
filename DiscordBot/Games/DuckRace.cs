using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games
{
    public class DuckRace : BaseMultiplayerGame<DuckRacePlayer>
    {
        public const int DuckCount = 5;
        public const int TrackLength = 20;
        public const int MaxStepPerTick = 3;
        public const double PayoutPerDuck = 0.9; //winner gets bet * DuckCount * PayoutPerDuck, so the house keeps 10%

        /// <summary>1-based number of the duck that won the race.</summary>
        public int WinningDuck { get; private set; }

        /// <summary>Distance covered by each duck, index 0 is duck 1.</summary>
        public int[] FinalPositions { get; private set; } = new int[DuckCount];

        /// <summary>
        /// Moves every duck a random distance each tick until at least one reaches the finish line.
        /// Ties on the final tick are broken randomly.
        /// </summary>
        public void RunRace()
        {
            var positions = new int[DuckCount];
            while (true)
            {
                var moves = DiceRoller.RollDice(DuckCount, MaxStepPerTick);
                for (int i = 0; i < DuckCount; i++)
                    positions[i] += moves[i];

                int leadingPosition = positions.Max();
                if (leadingPosition < TrackLength)
                    continue;

                var leaders = Enumerable.Range(0, DuckCount).Where(i => positions[i] == leadingPosition).ToList();
                int winnerIndex = leaders[DiceRoller.RollDice(1, leaders.Count)[0] - 1];

                FinalPositions = positions;
                WinningDuck = winnerIndex + 1;
                return;
            }
        }

        public override double GetWinnings(DuckRacePlayer player)
        {
            return player.DuckNumber == WinningDuck
                ? player.BetAmount * DuckCount * PayoutPerDuck
                : 0;
        }
    }
}
