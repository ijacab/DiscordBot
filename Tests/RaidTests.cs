using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models.Cards;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>Raids: crews robbing property. The odds, the money (conserved), the limits, revenge, and that property still pays.</summary>
    public class RaidTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A plain card at a level.</summary>
        public static BattlePerson AverageCard(int level, CardRarity rarity = CardRarity.Common, bool foil = false) => new BattlePerson
        {
            Name = $"card{level}",
            Level = level,
            Rarity = rarity,
            Foil = foil,
        };

        private static List<BattlePerson> Gang(int count, int level = 0) => Enumerable.Range(0, count).Select(_ => AverageCard(level)).ToList();

        private sealed class FixedRandom : Random
        {
            private readonly double[] _values;
            private int _next;
            public FixedRandom(params double[] values) { _values = values; }
            public override double NextDouble() => _values[Math.Min(_next++, _values.Length - 1)];
        }

        /// <summary>
        /// A raider with three level 0 cards and a target with three and a house, worth their cash plus property at the base index,
        /// and a raid service rolling <paramref name="rolls"/> (0 is a d100 of 100, 0.99 a 1).
        /// </summary>
        private static (CoinService Coins, RaidService Raids, CoinAccount A, CoinAccount B, PropertyHolding House) Setup(double aCash, double bCash, params double[] rolls)
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            coins.GetAll().PropertyIndex = 1.0;
            var a = new CoinAccount { UserId = 1, Name = "raider", NetWorth = aCash, Cards = Gang(3) };
            var b = new CoinAccount { UserId = 2, Name = "target", NetWorth = bCash, Cards = Gang(3) };
            var house = new PropertyHolding { Kind = PropertyKind.House };
            b.Properties.Add(house);
            coins.GetAll().Accounts.Add(a);
            coins.GetAll().Accounts.Add(b);
            var raids = new RaidService(coins, Worth, new FixedRandom(rolls.Length == 0 ? new[] { 0.5 } : rolls));
            return (coins, raids, a, b, house);
        }

        private static double Worth(CoinAccount x) => x.NetWorth - x.Debt + x.Properties.Sum(p => PropertyCatalog.SaleValue(p.Kind, 1.0));

        private static double Total(CoinService coins) => coins.GetAll().Accounts.Sum(x => x.NetWorth - x.Debt + x.Bounty);

        [Fact]
        public void Odds_ComeFromTheCrewAgainstSecurityPlusTheGang()
        {
            var crew = Gang(3, 10);
            double even = RaidRules.WinChance(RaidRules.AttackPower(crew, false), RaidRules.DefencePower(Gang(3, 10), 0, false, 1));
            Assert.Equal(0.5, even, 6);

            double house = RaidRules.WinChance(RaidRules.AttackPower(crew, false), RaidRules.DefencePower(Gang(3, 10), PropertyCatalog.TypeOf(PropertyKind.House).Security, false, 1));
            double penthouse = RaidRules.WinChance(RaidRules.AttackPower(crew, false), RaidRules.DefencePower(Gang(3, 10), PropertyCatalog.TypeOf(PropertyKind.Penthouse).Security, false, 1));
            double guarded = RaidRules.WinChance(RaidRules.AttackPower(crew, false), RaidRules.DefencePower(Gang(3, 10), PropertyCatalog.TypeOf(PropertyKind.House).Security, true, 1));
            Assert.True(house < even);
            Assert.True(penthouse < house);
            Assert.True(guarded < house);

            //a bigger, stronger crew does better, and an undefended food truck is easy
            Assert.True(RaidRules.WinChance(RaidRules.AttackPower(Gang(3, 30), false), RaidRules.DefencePower(Gang(3, 0), 60, false, 1)) > 0.6);
            Assert.InRange(RaidRules.WinChance(RaidRules.AttackPower(Gang(1, 0), false), RaidRules.DefencePower(new List<BattlePerson>(), PropertyCatalog.TypeOf(PropertyKind.FoodTruck).Security, false, 1)), 0.6, 0.8);
            Assert.Equal(RaidRules.MaxChance, RaidRules.WinChance(10_000, 1));
            Assert.Equal(RaidRules.MinChance, RaidRules.WinChance(1, 10_000));
        }

        [Fact]
        public void Foils_AndRarity_AddPower()
        {
            Assert.Equal(AverageCard(5).Power * (1 + GangRules.FoilBonus), AverageCard(5, foil: true).Power, 6);
            Assert.True(AverageCard(0, CardRarity.Legendary).Power > AverageCard(0, CardRarity.Epic).Power);
            Assert.Equal(AverageCard(3).Power * 3, RaidRules.TotalPower(Gang(3, 3)), 6);
        }

        [Fact]
        public void PunchingDown_IsHarder_AndPunchingUpIsnt()
        {
            Assert.Equal(1, RaidRules.PunchDownMultiplier(100, 1_000));
            Assert.Equal(1, RaidRules.PunchDownMultiplier(1_000, 1_000));
            Assert.Equal(1.2, RaidRules.PunchDownMultiplier(2_000, 1_000), 6);
            Assert.Equal(1 + RaidRules.MaxPunchDown, RaidRules.PunchDownMultiplier(1e12, 1_000));
            Assert.Equal(1 + RaidRules.MaxPunchDown, RaidRules.PunchDownMultiplier(1_000, 0));
        }

        [Fact]
        public void Loot_IsHoursOfProfit_CappedAtAFifthOfTheirWorth_AndTheStakeIsHalf()
        {
            double profit = PropertyCatalog.ProfitPerHour(PropertyKind.Mansion, 1.0);
            Assert.Equal(Math.Floor(profit * RaidRules.LootHours), RaidRules.Loot(PropertyKind.Mansion, 1.0, 1e9));
            Assert.Equal(1_000, RaidRules.Loot(PropertyKind.Mansion, 1.0, 5_000));
            Assert.Equal(0, RaidRules.Loot(PropertyKind.Mansion, 1.0, -5_000));
            Assert.Equal(500, RaidRules.Stake(1_000));
            Assert.Equal(1, RaidRules.Stake(0));
        }

        [Fact]
        public void EveryKind_StillBeatsABond_EvenRobbedAsOftenAsPossible()
        {
            //the worst case: one loot every protection window, every raid getting through and no failed stakes coming back
            foreach (var type in PropertyCatalog.Buyable)
            {
                double price = PropertyCatalog.Price(type.Kind, 1.0);
                double left = PropertyCatalog.ProfitPerHour(type.Kind, 1.0) * (1 - RaidRules.WorstCaseDrainShare) / price;
                Assert.True(left >= Stock.BondGrowthPerHour, $"{type.Name} keeps {left:P3} an hour");
            }
        }

        [Fact]
        public void Win_TakesTheLoot_SetsABounty_ProtectsTheProperty_AndConservesMoney()
        {
            var (coins, raids, a, b, house) = Setup(100_000, 100_000, 0.0);
            double before = Total(coins);
            var result = raids.Raid(a, b, house.Id, Now);

            Assert.True(result.Won);
            double loot = RaidRules.Loot(PropertyKind.House, 1.0, Worth(b) + result.Loot);
            Assert.Equal(loot, result.Loot, 2);
            Assert.Equal(100_000 - loot, b.NetWorth, 2);
            Assert.Equal(100_000 + loot * (1 - RaidRules.BountyShare), a.NetWorth, 2);
            Assert.Equal(loot * RaidRules.BountyShare, a.Bounty, 2);
            Assert.Equal(before, Total(coins), 2);
            Assert.Equal((Now + RaidRules.PropertyProtection).Ticks, house.RaidProtectedUntilUtcTicks);
            Assert.All(a.Cards, c => Assert.True(c.IsResting(Now)));
            Assert.All(a.Cards, c => Assert.Equal(CardLevels.RaidWinXp + CardLevels.RaidUpBonusXp, TotalXp(c))); //the target is richer (they own a house)
            Assert.All(b.Cards, c => Assert.Equal(CardLevels.DefenceLossXp, TotalXp(c)));
            Assert.Single(b.RevengeTokens);
        }

        [Fact]
        public void Loss_GivesTheStakeToTheOwner_AndConservesMoney()
        {
            var (coins, raids, a, b, house) = Setup(100_000, 100_000, 0.99);
            double before = Total(coins);
            var result = raids.Raid(a, b, house.Id, Now);

            Assert.False(result.Won);
            Assert.Equal(100_000 - result.Stake, a.NetWorth, 2);
            Assert.Equal(100_000 + result.Stake, b.NetWorth, 2);
            Assert.Equal(0, house.RaidProtectedUntilUtcTicks);
            Assert.Equal(before, Total(coins), 2);
        }

        [Fact]
        public void Win_TakesWhatCashCantCoverFromDebt_UnlessAParachuteOpens()
        {
            var (coins, raids, a, b, house) = Setup(100_000, 100, 0.0);
            var result = raids.Raid(a, b, house.Id, Now);
            Assert.Equal(100, result.Loot - result.LootToDebt, 2);
            Assert.True(result.LootToDebt > 0);
            Assert.Equal(result.LootToDebt, b.Debt, 2);

            var (_, raids2, x, y, house2) = Setup(100_000, 100, 0.0);
            y.ActivePowerUps.Add(new ActivePowerUp { Id = PowerUpIds.GoldenParachute, ExpiresAtTicks = Now.AddHours(1).Ticks, ChargesLeft = 1 });
            var saved = raids2.Raid(x, y, house2.Id, Now);
            Assert.True(saved.Parachuted);
            Assert.Equal(100, saved.Loot, 2);
            Assert.Equal(0, y.Debt);
        }

        [Fact]
        public void Limits_RestingCrews_HourlyCap_PairCooldown_AndPropertyProtection()
        {
            var (coins, raids, a, b, house) = Setup(1_000_000, 100_000, 0.99);
            raids.Raid(a, b, house.Id, Now);
            //the whole crew is lying low
            var check = raids.Check(a, b, house, Now.AddMinutes(1));
            Assert.Contains("lying low", check.Refusal);

            //a bigger gang sends another crew
            a.Cards.AddRange(Gang(6));
            var c = new CoinAccount { UserId = 3, Name = "other", NetWorth = 100_000 };
            var shack = new PropertyHolding { Kind = PropertyKind.Shack };
            c.Properties.Add(shack);
            coins.GetAll().Accounts.Add(c);
            Assert.Null(raids.Check(a, c, shack, Now.AddMinutes(1)).Refusal);
            Assert.Contains("recently", raids.Check(a, b, house, Now.AddMinutes(1)).Refusal); //the same target within 6 hours

            raids.Raid(a, c, shack.Id, Now.AddMinutes(1));
            shack.RaidProtectedUntilUtcTicks = 0;
            a.LastRaidOnUtcTicks.Clear();
            raids.Raid(a, c, shack.Id, Now.AddMinutes(2));
            a.LastRaidOnUtcTicks.Clear();
            a.Cards.ForEach(x => x.RestedUntilUtcTicks = 0);
            Assert.Contains("3 times in the last hour", raids.Check(a, c, shack, Now.AddMinutes(3)).Refusal);

            //a robbed place is watched
            var (_, raids2, x, y, house2) = Setup(1_000_000, 100_000, 0.0);
            raids2.Raid(x, y, house2.Id, Now);
            x.Cards.ForEach(card => card.RestedUntilUtcTicks = 0);
            x.LastRaidOnUtcTicks.Clear();
            Assert.Contains("watching", raids2.Check(x, y, house2, Now.AddHours(1)).Refusal);
            Assert.DoesNotContain("watching", raids2.Check(x, y, house2, Now.AddHours(9)).Refusal ?? "");
        }

        [Fact]
        public void Refusals_SelfNoGangNoPropertyShieldAndNoStake()
        {
            var (coins, raids, a, b, house) = Setup(100_000, 100_000);
            Assert.Contains("yourself", raids.Check(a, a, null, Now).Refusal);
            var broke = new CoinAccount { UserId = 9, Name = "broke", NetWorth = 0, Cards = Gang(1) };
            Assert.Contains("stake", raids.Check(broke, b, house, Now).Refusal);
            Assert.Contains("gang", raids.Check(new CoinAccount { UserId = 8, Name = "nobody", NetWorth = 1_000 }, b, house, Now).Refusal);
            Assert.Contains("owns no property", raids.Check(b, a, null, Now).Refusal);
            b.ActivePowerUps.Add(new ActivePowerUp { Id = PowerUpIds.Shield, ExpiresAtTicks = Now.AddHours(1).Ticks, ChargesLeft = ActivePowerUp.Unlimited });
            Assert.Contains("Shield", raids.Check(a, b, house, Now).Refusal);
            Assert.Throws<BadInputException>(() => raids.Raid(a, b, house.Id, Now));
            Assert.Throws<BadInputException>(() => raids.Raid(a, b, "not-a-property", Now));
        }

        [Fact]
        public void Revenge_IgnoresProtectionAndTheGap_IsStronger_AndWorksOnce()
        {
            var (coins, raids, a, b, house) = Setup(1_000_000, 1_000_000, 0.0);
            var aHouse = new PropertyHolding { Kind = PropertyKind.House };
            a.Properties.Add(aHouse);
            raids.Raid(a, b, house.Id, Now);
            aHouse.RaidProtectedUntilUtcTicks = Now.AddHours(5).Ticks; //as if it had just been robbed

            var check = raids.Check(b, a, aHouse, Now.AddMinutes(1));
            Assert.True(check.IsRevenge);
            Assert.Null(check.Refusal);
            Assert.Equal(RaidRules.TotalPower(b.Cards) * (1 + RaidRules.RevengeBonus), check.AttackPower, 6);
            var back = raids.Raid(b, a, aHouse.Id, Now.AddMinutes(1));
            Assert.True(back.Check.IsRevenge);
            Assert.Empty(b.RevengeTokens);
            Assert.Empty(a.RevengeTokens); //no token back for a revenge raid
        }

        [Fact]
        public void RaidingSomeoneRicher_GivesMoreXp()
        {
            var (_, raids, a, b, house) = Setup(10_000, 5_000_000, 0.0);
            raids.Raid(a, b, house.Id, Now);
            Assert.All(a.Cards, c => Assert.Equal(CardLevels.RaidWinXp + CardLevels.RaidUpBonusXp, TotalXp(c)));

            var (_, raids2, x, y, house2) = Setup(50_000_000, 100, 0.0);
            raids2.Raid(x, y, house2.Id, Now);
            Assert.All(x.Cards, c => Assert.Equal(CardLevels.RaidWinXp, TotalXp(c)));
        }

        /// <summary>All the XP a card has earned, counting its levels.</summary>
        private static int TotalXp(BattlePerson card) => Enumerable.Range(0, card.Level).Sum(CardLevels.XpToNext) + card.Xp;

        [Fact]
        public void Guards_CostTwoPercentIntoTheJackpot_AndBookUpTo24HoursAhead()
        {
            var (coins, raids, a, _, _) = Setup(100_000, 100_000);
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
        public void Roll_IsAD100WhereHigherWins_AndTheTextShowsIt()
        {
            Assert.Equal(41, RaidService.NeededRoll(0.60));
            Assert.Equal(96, RaidService.NeededRoll(0.05));

            var (_, raids, a, b, house) = Setup(1_000_000, 1_000_000, 0.91); //rolls a 9
            var lost = raids.Raid(a, b, house.Id, Now);
            Assert.Equal(9, lost.Roll);
            Assert.False(lost.Won);
            string text = RaidService.Describe(lost, new Random(1));
            Assert.Contains($"Needed **{lost.Needed}+** ({RaidService.ChancePercent(lost.Check.Chance)}%), rolled **9** ❌", text);
            Assert.Contains("keeps the", text);
            Assert.Contains("House", text);
            Assert.True(text.Split('\n').Length <= 5);

            var (_, raids2, x, y, house2) = Setup(1_000_000, 1_000_000, 0.0); //rolls a 100
            var won = raids2.Raid(x, y, house2.Id, Now);
            Assert.True(won.Won);
            string wonText = RaidService.Describe(won, new Random(1));
            Assert.Contains("Took", wonText);
            Assert.Contains("bounty", wonText);
            Assert.Contains("revenge", wonText);
            Assert.True(wonText.Split('\n').Length <= 5);
        }

        [Fact]
        public void TheRaidPicture_ShowsTheCrewAgainstTheProperty()
        {
            var (_, raids, a, b, house) = Setup(1_000_000, 1_000_000, 0.0);
            var scene = DiscordBot.Managers.CommandManager.RaidScene(raids.Raid(a, b, house.Id, Now));
            Assert.Equal("CREW OF 3", scene.Attacker.Kind);
            Assert.Equal("House", scene.Defender.Kind);
            using var picture = RaidRenderer.Render(scene);
            Assert.True(picture.Length > 0);
        }
    }
}
