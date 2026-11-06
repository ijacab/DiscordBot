using DiscordBot.Games.Models.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    public class LootTests
    {
        [Fact]
        public void Drop_Chances_AreWhatTheyAreMeantToBe()
        {
            Assert.Equal(0.35, Loot.MonsterDropChance, 2);
            Assert.Equal(0.60, Loot.TreasureDropChance, 2);
        }

        [Fact]
        public void Roll_MatchesTheStatedTierOdds()
        {
            var random = new Random(42);
            int trials = 20000;
            var counts = new Dictionary<LootTier, int> { [LootTier.Common] = 0, [LootTier.Rare] = 0, [LootTier.Epic] = 0 };
            for (int i = 0; i < trials; i++)
                counts[Loot.Roll(random).Tier]++;

            Assert.InRange((double)counts[LootTier.Common] / trials, 0.57, 0.63);
            Assert.InRange((double)counts[LootTier.Rare] / trials, 0.27, 0.33);
            Assert.InRange((double)counts[LootTier.Epic] / trials, 0.08, 0.12);
        }

        [Fact]
        public void BossDrop_IsNeverCommon()
        {
            var random = new Random(7);
            for (int i = 0; i < 2000; i++)
                Assert.NotEqual(LootTier.Common, Loot.Roll(random, bossDrop: true).Tier);
        }

        [Fact]
        public void EffectCount_GrowsWithTier()
        {
            var random = new Random(8);
            for (int i = 0; i < 2000; i++)
            {
                LootItem item = Loot.Roll(random);
                int expected = item.Tier == LootTier.Common ? 1 : item.Tier == LootTier.Rare ? 2 : 3;
                Assert.Equal(expected, item.Effects.Count);
            }
        }

        [Fact]
        public void Effects_AreDistinctWithinAnItem()
        {
            var random = new Random(9);
            for (int i = 0; i < 2000; i++)
            {
                LootItem item = Loot.Roll(random);
                Assert.Equal(item.Effects.Count, item.Effects.Select(e => e.Effect).Distinct().Count());
            }
        }

        [Fact]
        public void RareOnlyEffects_NeverAppearOnCommonItems()
        {
            var random = new Random(10);
            for (int i = 0; i < 4000; i++)
            {
                LootItem item = Loot.Roll(random);
                if (item.Tier != LootTier.Common) continue;
                foreach (LootEffectRoll roll in item.Effects)
                    Assert.True(Loot.MinTier(roll.Effect) <= LootTier.Common);
            }
        }

        [Fact]
        public void Effects_HaveVariedKinds()
        {
            var random = new Random(11);
            var seen = new HashSet<LootEffect>();
            for (int i = 0; i < 3000; i++)
                foreach (LootEffectRoll roll in Loot.Roll(random).Effects)
                    seen.Add(roll.Effect);

            Assert.Equal(Enum.GetValues(typeof(LootEffect)).Length, seen.Count);
        }

        [Fact]
        public void Describe_IsReadableForEveryEffect()
        {
            foreach (LootEffect effect in Enum.GetValues(typeof(LootEffect)))
            {
                var roll = new LootEffectRoll { Effect = effect, Amount = 5 };
                Assert.False(string.IsNullOrWhiteSpace(roll.Describe()));
                Assert.Contains("5", roll.Describe());
            }
        }

        [Fact]
        public void Names_AreNeverEmpty()
        {
            var random = new Random(12);
            for (int i = 0; i < 500; i++)
                Assert.False(string.IsNullOrWhiteSpace(Loot.Roll(random).Name));
        }
    }
}
