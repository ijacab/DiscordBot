using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.Cards;
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

        /// <summary>A common card at a level, with an archetype (none by default, so it has no trait).</summary>
        public static BattlePerson AverageCard(int level, Archetype kind = Archetype.None, CardRarity rarity = CardRarity.Common) => new BattlePerson
        {
            Name = $"card{level}",
            Level = level,
            Archetype = kind,
            Rarity = rarity,
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
            double chance = RaidRules.WinChance(RaidRules.AttackPower(AverageCard(30), AverageCard(0), false),
                RaidRules.DefencePower(AverageCard(0), AverageCard(30), RaidStance.Counter, false, 1));
            Assert.InRange(chance, 0.72, 0.80);

            double even = RaidRules.WinChance(RaidRules.AttackPower(AverageCard(10), AverageCard(10), false), RaidRules.DefencePower(AverageCard(10), AverageCard(10), RaidStance.Counter, false, 1));
            Assert.Equal(0.5, even, 3);
        }

        [Fact]
        public void Odds_GuardGuardsAndBeingRicherAllMakeARaidHarder()
        {
            double attack = RaidRules.AttackPower(AverageCard(10), AverageCard(10), false);
            var card = AverageCard(10);
            double counter = RaidRules.WinChance(attack, RaidRules.DefencePower(card, card, RaidStance.Counter, false, 1));
            double guard = RaidRules.WinChance(attack, RaidRules.DefencePower(card, card, RaidStance.Guard, false, 1));
            double guarded = RaidRules.WinChance(attack, RaidRules.DefencePower(card, card, RaidStance.Guard, true, 1));
            double richer = RaidRules.WinChance(attack, RaidRules.DefencePower(card, card, RaidStance.Guard, true, RaidRules.UnderdogMultiplier(1_000, 64_000)));
            Assert.True(counter > guard && guard > guarded && guarded > richer);
            Assert.True(RaidRules.WinChance(attack, RaidRules.DefencePower(card, card, RaidStance.Hoard, false, 1)) > counter);

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
                double p = RaidRules.WinChance(RaidRules.AttackPower(AverageCard(5), AverageCard(5), false), RaidRules.DefencePower(AverageCard(5), AverageCard(5), stance, guards, underdog));
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
            Assert.Equal(RaidRules.Power(b.BattlePerson) * 1.25, check.AttackPower, 6); //no archetypes, so only revenge's bonus

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
        public void Trickster_RerollsOneFailedRaidADay_AndGuardianProtectionLastsLonger()
        {
            var (_, raids, a, b) = Setup(1_000_000, 1_000_000, 0.99, 0.0);
            a.BattlePerson.Archetype = Archetype.Trickster;
            var result = raids.Raid(a, b, 10_000, Now);
            Assert.True(result.Rerolled);
            Assert.True(result.Won);
            Assert.Equal(RaidRules.Protection, result.ProtectedFor);

            var (_, raids2, x, y) = Setup(1_000_000, 1_000_000, 0.0);
            y.BattlePerson.Archetype = Archetype.Guardian;
            Assert.Equal(Archetypes.GuardianProtection, raids2.Raid(x, y, 10_000, Now).ProtectedFor);
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
        public void Roll_IsAD100WhereHigherWins_AndShowsTheNumberNeeded()
        {
            Assert.Equal(41, RaidService.NeededRoll(0.60));
            Assert.Equal(96, RaidService.NeededRoll(0.05));
            Assert.Equal(6, RaidService.NeededRoll(0.95));

            var (_, raids, a, b) = Setup(1_000_000, 1_000_000, 0.91); //rolls a 9
            var lost = raids.Raid(a, b, 10_000, Now);
            Assert.Equal(9, lost.Roll);
            Assert.False(lost.Won);
            string text = RaidService.Describe(lost);
            Assert.Contains($"Needed **{lost.Needed}+** ({RaidService.ChancePercent(lost.Check.Chance)}%), rolled **9** ❌", text);

            var (_, raids2, x, y) = Setup(1_000_000, 1_000_000, 0.0); //rolls a 100
            var won = raids2.Raid(x, y, 10_000, Now);
            Assert.Equal(100, won.Roll);
            Assert.True(won.Won);
            Assert.Contains("rolled **100** ✅", RaidService.Describe(won));
        }

        [Fact]
        public void Describe_NamesTheResult()
        {
            var (_, raids, a, b) = Setup(10_000, 100_000, 0.0);
            string text = RaidService.Describe(raids.Raid(a, b, 1_000, Now));
            Assert.Contains("Took **$2,000**", text);
            Assert.Contains("revenge", text);
        }

        [Fact]
        public void Describe_StaysShort_ForWinsLossesAndRevenge()
        {
            var (_, raids, a, b) = Setup(10_000, 100_000, 0.0);
            var win = raids.Raid(a, b, 1_000, Now);
            string winText = RaidService.Describe(win);
            Assert.InRange(winText.Split('\n').Length, 4, 6);
            Assert.Contains("rolled **100** ✅", winText);
            Assert.DoesNotContain("Raid succeeded", winText); //the embed title and the picture say it

            var (_, raids2, x, y) = Setup(1_000_000, 1_000_000, 0.99);
            string lossText = RaidService.Describe(raids2.Raid(x, y, 10_000, Now));
            Assert.InRange(lossText.Split('\n').Length, 3, 5);
            Assert.Contains("Lost the $10,000 stake", lossText);

            //a revenge raid has no further revenge note
            var (_, raids3, p, q) = Setup(1_000_000, 1_000_000, 0.0);
            raids3.Raid(q, p, 10_000, Now);
            var revenge = raids3.Raid(p, q, 10_000, Now.AddSeconds(5));
            string revengeText = RaidService.Describe(revenge);
            Assert.StartsWith("🔥 **Revenge raid!**", revengeText);
            Assert.DoesNotContain("can take revenge", revengeText);
            Assert.True(revengeText.Length < 1000);
        }

        [Fact]
        public void ARichVictim_CanAlwaysHitBackAtAPoorRaider_ButOnlyUpToTheirStake()
        {
            //the raider has $10,000 and the victim $10,000,000: the normal limits (1% of the victim's worth up to 10% of the raider's) would refuse the revenge
            var (coins, raids, poor, rich) = Setup(10_000, 10_000_000, 0.0, 0.0);
            Assert.True(raids.Raid(poor, rich, 1_000, Now).Won);   //the poor player's raid works
            rich.RaidProtectedUntilUtcTicks = 0;
            double richCash = rich.NetWorth;

            var normal = RaidRules.StakeLimits(rich.NetWorth, poor.NetWorth);
            Assert.True(normal.Min > normal.Max, "the ordinary limits really would have refused it");

            var check = raids.Check(rich, poor, Now.AddMinutes(5));
            Assert.Null(check.Refusal);
            Assert.True(check.IsRevenge);
            Assert.Equal(1_000, check.MaxStake);                    //what the poor raider staked
            Assert.InRange(check.MinStake, 1, 1_000);

            Assert.Throws<DiscordBot.Exceptions.BadInputException>(() => raids.Raid(rich, poor, 1_001, Now.AddMinutes(5)));
            double before = Total(coins, poor, rich);
            var result = raids.Raid(rich, poor, 1_000, Now.AddMinutes(5));
            Assert.True(result.Won);
            Assert.True(result.Loot <= 2_000, "the loot is at most twice the capped stake");
            Assert.Equal(before, Total(coins, poor, rich), 6);
            Assert.Equal(richCash - 1_000 + 1_000 + result.Loot - result.BountySet + result.BountyCollected, rich.NetWorth, 2);
            Assert.Empty(rich.RevengeTokens);                       //and nothing to take revenge for the revenge
        }

        [Fact]
        public void Revenge_StillRespectsTheLootCapAndTheHourlyLimit_AndOldTokensStillWork()
        {
            //a tiny target: a big revenge stake still never takes more than 20% of what they have
            var (coins, raids, tiny, big) = Setup(1_000, 1_000_000, 0.0);
            big.RevengeTokens.Add(new RevengeToken { UserId = 1, Name = "raider", ExpiresUtcTicks = Now.AddHours(1).Ticks, Stake = 50_000 });
            double before = Total(coins, tiny, big);
            var result = raids.Raid(big, tiny, 50_000, Now);
            Assert.True(result.Won);
            Assert.Equal(1_000 * RaidRules.MaxLootShare, result.Loot, 2);
            Assert.True(tiny.NetWorth >= 800 - 1e-6);
            Assert.Equal(before, Total(coins, tiny, big), 6);

            //a token saved before stakes were recorded falls back to the normal limits instead of breaking
            var (_, raids2, x, y) = Setup(1_000_000, 1_000_000, 0.0);
            x.RevengeTokens.Add(new RevengeToken { UserId = 2, Name = "target", ExpiresUtcTicks = Now.AddHours(1).Ticks });
            var old = raids2.Check(x, y, Now);
            Assert.Null(old.Refusal);
            Assert.Equal(RaidRules.StakeLimits(1_000_000, 1_000_000).Max, old.MaxStake);

            //the raids-an-hour limit still applies to revenge
            var (coins3, raids3, p, q) = Setup(1_000_000, 1_000_000, 0.99);
            var extras = Enumerable.Range(10, 3).Select(id => new CoinAccount { UserId = (ulong)id, Name = $"e{id}", NetWorth = 1_000_000, BattlePerson = AverageCard(0) }).ToList();
            coins3.GetAll().Accounts.AddRange(extras);
            foreach (var e in extras) raids3.Raid(p, e, 10_000, Now);
            p.RevengeTokens.Add(new RevengeToken { UserId = 2, Name = "target", ExpiresUtcTicks = Now.AddHours(1).Ticks, Stake = 5_000 });
            Assert.Contains("3 times", raids3.Check(p, q, Now.AddMinutes(10)).Refusal);
        }
    }
}
