using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    public class MarketCycleTests
    {
        private const long Day = 20000;
        private const double Tolerance = 1e-6;

        private static Stock Listing(string symbol, int tier, double price, StockRole role = StockRole.Normal) => new Stock
        {
            Symbol = symbol,
            RiskTier = tier,
            Role = role,
            Price = price,
            PreviousPrice = price,
            BasePrice = price,
            FairPrice = price,
        };

        private static void AssertClose(double expected, double actual, double tolerance) =>
            Assert.InRange(actual, expected - tolerance, expected + tolerance);

        /// <summary>Total log move a stock takes over a whole phase, given its per-tick drift.</summary>
        private static double PhaseLogMove(MarketDrift drift, int days) => drift.LogPerTick * days * Stock.TicksPerDay;

        [Fact]
        public void AdvanceDay_StartsADeclineOnADailyRoll()
        {
            var data = new CoinAccounts();

            string headline = MarketCycle.AdvanceDay(data, Day, () => 0);

            Assert.Equal(CrisisPhase.Decline, data.MarketPhase);
            Assert.Equal(MarketCycle.DeclineDays, data.MarketPhaseDaysLeft);
            Assert.Equal(Day + MarketCycle.CooldownDays, data.NextCrisisAllowedDay);
            Assert.NotNull(headline);
        }

        [Fact]
        public void AdvanceDay_NoCrashWithoutTheRoll()
        {
            var data = new CoinAccounts();

            Assert.Null(MarketCycle.AdvanceDay(data, Day, () => 0.99));
            Assert.Equal(CrisisPhase.None, data.MarketPhase);
        }

        [Fact]
        public void AdvanceDay_CooldownStopsAnotherCrashEvenOnALuckyRoll()
        {
            var data = new CoinAccounts { NextCrisisAllowedDay = Day + 100 };

            Assert.Null(MarketCycle.AdvanceDay(data, Day, () => 0));
            Assert.Equal(CrisisPhase.None, data.MarketPhase);
        }

        [Fact]
        public void AdvanceDay_WalksThroughDeclineBottomRecoveryAndEnds()
        {
            var data = new CoinAccounts();
            MarketCycle.AdvanceDay(data, Day, () => 0);

            for (int i = 0; i < MarketCycle.DeclineDays; i++) MarketCycle.AdvanceDay(data, Day + 1 + i, () => 0.99);
            Assert.Equal(CrisisPhase.Bottom, data.MarketPhase);

            for (int i = 0; i < MarketCycle.BottomDays; i++) MarketCycle.AdvanceDay(data, Day + 100 + i, () => 0.99);
            Assert.Equal(CrisisPhase.Recovery, data.MarketPhase);

            for (int i = 0; i < MarketCycle.RecoveryDays; i++) MarketCycle.AdvanceDay(data, Day + 200 + i, () => 0.99);
            Assert.Equal(CrisisPhase.None, data.MarketPhase);
            Assert.True(data.CrisisResetPending);
        }

        [Fact]
        public void Drift_OrdinaryStockLosesItsShareOverTheDecline()
        {
            var stock = Listing("MID", 3, 100);

            var drift = MarketCycle.DriftFor(stock, CrisisPhase.Decline);

            Assert.Equal(1 - MarketCycle.FallFor(stock), Math.Exp(PhaseLogMove(drift, MarketCycle.DeclineDays)), 6);
            Assert.False(drift.AllowReversion);
        }

        [Fact]
        public void Drift_FallsAreBiggerForRiskierTiersAndCrypto()
        {
            Assert.True(MarketCycle.FallFor(Listing("A", 1, 10)) < MarketCycle.FallFor(Listing("B", 5, 10)));
            Assert.Equal(MarketCycle.CryptoFall, MarketCycle.FallFor(Listing("C", Stock.CryptoTier, 10)));
        }

        [Fact]
        public void Drift_GoldRisesInTheDeclineAndKeepsItsBase()
        {
            var gold = Listing("GOLD", 2, 150, StockRole.SafeHaven);

            var decline = MarketCycle.DriftFor(gold, CrisisPhase.Decline);
            Assert.Equal(1 + MarketCycle.GoldRise, Math.Exp(PhaseLogMove(decline, MarketCycle.DeclineDays)), 6);
            Assert.False(decline.AllowReversion); //the drift alone carries it up, or the old base would pull it back

            Assert.Equal(MarketDrift.None.LogPerTick, MarketCycle.DriftFor(gold, CrisisPhase.Recovery).LogPerTick);
        }

        [Fact]
        public void Drift_BondsIgnoreTheCrisis()
        {
            var bond = Listing("BOND", 1, 100, StockRole.Bond);

            Assert.Equal(0, MarketCycle.DriftFor(bond, CrisisPhase.Decline).LogPerTick);
            Assert.Equal(0, MarketCycle.DriftFor(bond, CrisisPhase.Recovery).LogPerTick);
        }

        [Fact]
        public void Drift_BottomHasWiderSwingsAndNoTrend()
        {
            var stock = Listing("MID", 3, 100);

            var bottom = MarketCycle.DriftFor(stock, CrisisPhase.Bottom);

            Assert.Equal(0, bottom.LogPerTick);
            Assert.Equal(MarketCycle.BottomSwingScale, bottom.SwingScale);
        }

        [Fact]
        public void Drift_RecoveryGivesBackTheShareOfWhatWasLost()
        {
            var stock = Listing("MID", 3, 100);

            var decline = MarketCycle.DriftFor(stock, CrisisPhase.Decline);
            var recovery = MarketCycle.DriftFor(stock, CrisisPhase.Recovery);

            Assert.Equal(-MarketCycle.RecoveryShare * PhaseLogMove(decline, MarketCycle.DeclineDays),
                PhaseLogMove(recovery, MarketCycle.RecoveryDays), 6);
        }

        [Fact]
        public void ApplyTick_ADeclineActuallyPullsACheapStockDown()
        {
            //the old cent rounding used to swallow these small moves, so check a full decline through the tick
            var stock = Listing("CHEAP", 5, 10);
            var drift = MarketCycle.DriftFor(stock, CrisisPhase.Decline);

            for (int i = 0; i < MarketCycle.DeclineDays * Stock.TicksPerDay; i++)
                stock.ApplyTick(() => 0.5, drift);

            AssertClose(10 * (1 - MarketCycle.FallFor(stock)), stock.FairPrice, 0.05);
        }

        [Fact]
        public void ApplyTick_GoldRallies_ThroughADecline()
        {
            var gold = Listing("GOLD", 2, 150, StockRole.SafeHaven);
            var drift = MarketCycle.DriftFor(gold, CrisisPhase.Decline);

            for (int i = 0; i < MarketCycle.DeclineDays * Stock.TicksPerDay; i++)
                gold.ApplyTick(() => 0.5, drift);

            AssertClose(150 * (1 + MarketCycle.GoldRise), gold.FairPrice, 0.5);
        }

        [Fact]
        public void ApplyTick_BondsGrowASmallYieldEachDay()
        {
            var bond = Listing("BOND", 1, 100, StockRole.Bond);

            for (int i = 0; i < Stock.TicksPerDay; i++)
                bond.ApplyTick(() => 0.5);

            AssertClose(100 * Math.Exp(Stock.BondGrowthPerDay), bond.FairPrice, 0.001);
        }

        [Fact]
        public void ResetBasePrices_HitStocksKeepTheirNewLevelButGoldKeepsItsBase()
        {
            var hit = Listing("HIT", 3, 100);
            hit.FairPrice = 70;
            var gold = Listing("GOLD", 2, 150, StockRole.SafeHaven);
            gold.FairPrice = 170;

            MarketCycle.ResetBasePrices(new[] { hit, gold });

            Assert.Equal(70, hit.BasePrice);
            Assert.Equal(150, gold.BasePrice);
        }

        [Fact]
        public void StepProperty_DeclineTakesTheIndexDownAndRecoveryPutsSomeBack()
        {
            double index = 1.0;
            for (int i = 0; i < MarketCycle.DeclineDays; i++) MarketCycle.StepProperty(ref index, CrisisPhase.Decline);
            Assert.Equal(MarketCycle.PropertyDeclineTotal, index, 6);

            for (int i = 0; i < MarketCycle.RecoveryDays; i++) MarketCycle.StepProperty(ref index, CrisisPhase.Recovery);
            Assert.Equal(MarketCycle.PropertyDeclineTotal * MarketCycle.PropertyRecoveryTotal, index, 6);
        }

        [Fact]
        public void StepProperty_StaysInsideTheIndexClamp()
        {
            double index = PropertyCatalog.IndexMin + 0.01;
            for (int i = 0; i < 100; i++) MarketCycle.StepProperty(ref index, CrisisPhase.Decline);
            Assert.Equal(PropertyCatalog.IndexMin, index, 6);
        }

        [Fact]
        public void Market_HasGoldAndBondsAndTheyAreNotDuplicated()
        {
            var market = StockMarket.CreateDefault(new Random(1));
            Assert.Equal(StockRole.SafeHaven, market.Find("GOLD").Role);
            Assert.Equal(StockRole.Bond, market.Find("BOND").Role);

            int before = market.Stocks.Count;
            market.EnsureSafeHavens();
            Assert.Equal(before, market.Stocks.Count);
        }

        [Fact]
        public void Market_OldSavesGetTheSafeHavensAdded()
        {
            var market = StockMarket.CreateDefault(new Random(1));
            market.Stocks.RemoveAll(s => s.Role != StockRole.Normal);

            market.EnsureSafeHavens();

            Assert.NotNull(market.Find("GOLD"));
            Assert.NotNull(market.Find("BOND"));
        }
    }
}
