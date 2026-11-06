using DiscordBot.Games.Models.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tests
{
    public class PvpArenaTests
    {
        private static PvpFighter Fighter(ulong id, double haul, int hp = 30, int attack = 4, int defense = 1)
        {
            return new PvpFighter
            {
                UserId = id,
                Name = $"F{id}",
                Haul = haul,
                Stats = new Combatant { Name = $"F{id}", Hp = hp, MaxHp = hp, Attack = attack, Defense = defense }
            };
        }

        [Fact]
        public void Pot_IsTheSumOfEntrantsHauls()
        {
            var entrants = new List<PvpFighter> { Fighter(1, 300), Fighter(2, 500), Fighter(3, 200) };

            Assert.Equal(1000, PvpArena.Resolve(entrants, new Random(1)).Pot, 2);
        }

        [Fact]
        public void Payouts_NeverExceedThePot_AndAlwaysAddUpToIt()
        {
            for (int seed = 1; seed <= 200; seed++)
            {
                var entrants = new List<PvpFighter> { Fighter(1, 300), Fighter(2, 500), Fighter(3, 200), Fighter(4, 100) };

                PvpResult result = PvpArena.Resolve(entrants, new Random(seed));

                Assert.InRange(result.Payouts.Values.Sum(), result.Pot - 0.01, result.Pot + 0.01);
                Assert.All(result.Payouts.Keys, id => Assert.Contains(id, entrants.Select(e => e.UserId)));
            }
        }

        [Fact]
        public void TwoFighters_OneStandingTakesEverything()
        {
            // one side is far stronger, so the other goes down in the first few rounds
            var strong = Fighter(1, 400, hp: 500, attack: 50);
            var weak = Fighter(2, 600, hp: 5, attack: 1, defense: 0);

            PvpResult result = PvpArena.Resolve(new List<PvpFighter> { strong, weak }, new Random(2));

            Assert.Single(result.Payouts);
            Assert.Equal(1000, result.Payouts[1], 2);
            Assert.Equal(new[] { "F1" }, result.Winners);
        }

        [Fact]
        public void Fights_NeverGoPastTheTenRoundCap()
        {
            var roundNumber = new Regex(@"^Round (\d+):");
            for (int seed = 1; seed <= 200; seed++)
            {
                // tough fighters with no real damage, so the fight reaches the cap
                var entrants = new List<PvpFighter>
                {
                    Fighter(1, 100, hp: 100000, attack: 0, defense: 1000),
                    Fighter(2, 100, hp: 100000, attack: 0, defense: 1000),
                    Fighter(3, 100, hp: 100000, attack: 0, defense: 1000),
                };

                PvpResult result = PvpArena.Resolve(entrants, new Random(seed));

                int highest = result.Lines.Select(l => roundNumber.Match(l)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
                Assert.True(highest <= PvpArena.MaxRounds, $"round {highest} played with seed {seed}");
                Assert.Contains(result.Lines, l => l.Contains($"round limit of {PvpArena.MaxRounds}"));
            }
        }

        [Fact]
        public void Resolve_EndsWithExactlyOneOutcome()
        {
            var entrants = new List<PvpFighter> { Fighter(1, 100), Fighter(2, 100), Fighter(3, 100), Fighter(4, 100) };

            PvpResult result = PvpArena.Resolve(entrants, new Random(9));

            Assert.Equal(1, result.Lines.Count(l => l.Contains("last one standing")) + result.Lines.Count(l => l.Contains("round limit")));
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

        [Fact]
        public void AttackBonus_IsAppliedToEveryFighter()
        {
            var entrants = new List<PvpFighter> { Fighter(1, 100, attack: 4), Fighter(2, 100, attack: 4) };

            PvpArena.Resolve(entrants, new Random(10));

            Assert.All(entrants, e => Assert.Equal(4 + PvpArena.AttackBonus, e.Stats.Attack));
        }
    }
}
