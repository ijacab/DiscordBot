using DiscordBot.Games.Models.Dungeon;
using System;
using Xunit;

namespace Tests
{
    public class DungeonRunTests
    {
        [Fact]
        public void NewRun_StartsAtFullHealthWithNoHaulOrPotions()
        {
            var run = new DungeonRun(1000, new Random(1));

            Assert.Equal(DungeonRun.StartingHp, run.Hp);
            Assert.Equal(DungeonRun.StartingHp, run.MaxHp);
            Assert.Equal(0, run.Haul);
            Assert.Equal(0, run.Potions);
            Assert.Equal(0, run.Floor);
            Assert.False(run.IsOver);
        }

        [Fact]
        public void Boss_AppearsOnEveryFifthFloor()
        {
            int reachedFifth = 0;
            for (int seed = 1; seed <= 50; seed++)
            {
                var run = new DungeonRun(1000, new Random(seed));
                RoomResult last = null;
                while (!run.IsOver && run.Floor < DungeonRun.BossEvery)
                    last = run.Descend();

                if (run.Floor == DungeonRun.BossEvery && !run.IsOver)
                {
                    reachedFifth++;
                    Assert.Equal(RoomType.Boss, last.Type);
                }
            }
            Assert.True(reachedFifth > 0, "no seed survived to the first boss, so the test checked nothing");
        }

        [Fact]
        public void Invariants_HoldAcrossManySeeds()
        {
            for (int seed = 1; seed <= 200; seed++)
            {
                var run = new DungeonRun(1000, new Random(seed));
                int guard = 0;
                while (!run.IsOver && guard++ < 500)
                {
                    RoomResult room = run.Descend();

                    Assert.True(run.Haul >= 0, $"haul went negative with seed {seed}");
                    if (room.Died)
                    {
                        Assert.True(run.IsDead);
                        Assert.True(run.Hp <= 0);
                    }
                    else
                    {
                        Assert.True(run.Hp > 0);
                        Assert.True(run.Hp <= run.MaxHp);
                    }
                }
            }
        }

        [Fact]
        public void CashOut_ReturnsHaulAndEndsRun()
        {
            var run = new DungeonRun(1000, new Random(3));
            double haul = run.Haul;

            Assert.Equal(haul, run.CashOut());
            Assert.True(run.IsOver);
            Assert.Throws<InvalidOperationException>(() => run.Descend());
            Assert.Throws<InvalidOperationException>(() => run.CashOut());
        }

        [Fact]
        public void Descend_AfterDeath_Throws()
        {
            for (int seed = 1; seed <= 200; seed++)
            {
                var run = new DungeonRun(1000, new Random(seed));
                int guard = 0;
                while (!run.IsOver && guard++ < 500)
                    run.Descend();

                if (run.IsDead)
                {
                    Assert.Throws<InvalidOperationException>(() => run.Descend());
                    return;
                }
            }
            Assert.True(false, "no seed in range produced a death, so the test checked nothing");
        }

        [Fact]
        public void Buying_WithNothingOnOffer_Fails()
        {
            var run = new DungeonRun(1000, new Random(1));

            Assert.False(run.TryBuyOffer(out _));
        }

        [Fact]
        public void Drinking_WithNoPotions_Fails()
        {
            var run = new DungeonRun(1000, new Random(1));

            Assert.False(run.TryDrink(out _));
        }

        [Fact]
        public void PotionPrice_ScalesWithBuyIn()
        {
            Assert.True(new DungeonRun(10000, new Random(1)).PotionPrice > new DungeonRun(1000, new Random(1)).PotionPrice);
        }
    }
}
