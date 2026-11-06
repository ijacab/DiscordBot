using DiscordBot.Games.Models.Dungeon;
using System;
using Xunit;

namespace Tests
{
    /// <summary>
    /// Checks that a run's odds of clearing blocks match gambling odds: about 50% for the first 10 floors,
    /// 25% for 20 and 12.5% for 30. Runs are seeded, so the result is the same every time.
    /// The bands leave room for the loot and luck the sim can't model exactly. If this fails, retune
    /// <see cref="DungeonRun.BlockDifficultyStep"/> or the monster numbers in <see cref="DungeonRun.MonsterFor"/>.
    /// </summary>
    public class DungeonOddsTests
    {
        private const int Runs = 10000;

        [Fact]
        public void BlockOdds_MatchGamblingOdds()
        {
            int clearedOne = 0, clearedTwo = 0, clearedThree = 0;
            for (int seed = 1; seed <= Runs; seed++)
            {
                var run = new DungeonRun((ulong)seed, $"P{seed}", 1000, new Random(seed));

                run.RunBlock();
                if (!run.Alive) continue;
                clearedOne++;
                ContinueWithoutPvp(run);

                run.RunBlock();
                if (!run.Alive) continue;
                clearedTwo++;
                ContinueWithoutPvp(run);

                run.RunBlock();
                if (run.Alive) clearedThree++;
            }

            double one = (double)clearedOne / Runs;
            double two = (double)clearedTwo / Runs;
            double three = (double)clearedThree / Runs;

            Assert.InRange(one, 0.45, 0.55);
            Assert.InRange(two, 0.20, 0.30);
            Assert.InRange(three, 0.09, 0.17);
        }

        /// <summary>Wins a pool with no change to the haul, so the odds test only looks at combat.</summary>
        private static void ContinueWithoutPvp(DungeonRun run)
        {
            run.EnterPool();
            double haul = run.TakeHaulIntoPool();
            run.ReturnFromPool(haul, run.Hp);
        }
    }
}
