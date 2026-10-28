using DiscordBot.Games.Models;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games
{
    /// <summary>
    /// Every player gets their own duck and races against one extra bot duck, so the more players the lower
    /// each player's odds and the higher the payout.
    /// </summary>
    public class DuckRace : BaseMultiplayerGame<DuckRacePlayer>
    {
        public const int TrackLength = 12;
        public const int MaxStepPerTick = 3;
        public const int BackwardChanceOneIn = 20; //each tick a duck has this chance (1 in N) to be pushed back a space instead of moving forward
        public const double PayoutPerDuck = 0.9; //winner gets bet * DuckCount * PayoutPerDuck, so the house keeps 10%

        /// <summary>One duck per player plus the bot duck (always the last duck).</summary>
        public int DuckCount => Players.Count + 1;

        /// <summary>1-based number of the duck that won the race.</summary>
        public int WinningDuck { get; private set; }

        /// <summary>Every duck's position after each tick. Frame 0 is the starting line. Index 0 is duck 1.</summary>
        public List<int[]> Frames { get; } = new List<int[]>();

        public bool IsBotDuck(int duckNumber) => duckNumber == DuckCount;

        /// <summary>
        /// Moves every duck a random distance each tick until at least one reaches the finish line.
        /// Ties on the final tick are broken randomly. Also assigns each player their duck number.
        /// </summary>
        public void RunRace()
        {
            for (int i = 0; i < Players.Count; i++)
                Players[i].DuckNumber = i + 1;

            var positions = new int[DuckCount];
            Frames.Clear();
            Frames.Add((int[])positions.Clone());

            while (true)
            {
                var moves = DiceRoller.RollDice(DuckCount, MaxStepPerTick);
                var backwardRolls = DiceRoller.RollDice(DuckCount, BackwardChanceOneIn);
                for (int i = 0; i < DuckCount; i++)
                {
                    int step = backwardRolls[i] == 1 ? -1 : moves[i];
                    positions[i] = System.Math.Clamp(positions[i] + step, 0, TrackLength);
                }

                Frames.Add((int[])positions.Clone());

                int leadingPosition = positions.Max();
                if (leadingPosition < TrackLength)
                    continue;

                var leaders = Enumerable.Range(0, DuckCount).Where(i => positions[i] == leadingPosition).ToList();
                WinningDuck = leaders[DiceRoller.RollDice(1, leaders.Count)[0] - 1] + 1;
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
