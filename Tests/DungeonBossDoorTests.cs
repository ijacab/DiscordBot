using DiscordBot.Games.Models.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    /// <summary>The floor-by-floor crawl: boss doors, sneaking, and potions.</summary>
    public class DungeonBossDoorTests
    {
        /// <summary>Crawls floor by floor to the first boss door, or returns null if the run died first.</summary>
        private static DungeonRun AtFirstDoor(int seed)
        {
            var run = new DungeonRun((ulong)seed, $"P{seed}", 1000, new Random(seed));
            while (run.Phase == RunPhase.Crawling)
                run.RunFloor();
            return run.Phase == RunPhase.AtBossDoor ? run : null;
        }

        [Fact]
        public void RunFloor_StopsBeforeTheBossWithoutEnteringIt()
        {
            DungeonRun run = Enumerable.Range(1, 100).Select(AtFirstDoor).First(r => r != null);

            Assert.Equal(4, run.Floor);
            Assert.True(run.NextIsBoss);
            Assert.NotNull(run.PendingBossName);
            Assert.Throws<InvalidOperationException>(() => run.RunFloor());
        }

        [Fact]
        public void FaceBoss_Fight_EntersTheBossFloor()
        {
            DungeonRun run = Enumerable.Range(1, 100).Select(AtFirstDoor).First(r => r != null);

            RoomResult room = run.FaceBoss(BossChoice.Fight);

            Assert.Equal(RoomType.Boss, room.Type);
            Assert.Equal(5, run.Floor);
            Assert.NotNull(room.EnemyMaxHp);
            Assert.Null(run.PendingBossName);
        }

        [Fact]
        public void FaceBoss_AwayFromADoor_Throws()
        {
            var run = new DungeonRun(1, "P", 1000, new Random(1));
            Assert.Throws<InvalidOperationException>(() => run.FaceBoss(BossChoice.Fight));
        }

        [Fact]
        public void FaceBoss_Potion_WithNoPotions_Throws()
        {
            DungeonRun run = Enumerable.Range(1, 200).Select(AtFirstDoor).First(r => r != null && r.Potions == 0);
            Assert.Throws<InvalidOperationException>(() => run.FaceBoss(BossChoice.Potion));
            Assert.Equal(RunPhase.AtBossDoor, run.Phase);
        }

        [Fact]
        public void Sneak_EitherSkipsTheHoardOrGivesTheBossTheFirstStrike()
        {
            int sneaked = 0, caught = 0;
            foreach (DungeonRun run in Enumerable.Range(1, 400).Select(AtFirstDoor).Where(r => r != null))
            {
                double haul = run.Haul;
                int maxHp = run.MaxHp;
                RoomResult room = run.FaceBoss(BossChoice.Sneak);
                if (room.Sneaked)
                {
                    sneaked++;
                    Assert.Equal(haul, run.Haul);
                    Assert.Equal(maxHp, run.MaxHp);
                    Assert.Null(room.EnemyMaxHp);
                    Assert.Equal(5, run.Floor);
                }
                else
                {
                    caught++;
                    Assert.NotNull(room.EnemyMaxHp);
                    Assert.Contains(room.Lines, l => l.Contains("strikes first"));
                }
            }

            double share = (double)sneaked / (sneaked + caught);
            Assert.InRange(share, 0.4, 0.6);
        }

        [Fact]
        public void Potions_DropInQuietRoomsAndChests()
        {
            int quiet = 0, quietPotions = 0, chests = 0, chestPotions = 0;
            for (int seed = 1; seed <= 3000; seed++)
            {
                var run = new DungeonRun((ulong)seed, "P", 1000, new Random(seed));
                foreach (RoomResult room in run.RunBlock())
                {
                    if (room.Type == RoomType.Quiet) { quiet++; if (room.FoundPotion) quietPotions++; }
                    if (room.Type == RoomType.Treasure && room.EnemyName == null) { chests++; if (room.FoundPotion) chestPotions++; }
                }
            }

            Assert.InRange((double)quietPotions / quiet, 0.07, 0.13);
            Assert.InRange((double)chestPotions / chests, 0.20, 0.30);
        }

        [Fact]
        public void DrinkPotion_HealsHalfOfMaxHpAndUsesOneUp()
        {
            foreach (int seed in Enumerable.Range(1, 3000))
            {
                var run = new DungeonRun((ulong)seed, "P", 1000, new Random(seed));
                while (run.Phase == RunPhase.Crawling && !(run.Potions > 0 && run.Hp < run.MaxHp))
                    run.RunFloor();
                if (!(run.Potions > 0 && run.Hp < run.MaxHp))
                    continue;

                int before = run.Hp, potions = run.Potions;
                int healed = run.DrinkPotion();

                Assert.Equal(Math.Min(run.MaxHp - before, (int)Math.Round(run.MaxHp * DungeonRun.PotionHealShare)), healed);
                Assert.Equal(before + healed, run.Hp);
                Assert.Equal(potions - 1, run.Potions);
                return;
            }
            throw new InvalidOperationException("no seed found a potion while hurt");
        }

        [Fact]
        public void DrinkPotion_IsRefusedWithNoPotionsOrAtFullHp()
        {
            var run = new DungeonRun(1, "P", 1000, new Random(1));
            Assert.Throws<InvalidOperationException>(() => run.DrinkPotion());
        }

        [Fact]
        public void RunBlock_MatchesFloorByFloorWithFightsAtEveryDoor()
        {
            for (int seed = 1; seed <= 50; seed++)
            {
                var whole = new DungeonRun(1, "P", 1000, new Random(seed));
                var stepped = new DungeonRun(1, "P", 1000, new Random(seed));

                List<RoomResult> rooms = whole.RunBlock();
                var steppedRooms = new List<RoomResult>();
                while (stepped.Phase == RunPhase.Crawling || stepped.Phase == RunPhase.AtBossDoor)
                {
                    RoomResult room = stepped.Phase == RunPhase.AtBossDoor ? stepped.FaceBoss(BossChoice.Fight) : stepped.RunFloor();
                    if (!room.AtBossDoor)
                        steppedRooms.Add(room);
                }

                Assert.Equal(rooms.Count, steppedRooms.Count);
                Assert.Equal(whole.Hp, stepped.Hp);
                Assert.Equal(whole.Haul, stepped.Haul);
                Assert.Equal(whole.Phase, stepped.Phase);
            }
        }
    }
}
