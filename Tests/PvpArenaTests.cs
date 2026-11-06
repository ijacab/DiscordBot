using DiscordBot.Games.Models.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    public class PvpArenaTests
    {
        private static PvpFighter Fighter(ulong id, double haul, int hp = 30, int attack = 4, int defense = 1)
        {
            return new PvpFighter { UserId = id, Name = $"F{id}", Hp = hp, Attack = attack, Defense = defense, Haul = haul };
        }

        [Fact]
        public void Pot_IsTheSumOfEntrantsHauls()
        {
            var entrants = new List<PvpFighter> { Fighter(1, 300), Fighter(2, 500), Fighter(3, 200) };

            PvpResult result = PvpArena.Resolve(entrants, new Random(1));

            Assert.Equal(1000, result.Pot, 2);
        }

        [Fact]
        public void Winner_TakesTheWholePot_AndPayoutsNeverExceedThePot()
        {
            for (int seed = 1; seed <= 200; seed++)
            {
                var entrants = new List<PvpFighter> { Fighter(1, 300), Fighter(2, 500), Fighter(3, 200), Fighter(4, 100) };

                PvpResult result = PvpArena.Resolve(entrants, new Random(seed));

                Assert.True(result.Payouts.Values.Sum() <= result.Pot + 0.001, $"paid out more than the pot with seed {seed}");
                Assert.InRange(result.Payouts.Values.Sum(), result.Pot - 0.01, result.Pot + 0.01);
                Assert.All(result.Payouts.Keys, id => Assert.Contains(id, entrants.Select(e => e.UserId)));
            }
        }

        [Fact]
        public void TwoFighters_OneSurvivesAndTakesEverything()
        {
            // one-sided stats so the outcome is decided quickly
            var strong = Fighter(1, 400, hp: 60, attack: 50);
            var weak = Fighter(2, 600, hp: 5, attack: 1, defense: 0);

            PvpResult result = PvpArena.Resolve(new List<PvpFighter> { strong, weak }, new Random(2));

            Assert.Single(result.Payouts);
            Assert.Equal(1000, result.Payouts[1], 2);
            Assert.False(result.Payouts.ContainsKey(2));
        }

        [Fact]
        public void TieAtTheRoundCap_SplitsThePot()
        {
            // no damage can get through, so everyone is still standing at the cap with equal HP
            var a = Fighter(1, 500, hp: 1000, attack: 0, defense: 1000);
            var b = Fighter(2, 500, hp: 1000, attack: 0, defense: 1000);

            PvpResult result = PvpArena.Resolve(new List<PvpFighter> { a, b }, new Random(3));

            Assert.Equal(500, result.Payouts[1], 2);
            Assert.Equal(500, result.Payouts[2], 2);
            Assert.Contains(result.Lines, l => l.Contains(PvpArena.MaxRounds.ToString()));
        }

        [Fact]
        public void HighestHpWinsAtTheRoundCap()
        {
            // the first fighter takes 1 damage per hit, the second takes 0, so after 10 rounds the second has more HP
            var tough = Fighter(1, 500, hp: 1000, attack: 0, defense: 1000);
            var sturdy = Fighter(2, 500, hp: 1000, attack: 0, defense: 1000);
            tough.Hp = 1000;
            sturdy.Hp = 1001;

            PvpResult result = PvpArena.Resolve(new List<PvpFighter> { tough, sturdy }, new Random(4));

            Assert.Single(result.Payouts);
            Assert.Equal(1000, result.Payouts[2], 2);
        }

        [Fact]
        public void SingleEntrant_KeepsTheirOwnHaul()
        {
            PvpResult result = PvpArena.Resolve(new List<PvpFighter> { Fighter(9, 750) }, new Random(5));

            Assert.Equal(750, result.Payouts[9], 2);
        }

        [Fact]
        public void NoEntrants_PaysNothing()
        {
            PvpResult result = PvpArena.Resolve(new List<PvpFighter>(), new Random(6));

            Assert.Empty(result.Payouts);
            Assert.Equal(0, result.Pot);
        }
    }

    public class LootBoxTests
    {
        [Fact]
        public void Cost_IsTenPercentOfTheBuyIn()
        {
            Assert.Equal(100, LootBox.Cost(1000), 2);
        }

        [Fact]
        public void Roll_MatchesTheStatedTierOdds()
        {
            var random = new Random(42);
            int trials = 20000;
            var counts = new Dictionary<LootTier, int> { [LootTier.Common] = 0, [LootTier.Rare] = 0, [LootTier.Epic] = 0 };
            for (int i = 0; i < trials; i++)
                counts[LootBox.Roll(random).Tier]++;

            Assert.InRange((double)counts[LootTier.Common] / trials, 0.57, 0.63);
            Assert.InRange((double)counts[LootTier.Rare] / trials, 0.27, 0.33);
            Assert.InRange((double)counts[LootTier.Epic] / trials, 0.08, 0.12);
        }

        [Fact]
        public void Roll_GivesNonNegativeBoosts()
        {
            var random = new Random(7);
            for (int i = 0; i < 1000; i++)
            {
                LootResult loot = LootBox.Roll(random);

                Assert.True(loot.AttackGain >= 0 && loot.DefenseGain >= 0 && loot.MaxHpGain >= 0);
                Assert.False(string.IsNullOrEmpty(loot.Name));
            }
        }
    }
}
