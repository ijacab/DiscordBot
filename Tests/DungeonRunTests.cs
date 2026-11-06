using DiscordBot.Games.Models.Dungeon;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tests
{
    public class DungeonRunTests
    {
        private static DungeonRun RunFor(int seed, double buyIn = 1000)
        {
            return new DungeonRun((ulong)seed, $"P{seed}", buyIn, new Random(seed));
        }

        [Fact]
        public void NewRun_StartsAliveAtFullHealthWithNoHaul()
        {
            var run = RunFor(1);

            Assert.Equal(DungeonRun.StartingHp, run.Hp);
            Assert.Equal(DungeonRun.StartingHp, run.MaxHp);
            Assert.Equal(0, run.Haul);
            Assert.Equal(0, run.Floor);
            Assert.True(run.Alive);
            Assert.False(run.IsOver);
            Assert.Empty(run.Items);
        }

        [Fact]
        public void Boss_AppearsOnEveryFifthFloor()
        {
            int reachedFifth = 0;
            for (int seed = 1; seed <= 50; seed++)
            {
                var run = RunFor(seed);
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
                var run = RunFor(seed);
                int guard = 0;
                while (!run.IsOver && guard++ < 500)
                {
                    RoomResult room = run.Descend();

                    Assert.True(run.Haul >= 0, $"haul went negative with seed {seed}");
                    if (room.Died)
                    {
                        Assert.False(run.Alive);
                        Assert.True(run.IsOver);
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
        public void Fights_NeverRunLongerThanTheRoundCap()
        {
            var roundCount = new Regex(@"(\d+) rounds?");
            for (int seed = 1; seed <= 200; seed++)
            {
                var run = RunFor(seed);
                for (int i = 0; i < 30 && !run.IsOver; i++)
                {
                    foreach (string line in run.Descend().Lines)
                    {
                        Match match = roundCount.Match(line);
                        if (match.Success)
                            Assert.True(int.Parse(match.Groups[1].Value) <= Combat.MaxRounds, $"fight ran past the cap with seed {seed}: {line}");
                    }
                }
            }
        }

        [Fact]
        public void CashOut_ReturnsHaulAndEndsRun()
        {
            var run = RunFor(3);

            Assert.Equal(run.Haul, run.CashOut());
            Assert.True(run.IsOver);
            Assert.Throws<InvalidOperationException>(() => run.Descend());
            Assert.Throws<InvalidOperationException>(() => run.CashOut());
        }

        [Fact]
        public void Descend_AfterDeath_Throws()
        {
            for (int seed = 1; seed <= 100; seed++)
            {
                var run = RunFor(seed);
                int guard = 0;
                while (!run.IsOver && guard++ < 500)
                    run.Descend();

                if (!run.Alive)
                {
                    Assert.Throws<InvalidOperationException>(() => run.Descend());
                    return;
                }
            }
            Assert.True(false, "no seed in range produced a death, so the test checked nothing");
        }

        [Fact]
        public void TakeHaulForPvp_EmptiesTheRunAndEndsIt()
        {
            var run = RunFor(5);
            double before = run.Haul;

            Assert.Equal(before, run.TakeHaulForPvp());
            Assert.Equal(0, run.Haul);
            Assert.True(run.IsOver);
        }

        [Fact]
        public void CloneStats_IsACopyThatDoesNotChangeTheRun()
        {
            var run = RunFor(6);
            var copy = run.CloneStats();
            copy.Attack += 50;
            copy.Hp = 1;

            Assert.Equal(DungeonRun.StartingAttack, run.Attack);
            Assert.Equal(DungeonRun.StartingHp, run.Hp);
        }

        [Fact]
        public void SplitHaul_SharesEvenlyAndPaysTheRemainderToTheFirstRecipient()
        {
            var payouts = DungeonRun.SplitHaul(100, new[] { 1UL, 2UL, 3UL });

            Assert.Equal(33.34, payouts[1], 2);
            Assert.Equal(33.33, payouts[2], 2);
            Assert.Equal(33.33, payouts[3], 2);
            Assert.Equal(100, payouts.Values.Sum(), 2);
        }

        [Fact]
        public void Buying_WithNothingOnOffer_Fails()
        {
            Assert.False(RunFor(1).TryBuyOffer(out _));
        }

        [Fact]
        public void Drinking_WithNoPotions_Fails()
        {
            Assert.False(RunFor(1).TryDrink(out _));
        }

        [Fact]
        public void PotionPrice_ScalesWithBuyIn()
        {
            Assert.True(RunFor(1, 10000).PotionPrice > RunFor(1, 1000).PotionPrice);
        }
    }
}
