using DiscordBot.Games.Models.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    public class DungeonRunTests
    {
        private static DungeonRun PartyOf(int size, int seed = 1)
        {
            var run = new DungeonRun(1000, new Random(seed));
            for (int i = 1; i <= size; i++)
                run.AddMember((ulong)i, $"P{i}");
            return run;
        }

        [Fact]
        public void NewRun_HasNoMembersUntilAdded()
        {
            var run = new DungeonRun(1000, new Random(1));

            Assert.Empty(run.Members);
            Assert.Equal(0, run.Haul);
            Assert.Equal(0, run.Floor);
            Assert.False(run.IsOver);
        }

        [Fact]
        public void AddMember_StartsEachMemberAtFullHealth()
        {
            var run = PartyOf(2);

            Assert.All(run.Members, m =>
            {
                Assert.Equal(DungeonMember.StartingHp, m.Hp);
                Assert.Equal(DungeonMember.StartingHp, m.MaxHp);
                Assert.True(m.Alive);
            });
        }

        [Fact]
        public void AddMember_RejectsFullPartiesAndDuplicates()
        {
            var run = PartyOf(DungeonRun.MaxPartySize);

            Assert.Throws<InvalidOperationException>(() => run.AddMember(99, "Extra"));
            Assert.Throws<InvalidOperationException>(() => PartyOf(1).AddMember(1, "Again"));
        }

        [Fact]
        public void AddMember_RejectsJoinsAfterTheRunStarts()
        {
            var run = PartyOf(1, seed: 4);
            run.Descend();

            Assert.Throws<InvalidOperationException>(() => run.AddMember(50, "Late"));
        }

        [Fact]
        public void Boss_AppearsOnEveryFifthFloor()
        {
            int reachedFifth = 0;
            for (int seed = 1; seed <= 50; seed++)
            {
                var run = PartyOf(2, seed);
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

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(4)]
        public void Invariants_HoldAcrossManySeeds(int partySize)
        {
            for (int seed = 1; seed <= 100; seed++)
            {
                var run = PartyOf(partySize, seed);
                int guard = 0;
                while (!run.IsOver && guard++ < 500)
                {
                    RoomResult room = run.Descend();

                    Assert.True(run.Haul >= 0, $"haul went negative with seed {seed}");
                    Assert.All(run.Living, m =>
                    {
                        Assert.True(m.Hp > 0, $"living member at {m.Hp} HP with seed {seed}");
                        Assert.True(m.Hp <= m.MaxHp, $"HP above max with seed {seed}");
                    });
                    if (room.Died)
                    {
                        Assert.True(run.IsOver);
                        Assert.Equal(0, run.LivingCount);
                    }
                }
            }
        }

        [Fact]
        public void Descend_AfterTheRunEnds_Throws()
        {
            for (int seed = 1; seed <= 100; seed++)
            {
                var run = PartyOf(2, seed);
                int guard = 0;
                while (!run.IsOver && guard++ < 500)
                    run.Descend();

                if (run.IsOver)
                {
                    Assert.Throws<InvalidOperationException>(() => run.Descend());
                    return;
                }
            }
            Assert.True(false, "no seed ended a run in range, so the test checked nothing");
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
        public void CashOut_LeavesOutDeadMembers()
        {
            var run = PartyOf(3);
            run.Member(2).Alive = false;

            IReadOnlyDictionary<ulong, double> payouts = run.CashOut();

            Assert.Equal(2, payouts.Count);
            Assert.False(payouts.ContainsKey(2));
            Assert.True(run.IsOver);
        }

        [Theory]
        [InlineData(4, 2, false)]
        [InlineData(4, 3, true)]
        [InlineData(3, 2, true)]
        [InlineData(2, 1, false)]
        [InlineData(2, 2, true)]
        [InlineData(1, 1, true)]
        public void VoteCashOut_NeedsMoreThanHalfOfTheLivingMembers(int partySize, int votes, bool shouldCashOut)
        {
            var run = PartyOf(partySize);
            bool result = false;
            for (int i = 1; i <= votes; i++)
                result = run.VoteCashOut((ulong)i);

            Assert.Equal(shouldCashOut, result);
        }

        [Fact]
        public void VoteCashOut_CountsOnlyLivingMembers()
        {
            var run = PartyOf(3);
            run.Member(3).Alive = false;

            Assert.Throws<InvalidOperationException>(() => run.VoteCashOut(3));
            Assert.False(run.VoteCashOut(1)); //1 of 2 living is not more than half
            Assert.True(run.VoteCashOut(2));
        }

        [Fact]
        public void Buying_WithNothingOnOffer_Fails()
        {
            var run = PartyOf(1);

            Assert.False(run.TryBuyOffer(1, out _));
        }

        [Fact]
        public void Buying_ByANonMember_Fails()
        {
            var run = PartyOf(1, seed: 2);
            run.Descend();

            Assert.False(run.TryBuyOffer(999, out _));
        }

        [Fact]
        public void Drinking_WithNoPotions_Fails()
        {
            var run = PartyOf(1);

            Assert.False(run.TryDrink(1, out _));
        }

        [Fact]
        public void PotionPrice_ScalesWithBuyIn()
        {
            Assert.True(new DungeonRun(10000, new Random(1)).PotionPrice > new DungeonRun(1000, new Random(1)).PotionPrice);
        }
    }
}
