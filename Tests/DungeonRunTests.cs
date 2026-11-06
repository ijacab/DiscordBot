using DiscordBot.Games.Models.Dungeon;
using System;
using System.Collections.Generic;
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

        /// <summary>Finds a seed whose run survives its first block, so tests can look at a full block.</summary>
        private static DungeonRun SurvivingRun(out List<RoomResult> rooms)
        {
            for (int seed = 1; seed <= 500; seed++)
            {
                var run = RunFor(seed);
                rooms = run.RunBlock();
                if (run.Alive)
                    return run;
            }
            throw new InvalidOperationException("no seed survived a block, so the test can't run");
        }

        /// <summary>Plays a run through the checkpoint by taking the fight and winning it with no change, for tests that don't care about PvP.</summary>
        private static void WinPoolAndKeepGoing(DungeonRun run)
        {
            run.EnterPool();
            double haul = run.TakeHaulIntoPool();
            run.ReturnFromPool(haul, run.Hp);
        }

        [Fact]
        public void NewRun_StartsCrawlingAliveAtFullHealth()
        {
            var run = RunFor(1);

            Assert.Equal(DungeonRun.StartingHp, run.Hp);
            Assert.Equal(DungeonRun.StartingHp, run.MaxHp);
            Assert.Equal(0, run.Haul);
            Assert.Equal(0, run.Floor);
            Assert.Equal(0, run.BlocksCleared);
            Assert.Equal(1, run.NextBlockNumber);
            Assert.Equal(RunPhase.Crawling, run.Phase);
            Assert.True(run.Alive);
            Assert.False(run.IsOver);
            Assert.Empty(run.Items);
        }

        [Fact]
        public void RunBlock_CrawlsTenFloorsAndStopsAtACheckpoint()
        {
            var run = SurvivingRun(out List<RoomResult> rooms);

            Assert.Equal(DungeonRun.FloorsPerBlock, rooms.Count);
            Assert.Equal(DungeonRun.FloorsPerBlock, run.Floor);
            Assert.Equal(RunPhase.AtCheckpoint, run.Phase);
            Assert.Equal(1, run.BlocksCleared);
        }

        [Fact]
        public void RunBlock_HasBossesOnFloorsFiveAndTen()
        {
            var run = SurvivingRun(out List<RoomResult> rooms);

            Assert.Equal(RoomType.Boss, rooms[4].Type);
            Assert.Equal(RoomType.Boss, rooms[9].Type);
        }

        [Fact]
        public void RunBlock_CannotStartAgainWithoutAChoice()
        {
            var run = SurvivingRun(out _);

            Assert.Throws<InvalidOperationException>(() => run.RunBlock());
        }

        [Fact]
        public void Checkpoint_FightThenWin_LetsTheRunCrawlTheNextBlock()
        {
            var run = SurvivingRun(out _);
            WinPoolAndKeepGoing(run);

            Assert.Equal(RunPhase.Crawling, run.Phase);
            run.RunBlock();
            Assert.True(run.Phase == RunPhase.AtCheckpoint ? run.Floor == 20 : run.Phase == RunPhase.Out);
        }

        [Fact]
        public void EnterPool_NeedsACheckpoint()
        {
            var run = RunFor(2);

            Assert.Throws<InvalidOperationException>(() => run.EnterPool());
        }

        [Fact]
        public void CashOut_IsRefusedInThePool()
        {
            var run = SurvivingRun(out _);
            run.EnterPool();

            Assert.Throws<InvalidOperationException>(() => run.CashOut());
        }

        [Fact]
        public void CashOut_AtACheckpoint_ReturnsTheHaulAndEndsTheRun()
        {
            var run = SurvivingRun(out _);
            double haul = run.Haul;

            Assert.Equal(haul, run.CashOut());
            Assert.True(run.IsOver);
            Assert.Equal(RunPhase.Out, run.Phase);
        }

        [Fact]
        public void Eliminate_OnlyFromThePool()
        {
            var run = SurvivingRun(out _);
            Assert.Throws<InvalidOperationException>(() => run.Eliminate());

            run.EnterPool();
            run.Eliminate();
            Assert.True(run.IsOver);
            Assert.Equal(RunPhase.Out, run.Phase);
        }

        [Fact]
        public void ReturnFromPool_AddsWinningsAndKeepsHitPointsWithinRange()
        {
            var run = SurvivingRun(out _);
            run.EnterPool();
            run.TakeHaulIntoPool();

            run.ReturnFromPool(500, 9999);

            Assert.Equal(500, run.Haul);
            Assert.Equal(run.MaxHp, run.Hp);
            Assert.Equal(RunPhase.Crawling, run.Phase);
        }

        [Fact]
        public void Invariants_HoldAcrossManySeedsAndBlocks()
        {
            for (int seed = 1; seed <= 200; seed++)
            {
                var run = RunFor(seed);
                for (int block = 0; block < 5 && run.Phase != RunPhase.Out; block++)
                {
                    run.RunBlock();
                    Assert.True(run.Haul >= 0, $"haul went negative with seed {seed}");
                    if (run.Alive)
                    {
                        Assert.True(run.Hp > 0);
                        Assert.True(run.Hp <= run.MaxHp);
                        WinPoolAndKeepGoing(run);
                    }
                    else
                    {
                        Assert.Equal(RunPhase.Out, run.Phase);
                        Assert.True(run.IsOver);
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
                for (int block = 0; block < 3 && run.Phase != RunPhase.Out; block++)
                {
                    foreach (RoomResult room in run.RunBlock())
                        foreach (string line in room.Lines)
                        {
                            Match match = roundCount.Match(line);
                            if (match.Success)
                                Assert.True(int.Parse(match.Groups[1].Value) <= Combat.MaxRounds, $"fight ran past the cap with seed {seed}: {line}");
                        }
                    if (run.Alive)
                        WinPoolAndKeepGoing(run);
                }
            }
        }

        [Fact]
        public void TakeHaulIntoPool_EmptiesTheRunButDoesNotEndIt()
        {
            var run = SurvivingRun(out _);
            run.EnterPool();
            double before = run.Haul;

            Assert.Equal(before, run.TakeHaulIntoPool());
            Assert.Equal(0, run.Haul);
            Assert.False(run.IsOver);
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

    }
}
