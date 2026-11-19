using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.BattleArena;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class RaidTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A card with the average of every rolled stat (common rarity), at a level.</summary>
        public static BattlePerson AverageCard(int level, CardAbility ability = CardAbility.Heal) => new BattlePerson
        {
            Name = $"card{level}",
            Level = level,
            Ability = ability,
            Stats = new BattleStats
            {
                Attack = 15, AttackBonusPerLevel = 1.5,
                Defense = 11.25, DefenseBonusPerLevel = 1.125,
                CritMultiplier = 0.3, CritMultiplierBonusPerLevel = 0.03,
            },
        };

        private sealed class FixedRandom : Random
        {
            private readonly double[] _values;
            private int _next;
            public FixedRandom(params double[] values) { _values = values; }
            public override double NextDouble() => _values[Math.Min(_next++, _values.Length - 1)];
        }

        /// <summary>Two players worth only their cash, both with average level 0 cards, and a raid service rolling <paramref name="rolls"/>.</summary>
        private static (CoinService Coins, RaidService Raids, CoinAccount A, CoinAccount B) Setup(double aCash, double bCash, params double[] rolls)
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            var a = new CoinAccount { UserId = 1, Name = "raider", NetWorth = aCash, BattlePerson = AverageCard(0) };
            var b = new CoinAccount { UserId = 2, Name = "target", NetWorth = bCash, BattlePerson = AverageCard(0) };
            coins.GetAll().Accounts.Add(a);
            coins.GetAll().Accounts.Add(b);
            var raids = new RaidService(coins, x => x.NetWorth - x.Debt, new FixedRandom(rolls.Length == 0 ? new[] { 0.5 } : rolls));
            return (coins, raids, a, b);
        }

        private static double Total(CoinService coins, params CoinAccount[] accounts) =>
            accounts.Sum(a => a.NetWorth - a.Debt + a.Bounty) + coins.JackpotPool;

        [Fact]
        public void Odds_Level30BeatsAFreshCardAboutThreeTimesInFour()
        {
            double chance = RaidRules.WinChance(RaidRules.AttackPower(AverageCard(30), false),
                RaidRules.DefencePower(AverageCard(0), RaidStance.Counter, false, 1));
            Assert.InRange(chance, 0.72, 0.80);

            double even = RaidRules.WinChance(RaidRules.AttackPower(AverageCard(10), false), RaidRules.DefencePower(AverageCard(10), RaidStance.Counter, false, 1));
            Assert.Equal(0.5, even, 3);
        }

        [Fact]
        public void Odds_GuardGuardsAndBeingRicherAllMakeARaidHarder()
        {
            double attack = RaidRules.AttackPower(AverageCard(10), false);
            var card = AverageCard(10);
            double counter = RaidRules.WinChance(attack, RaidRules.DefencePower(card, RaidStance.Counter, false, 1));
            double guard = RaidRules.WinChance(attack, RaidRules.DefencePower(card, RaidStance.Guard, false, 1));
            double guarded = RaidRules.WinChance(attack, RaidRules.DefencePower(card, RaidStance.Guard, true, 1));
            double richer = RaidRules.WinChance(attack, RaidRules.DefencePower(card, RaidStance.Guard, true, RaidRules.UnderdogMultiplier(1_000, 64_000)));
            Assert.True(counter > guard && guard > guarded && guarded > richer);
            Assert.True(RaidRules.WinChance(attack, RaidRules.DefencePower(card, RaidStance.Hoard, false, 1)) > counter);

            Assert.Equal(1, RaidRules.UnderdogMultiplier(5_000, 1_000));
            Assert.Equal(1.4, RaidRules.UnderdogMultiplier(1_000, 4_000), 6);
            Assert.Equal(2, RaidRules.UnderdogMultiplier(1, 1e12), 6);
        }

        [Fact]
        public void Balance_EqualCardsAgainstGuardAreOnlyMildlyWorthRaiding()
        {
            //what one dollar of stake returns on average, after the bounty is set aside
            double ExpectedPerStake(RaidStance stance, bool guards, double underdog)
            {
                double p = RaidRules.WinChance(RaidRules.AttackPower(AverageCard(5), false), RaidRules.DefencePower(AverageCard(5), stance, guards, underdog));
                double loot = RaidRules.LootMultiple * (stance == RaidStance.Hoard ? RaidRules.HoardLootShare : 1) * (1 - RaidRules.BountyShare);
                double loss = 1 + (stance == RaidStance.Counter ? RaidRules.CounterHitMultiple : 0);
                return p * loot - (1 - p) * loss;
            }

            Assert.InRange(ExpectedPerStake(RaidStance.Guard, false, 1), 0.05, 0.3);  //worth trying, not a money printer
            Assert.True(ExpectedPerStake(RaidStance.Counter, false, 1) < 0);           //counter punishes raiders
            Assert.True(ExpectedPerStake(RaidStance.Guard, true, 1) < 0);              //guards make it a bad bet
            Assert.True(ExpectedPerStake(RaidStance.Guard, false, RaidRules.UnderdogMultiplier(1_000, 4_000)) < 0); //raiding up needs a better card
        }

        [Fact]
        public void Win_TakesTwiceTheStake_SetsABountyAndConservesMoney()
        {
            var (coins, raids, a, b) = Setup(10_000, 100_000, 0.0);
            double before = Total(coins, a, b);

            var result = raids.Raid(a, b, 1_000, Now);

            Assert.True(result.Won);
            Assert.Equal(2_000, result.Loot);
            Assert.Equal(98_000, b.NetWorth);
            Assert.Equal(200, a.Bounty);
            Assert.Equal(10_000 + 2_000 - 200, a.NetWorth);
            Assert.Equal(before, Total(coins, a, b), 6);
            Assert.True(b.RaidProtectedUntilUtcTicks > Now.Ticks);
            Assert.Contains(b.RevengeTokens, t => t.UserId == a.UserId);
            Assert.Equal(1, a.Stats.RaidsWon);
            Assert.Equal(1, b.Stats.DefencesLost);
        }

        [Fact]
        public void Loss_GivesTheStakeToTheDefender_AndCounterHitsBack()
        {
            var (coins, raids, a, b) = Setup(10_000, 100_000, 0.99);
            b.RaidStance = RaidStance.Counter;
            a.Bounty = 300;
            double before = Total(coins, a, b);

            var result = raids.Raid(a, b, 1_000, Now);

            Assert.False(result.Won);
            Assert.Equal(1_000, result.CounterHit);
            Assert.Equal(300, result.BountyCollected);
            Assert.Equal(10_000 - 2_000, a.NetWorth);
            Assert.Equal(100_000 + 2_000 + 300, b.NetWorth);
            Assert.Equal(0, a.Bounty);
            Assert.Equal(before, Total(coins, a, b), 6);
        }

        [Fact]
        public void Win_TakesWhatCashCantCoverFromDebt_UnlessAParachuteOpens()
        {
            var (coins, raids, a, b) = Setup(10_000, 1_000, 0.0);
            b.Properties.Clear();
            //the target is worth 50,000 but holds only 1,000 in cash
            var raidsWorth = new RaidService(coins, x => x.UserId == 2 ? 50_000 : x.NetWorth - x.Debt, new FixedRandom(0.0));

            var result = raidsWorth.Raid(a, b, 4_000, Now);

            Assert.Equal(8_000, result.Loot);
            Assert.Equal(7_000, result.LootToDebt);
            Assert.Equal(7_000, b.Debt);
            Assert.Equal(0, b.NetWorth);

            var (coins2, _, a2, b2) = Setup(10_000, 1_000, 0.0);
            b2.Grant(PowerUpIds.GoldenParachute, Now);
            b2.Start(PowerUpCatalog.Find(PowerUpIds.GoldenParachute), Now);
            var parachuted = new RaidService(coins2, x => x.UserId == 2 ? 50_000 : x.NetWorth - x.Debt, new FixedRandom(0.0)).Raid(a2, b2, 4_000, Now);
            Assert.True(parachuted.Parachuted);
            Assert.Equal(1_000, parachuted.Loot);
            Assert.Equal(0, b2.Debt);
        }

        [Fact]
        public void Loot_HalvedByHoard_AndNeverOverAFifthOfTheirWorth()
        {
            Assert.Equal(2_000, RaidRules.Loot(1_000, RaidStance.Guard, 1_000_000));
            Assert.Equal(1_000, RaidRules.Loot(1_000, RaidStance.Hoard, 1_000_000));
            Assert.Equal(2_000, RaidRules.Loot(5_000, RaidStance.Guard, 10_000));
        }

        [Fact]
        public void Stakes_AreAtLeastOnePercentOfYoursAndAtMostTenPercentOfTheirs()
        {
            var (_, raids, a, b) = Setup(100_000, 50_000);
            Assert.Throws<BadInputException>(() => raids.Raid(a, b, 999, Now));
            Assert.Throws<BadInputException>(() => raids.Raid(a, b, 5_001, Now));

            //much richer players can't pick on the poor: their smallest stake is more than the most anyone can stake on them
            var (_, raids2, rich, poor) = Setup(10_000_000, 50_000);
            Assert.Contains("too small", raids2.Check(rich, poor, Now).Refusal);
        }

        [Fact]
        public void Limits_HourlyCapPairCooldownAndProtection()
        {
            var (coins, raids, a, b) = Setup(1_000_000, 1_000_000, 0.99);
            var c = new CoinAccount { UserId = 3, Name = "third", NetWorth = 1_000_000, BattlePerson = AverageCard(0) };
            var d = new CoinAccount { UserId = 4, Name = "fourth", NetWorth = 1_000_000, BattlePerson = AverageCard(0) };
            var e = new CoinAccount { UserId = 5, Name = "fifth", NetWorth = 1_000_000, BattlePerson = AverageCard(0) };
            coins.GetAll().Accounts.AddRange(new[] { c, d, e });

            raids.Raid(a, b, 10_000, Now);
            raids.Raid(a, c, 10_000, Now.AddMinutes(1));
            raids.Raid(a, d, 10_000, Now.AddMinutes(2));
            Assert.Contains("3 times", raids.Check(a, e, Now.AddMinutes(30)).Refusal);
            Assert.Null(raids.Check(a, e, Now.AddMinutes(61)).Refusal);

            Assert.Contains("recently", raids.Check(a, b, Now.AddHours(5)).Refusal);
            Assert.Null(raids.Check(a, b, Now.AddHours(6)).Refusal);

            //a successful raid protects the target from everyone else for an hour
            var (_, raids2, x, y) = Setup(1_000_000, 1_000_000, 0.0);
            y.BattlePerson.Ability = CardAbility.CritBoost; //Heal would protect for 2 hours
            raids2.Raid(x, y, 10_000, Now);
            var z = new CoinAccount { UserId = 9, Name = "z", NetWorth = 1_000_000, BattlePerson = AverageCard(0) };
            Assert.Contains("protected", raids2.Check(z, y, Now.AddMinutes(30)).Refusal);
            Assert.Null(raids2.Check(z, y, Now.AddMinutes(61)).Refusal);
        }

        [Fact]
        public void Revenge_IgnoresProtectionAndTheGap_IsStronger_AndWorksOnce()
        {
            var (_, raids, a, b) = Setup(1_000_000, 100_000, 0.0, 0.0);
            raids.Raid(a, b, 10_000, Now);
            b.RaidProtectedUntilUtcTicks = 0;
            a.RaidProtectedUntilUtcTicks = Now.AddHours(1).Ticks; //as if someone else just raided the raider

            var check = raids.Check(b, a, Now.AddMinutes(5));
            Assert.Null(check.Refusal);
            Assert.True(check.IsRevenge);
            Assert.Equal(1, check.Underdog); //a is 10x richer, but revenge ignores it
            Assert.Equal(RaidRules.Power(b.BattlePerson) * 1.25, check.AttackPower, 6);

            var result = raids.Raid(b, a, 10_000, Now.AddMinutes(5));
            Assert.True(result.Won);
            Assert.True(result.BountyCollected > 0); //a's bounty from the first raid
            Assert.Empty(b.RevengeTokens);
            Assert.DoesNotContain(a.RevengeTokens, t => t.UserId == b.UserId); //no revenge for revenge
            Assert.False(raids.Check(b, a, Now.AddMinutes(10)).IsRevenge);
        }

        [Fact]
        public void Refusals_ShieldSelfAndNoCard()
        {
            var (_, raids, a, b) = Setup(100_000, 100_000);
            Assert.Contains("yourself", raids.Check(a, a, Now).Refusal);
            b.Start(PowerUpCatalog.Find(PowerUpIds.Shield), Now);
            Assert.Contains("Shield", raids.Check(a, b, Now).Refusal);
            Assert.Throws<BadInputException>(() => raids.Raid(a, b, 2_000, Now));
            Assert.Equal(100_000, a.NetWorth); //no stake taken

            a.BattlePerson = null;
            Assert.Contains("cardpull", raids.Check(a, b, Now).Refusal);
        }

        [Fact]
        public void Raids_GiveXp_MoreForRaidingSomeoneRicher()
        {
            var (_, raids, a, b) = Setup(10_000, 100_000, 0.0);
            raids.Raid(a, b, 1_000, Now);
            Assert.Equal(CardLevels.RaidWinXp + CardLevels.RaidUpBonusXp - CardLevels.XpToNext(0), a.BattlePerson.Xp);
            Assert.Equal(1, a.BattlePerson.Level);
            Assert.Equal(CardLevels.DefenceLossXp, b.BattlePerson.Xp);
        }

        [Fact]
        public void Abilities_RerollOnceADayAndHealProtectsLonger()
        {
            var (_, raids, a, b) = Setup(1_000_000, 1_000_000, 0.99, 0.0);
            a.BattlePerson.Ability = CardAbility.Reroll;
            b.BattlePerson.Ability = CardAbility.CritBoost;
            var result = raids.Raid(a, b, 10_000, Now);
            Assert.True(result.Rerolled);
            Assert.True(result.Won);
            Assert.Equal(RaidRules.Protection, result.ProtectedFor);

            var (_, raids2, x, y) = Setup(1_000_000, 1_000_000, 0.0);
            y.BattlePerson.Ability = CardAbility.Heal;
            Assert.Equal(RaidRules.HealProtection, raids2.Raid(x, y, 10_000, Now).ProtectedFor);
        }

        [Fact]
        public void Guards_CostTwoPercentIntoTheJackpot_AndBookUpTo24HoursAhead()
        {
            var (coins, raids, a, _) = Setup(100_000, 100_000);
            double jackpot = coins.JackpotPool;
            Assert.Equal(2_000, raids.HireGuards(a, Now));
            Assert.Equal(98_000, a.NetWorth);
            Assert.Equal(jackpot + 2_000, coins.JackpotPool, 6);
            Assert.Equal(Now.AddHours(6).Ticks, a.GuardsUntilUtcTicks);
            raids.HireGuards(a, Now);
            raids.HireGuards(a, Now);
            raids.HireGuards(a, Now);
            Assert.Equal(Now.AddHours(24).Ticks, a.GuardsUntilUtcTicks);
            Assert.Throws<BadInputException>(() => raids.HireGuards(a, Now));
        }

        [Fact]
        public void Describe_NamesTheResult()
        {
            var (_, raids, a, b) = Setup(10_000, 100_000, 0.0);
            string text = RaidService.Describe(raids.Raid(a, b, 1_000, Now));
            Assert.Contains("Raid succeeded", text);
            Assert.Contains("$2,000", text);
            Assert.Contains("revenge", text);
        }
    }
}
