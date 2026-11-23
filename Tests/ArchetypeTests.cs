using Common.Services;
using DiscordBot;
using DiscordBot.Games.Models.Cards;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class ArchetypeTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        private sealed class Rolls : Random
        {
            private readonly double[] _values;
            private int _next;
            public Rolls(params double[] values) { _values = values; }
            public override double NextDouble() => _values[Math.Min(_next++, _values.Length - 1)];
        }

        private static (CoinService Coins, RaidService Raids, CoinAccount A, CoinAccount B) Setup(Archetype attacker, Archetype defender, params double[] rolls)
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            var a = new CoinAccount { UserId = 1, Name = "raider", NetWorth = 1_000_000, BattlePerson = RaidTests.AverageCard(5, attacker) };
            var b = new CoinAccount { UserId = 2, Name = "target", NetWorth = 1_000_000, BattlePerson = RaidTests.AverageCard(5, defender) };
            coins.GetAll().Accounts.Add(a);
            coins.GetAll().Accounts.Add(b);
            return (coins, new RaidService(coins, x => x.NetWorth - x.Debt, new Rolls(rolls)), a, b);
        }

        private static double Total(CoinService coins, params CoinAccount[] accounts) => accounts.Sum(a => a.NetWorth - a.Debt + a.Bounty) + coins.JackpotPool;

        [Fact]
        public void Counters_FormOneTriangle_AndTheOthersHaveNoCounter()
        {
            Assert.True(Archetypes.Counters(Archetype.Brute, Archetype.Thief));
            Assert.True(Archetypes.Counters(Archetype.Thief, Archetype.Guardian));
            Assert.True(Archetypes.Counters(Archetype.Guardian, Archetype.Brute));
            foreach (var a in Archetypes.Pullable)
            {
                Assert.False(Archetypes.Counters(a, a));
                foreach (var b in Archetypes.Pullable)
                    Assert.False(Archetypes.Counters(a, b) && Archetypes.Counters(b, a)); //nobody counters each other
            }
            foreach (var free in new[] { Archetype.Trickster, Archetype.Gambler })
            {
                Assert.Null(Archetypes.Beats(free));
                Assert.Null(Archetypes.WeakTo(free));
                Assert.All(Archetypes.Pullable, other => Assert.False(Archetypes.Counters(free, other) || Archetypes.Counters(other, free)));
            }
            foreach (var a in new[] { Archetype.Brute, Archetype.Thief, Archetype.Guardian })
                Assert.True(Archetypes.Counters(a, Archetypes.Beats(a).Value));
            Assert.Equal(Archetype.Guardian, Archetypes.Normalise(Archetype.Medic)); //the old Heal
        }

        [Fact]
        public void TheMatchup_ChangesPower_BothWays_AndIsShown()
        {
            var brute = RaidTests.AverageCard(5, Archetype.Brute);
            var thief = RaidTests.AverageCard(5, Archetype.Thief);
            var guardian = RaidTests.AverageCard(5, Archetype.Guardian);
            double basePower = brute.Power;

            Assert.Equal(basePower * Archetypes.BruteAttack * (1 + Archetypes.CounterBonus), RaidRules.AttackPower(brute, thief, false), 6);  //counters the thief
            Assert.Equal(basePower * Archetypes.BruteAttack, RaidRules.AttackPower(brute, guardian, false), 6);                              //the guardian counters it, which shows on its defence
            Assert.Equal(basePower * Archetypes.GuardianDefence * (1 + Archetypes.CounterBonus), RaidRules.DefencePower(guardian, brute, RaidStance.Counter, false, 1), 6);

            Assert.Contains("beats", RaidService.MatchupLine(brute, thief));
            Assert.Contains("for the raider", RaidService.MatchupLine(brute, thief));
            Assert.Contains("for the defender", RaidService.MatchupLine(brute, guardian));
            Assert.Equal("", RaidService.MatchupLine(RaidTests.AverageCard(5, Archetype.Gambler), brute));
        }

        [Fact]
        public void Thief_WinsPayMore_ButAFailedRaidCostsExtra_AndMoneyIsConserved()
        {
            var (coins, raids, a, b) = Setup(Archetype.Thief, Archetype.None, 0.0);
            double before = Total(coins, a, b);
            var won = raids.Raid(a, b, 10_000, Now);
            Assert.True(won.Won);
            Assert.Equal(10_000 * Archetypes.ThiefLootMultiple, won.Loot, 2);
            Assert.Equal(before, Total(coins, a, b), 6);

            var (coins2, raids2, a2, b2) = Setup(Archetype.Thief, Archetype.None, 0.99);
            double before2 = Total(coins2, a2, b2);
            var lost = raids2.Raid(a2, b2, 10_000, Now);
            Assert.False(lost.Won);
            Assert.Equal(10_000 * Archetypes.ThiefFailedRaidExtra, lost.ExtraLoss, 2);
            Assert.Equal(1_000_000 - 10_000 - lost.ExtraLoss, a2.NetWorth, 2);
            Assert.Equal(before2, Total(coins2, a2, b2), 6);
            Assert.Contains("getting caught", RaidService.Describe(lost, new Random(1)));
        }

        [Fact]
        public void Gambler_SwingsOnACoinFlip_AndNaturals_DoubleTheLootOrTheLoss()
        {
            //0.0 is hot (+25%) and a roll of 100; money stays conserved
            var (coins, raids, a, b) = Setup(Archetype.Gambler, Archetype.None, 0.0, 0.0);
            double before = Total(coins, a, b);
            var hot = raids.Raid(a, b, 10_000, Now);
            Assert.Equal(1 + Archetypes.GamblerSwing, hot.Swing, 6);
            Assert.Equal(100, hot.Roll);
            Assert.True(hot.Won);
            Assert.Equal(RaidRules.LootMultiple * 2, hot.LootMultiple, 6);   //a natural 100 doubles the usual 2x
            Assert.Equal(before, Total(coins, a, b), 6);
            Assert.Contains("NATURAL 100", RaidService.Describe(hot, new Random(1)));

            //0.9 is cold (-25%), and 0.9999 rolls a 1: snake eyes
            var (coins2, raids2, a2, b2) = Setup(Archetype.Gambler, Archetype.None, 0.9, 0.9999);
            double before2 = Total(coins2, a2, b2);
            var cold = raids2.Raid(a2, b2, 10_000, Now);
            Assert.Equal(1 - Archetypes.GamblerSwing, cold.Swing, 6);
            Assert.Equal(1, cold.Roll);
            Assert.False(cold.Won);
            Assert.Equal(10_000, cold.ExtraLoss, 2);                          //the loss is doubled
            Assert.Equal(before2, Total(coins2, a2, b2), 6);
            Assert.Contains("SNAKE EYES", RaidService.Describe(cold, new Random(1)));

            //hot is a better chance than cold, for the same cards
            Assert.True(hot.Check.Chance > cold.Check.Chance);
        }

        [Fact]
        public void Streak_CountsWinsInARow_AndALossResetsIt()
        {
            var (coins, raids, a, b) = Setup(Archetype.None, Archetype.None, 0.0);
            var c = new CoinAccount { UserId = 3, Name = "third", NetWorth = 1_000_000, BattlePerson = RaidTests.AverageCard(0) };
            var d = new CoinAccount { UserId = 4, Name = "fourth", NetWorth = 1_000_000, BattlePerson = RaidTests.AverageCard(0) };
            coins.GetAll().Accounts.AddRange(new[] { c, d });
            Assert.Equal(1, raids.Raid(a, b, 30_000, Now).Streak);
            Assert.Equal(2, raids.Raid(a, c, 30_000, Now.AddMinutes(1)).Streak);
            Assert.Equal(2, a.Stats.BestRaidStreak);
            var failing = new RaidService(coins, x => x.NetWorth - x.Debt, new Rolls(0.99));
            Assert.False(failing.Raid(a, d, 30_000, Now.AddMinutes(2)).Won);
            Assert.Equal(0, a.RaidStreak);
            Assert.Equal(2, a.Stats.BestRaidStreak);
        }

        /// <summary>What a dollar staked is expected to bring back net, from the rules' numbers (not counting a Trickster's once-a-day second roll).</summary>
        private static double ExpectedNetPerDollar(Archetype kind, double power, double defence, RaidStance stance = RaidStance.Counter)
        {
            var raider = RaidTests.AverageCard(0, kind);
            var target = RaidTests.AverageCard(0, Archetype.None);
            raider.Level = target.Level = 0;
            double p = RaidRules.WinChance(RaidRules.AttackPower(raider, target, false) * power / raider.Power, RaidRules.DefencePower(target, raider, stance, false, 1) * defence / target.Power);
            double loot = Archetypes.LootMultiple(kind, RaidRules.LootMultiple) * (1 - RaidRules.BountyShare);
            double lost = 1 + (kind == Archetype.Thief ? Archetypes.ThiefFailedRaidExtra : 0) + (stance == RaidStance.Counter ? RaidRules.CounterHitMultiple : 0);
            return p * loot - (1 - p) * lost;
        }

        [Fact]
        public void NoArchetype_IsAFreeLunch_OrAnAnchor()
        {
            //even cards against a plain defender: every archetype's expected result is within a narrow band of the others
            var results = Archetypes.Pullable.ToDictionary(k => k, k => ExpectedNetPerDollar(k, 1, 1, RaidStance.Guard));
            double plain = ExpectedNetPerDollar(Archetype.None, 1, 1, RaidStance.Guard);
            foreach (var pair in results)
                Assert.InRange(pair.Value - plain, -0.05, 0.2); //never worse than a rookie by much, never a big edge
            Assert.InRange(results.Values.Max() - results.Values.Min(), 0, 0.2);

            //and raiding up (a rich, well-defended target) is a losing bet for all of them, so nobody can farm it
            foreach (var kind in Archetypes.Pullable)
                Assert.True(ExpectedNetPerDollar(kind, 1, 1.5, RaidStance.Counter) < 0, $"{kind} should lose against a Counter that defends 50% harder");
        }

        [Fact]
        public void OldSavedCards_StillLoad_WithTheirKindKept()
        {
            //a card saved with the old stats, focus and ability numbers (3 was Reroll, 4 CritBoost, 1 Heal)
            string json = "{\"Name\":\"Old Card\",\"Level\":5,\"Xp\":40,\"Rarity\":2,\"Ability\":3,\"Foil\":true,\"Wins\":4,\"Losses\":2,\"Focus\":1," +
                          "\"Stats\":{\"Attack\":15.0,\"AttackBonusPerLevel\":1.5,\"Defense\":11.0,\"DefenseBonusPerLevel\":1.1,\"CritMultiplier\":0.3,\"CritMultiplierBonusPerLevel\":0.03}}";
            var card = JsonConvert.DeserializeObject<BattlePerson>(json);
            Assert.Equal(Archetype.Trickster, card.Archetype);
            Assert.Equal(CardRarity.Epic, card.Rarity);
            Assert.Equal(5, card.Level);
            Assert.True(card.Foil);
            Assert.Equal(CardLevels.PowerOf(CardRarity.Epic, 5), card.Power, 6);

            Assert.Equal(Archetype.Brute, JsonConvert.DeserializeObject<BattlePerson>("{\"Ability\":4}").Archetype);
            Assert.Equal(Archetype.Guardian, JsonConvert.DeserializeObject<BattlePerson>("{\"Ability\":1}").Archetype); //Heal became Guardian
            Assert.Equal(Archetype.Guardian, JsonConvert.DeserializeObject<BattlePerson>("{\"Ability\":2}").Archetype);

            var again = JsonConvert.DeserializeObject<BattlePerson>(JsonConvert.SerializeObject(card));
            Assert.Equal(card.Archetype, again.Archetype);
            Assert.Contains("\"Ability\":3", JsonConvert.SerializeObject(card)); //saved under the old name
        }

        [Fact]
        public void Pulls_FollowTheOddsAndNameThemselvesToMatch()
        {
            var random = new Random(77);
            var pulls = Enumerable.Range(0, 20_000).Select(_ => BattlePerson.Pull(random)).ToList();
            Assert.InRange(pulls.Count(p => p.Rarity == CardRarity.Legendary) / 20_000.0, 0.02, 0.04);
            Assert.InRange(pulls.Count(p => p.Rarity == CardRarity.Epic) / 20_000.0, 0.10, 0.14);
            Assert.InRange(pulls.Count(p => p.Foil) / 20_000.0, 0.04, 0.06);
            Assert.All(Archetypes.Pullable, kind => Assert.InRange(pulls.Count(p => p.Archetype == kind) / 20_000.0, 0.17, 0.23));
            Assert.All(pulls, p =>
            {
                Assert.InRange(p.Name.Length, 8, 45);
                Assert.Contains("\"", p.Name);
                Assert.Equal(0, p.Level);
            });
            Assert.True(pulls.Select(p => p.Name).Distinct().Count() > 5_000, "names should be varied");
        }

        [Fact]
        public void EveryArchetype_HasATraitAndCriesForBothOutcomes()
        {
            var random = new Random(5);
            foreach (var kind in Archetypes.Pullable.Concat(new[] { Archetype.None }))
            {
                Assert.False(string.IsNullOrWhiteSpace(Archetypes.Name(kind)));
                Assert.False(string.IsNullOrWhiteSpace(Archetypes.Trait(kind)));
                Assert.False(string.IsNullOrWhiteSpace(Archetypes.Cry(kind, true, random)));
                Assert.False(string.IsNullOrWhiteSpace(Archetypes.Cry(kind, false, random)));
            }
            Assert.Contains("15%", Archetypes.Trait(Archetype.Brute));   //numbers come from the constants
            Assert.Contains("2.5x", Archetypes.Trait(Archetype.Thief));
            Assert.Contains("25%", Archetypes.Trait(Archetype.Gambler));
        }

        [Fact]
        public void ARaidResult_ShowsMatchupCriesAndTheMoney()
        {
            var (_, raids, a, b) = Setup(Archetype.Brute, Archetype.Thief, 0.0);
            string text = RaidService.Describe(raids.Raid(a, b, 10_000, Now), new Random(3));
            Assert.Contains("beats", text);
            Assert.Contains("Took **", text);
            Assert.Contains("“", text); //a battle cry in quotes
            Assert.Contains("revenge", text);
        }

        [Fact]
        public void TheRaidPicture_IsDrawnAtItsSize_WithAndWithoutArt()
        {
            var (_, raids, a, b) = Setup(Archetype.Gambler, Archetype.Guardian, 0.0, 0.0);
            a.BattlePerson.FilePath = "/no/such/card.jpg";
            b.BattlePerson.FilePath = null;
            var scene = CommandManager.RaidScene(raids.Raid(a, b, 10_000, Now));
            using var png = RaidRenderer.Render(scene);
            using Image image = Image.Load(png.ToArray());
            Assert.Equal(RaidRenderer.Width, image.Width);
            Assert.Equal(RaidRenderer.Height, image.Height);
            Assert.Equal("RAID SUCCEEDED", scene.Banner);
            Assert.True(scene.Attacker.Winner);
            Assert.False(scene.Defender.Winner);
            Assert.Equal(new List<string> { "GARY KNUCKLES", "PEMBERTON" }, RaidRenderer.Wrap("GARY KNUCKLES PEMBERTON", 15, 3));
        }
    }
}
