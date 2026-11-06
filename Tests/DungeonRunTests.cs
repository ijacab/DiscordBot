using DiscordBot.Games.Models.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
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
            Assert.False(run.CanLoot);
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
            double taken = run.TakeHaulForPvp();

            Assert.Equal(before, taken);
            Assert.Equal(0, run.Haul);
            Assert.True(run.IsOver);
        }

        [Fact]
        public void SplitHaul_SharesEvenlyAndPaysTheRemainderToTheFirstRecipient()
        {
            IReadOnlyDictionary<ulong, double> payouts = DungeonRun.SplitHaul(100, new List<ulong> { 1, 2, 3 });

            Assert.Equal(33.34, payouts[1], 2);
            Assert.Equal(33.33, payouts[2], 2);
            Assert.Equal(33.33, payouts[3], 2);
            Assert.Equal(100, payouts.Values.Sum(), 2);
        }

        [Fact]
        public void SplitHaul_WithNoRecipients_PaysNothing()
        {
            Assert.Empty(DungeonRun.SplitHaul(100, new List<ulong>()));
        }

        [Fact]
        public void Buying_WithNothingOnOffer_Fails()
        {
            var run = RunFor(1);

            Assert.False(run.TryBuyOffer(out _));
        }

        [Fact]
        public void Drinking_WithNoPotions_Fails()
        {
            var run = RunFor(1);

            Assert.False(run.TryDrink(out _));
        }

        [Fact]
        public void PotionPrice_ScalesWithBuyIn()
        {
            Assert.True(RunFor(1, 10000).PotionPrice > RunFor(1, 1000).PotionPrice);
        }

        [Fact]
        public void ApplyLoot_BoostsStatsAndUsesUpTheRoomsBox()
        {
            DungeonRun run = null;
            for (int seed = 1; seed <= 100 && run == null; seed++)
            {
                var candidate = RunFor(seed);
                candidate.Descend();
                if (candidate.Alive) run = candidate;
            }
            Assert.NotNull(run);
            Assert.True(run.CanLoot);

            var loot = new LootResult { Tier = LootTier.Rare, Name = "Test", AttackGain = 2, DefenseGain = 1, MaxHpGain = 5 };
            int attack = run.Attack, defense = run.Defense, maxHp = run.MaxHp;
            run.ApplyLoot(loot);

            Assert.Equal(attack + 2, run.Attack);
            Assert.Equal(defense + 1, run.Defense);
            Assert.Equal(maxHp + 5, run.MaxHp);
            Assert.False(run.CanLoot);
            Assert.Throws<InvalidOperationException>(() => run.ApplyLoot(loot));
        }

        [Fact]
        public void ApplyLoot_BeforeAnyRoom_Throws()
        {
            var run = RunFor(8);

            Assert.Throws<InvalidOperationException>(() => run.ApplyLoot(new LootResult()));
        }
    }
}
