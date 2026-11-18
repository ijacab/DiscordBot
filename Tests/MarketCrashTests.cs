using Common.Services;
using DiscordBot;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>The GFC: how likely it is, how bad it can be, and the patch notes / channels it is announced in.</summary>
    public class MarketCrashTests
    {
        private const long Day = 20000;

        private static Stock Listing(string symbol, int tier, StockRole role = StockRole.Normal, double price = 100) => new Stock
        {
            Symbol = symbol, RiskTier = tier, Role = role, Price = price, PreviousPrice = price, BasePrice = price, FairPrice = price,
        };

        // ---- chance

        [Fact]
        public void Chance_IsTheBaseWithNothingHeldAndFiveTimesThatWithALot()
        {
            Assert.Equal(MarketCycle.DailyChance, MarketCycle.ChanceFor(0), 9);
            Assert.Equal(MarketCycle.DailyChance * 5, MarketCycle.ChanceFor(MarketCycle.FullExposureShare), 9);
            Assert.Equal(MarketCycle.DailyChance * 5, MarketCycle.ChanceFor(1), 9); //more than that adds nothing
            Assert.Equal(MarketCycle.DailyChance * 3, MarketCycle.ChanceFor(MarketCycle.FullExposureShare / 2), 9);
        }

        [Theory]
        [InlineData(0, 100, 0)]
        [InlineData(30, 100, 0.3)]
        [InlineData(0, 0, 0)]
        [InlineData(150, 100, 1)]
        public void Exposure_IsTheRiskyShareOfWealth(double risky, double total, double expected) =>
            Assert.Equal(expected, MarketCycle.ExposureOf(risky, total), 9);

        [Fact]
        public void AdvanceDay_OnlyAsksForExposureOnADayARollIsMade()
        {
            int asked = 0;
            double Exposure() { asked++; return 0.6; }

            var cooling = new CoinAccounts { NextCrisisAllowedDay = Day + 5 };
            MarketCycle.AdvanceDay(cooling, Day, () => 0, Exposure);
            Assert.Equal(0, asked);

            var data = new CoinAccounts();
            Assert.Null(MarketCycle.AdvanceDay(data, Day, () => 0.3, Exposure)); //0.3 is above a 25% chance
            Assert.Equal(1, asked);
        }

        [Fact]
        public void AdvanceDay_MoreHoldingsMeansMoreCrashes()
        {
            Assert.Null(MarketCycle.AdvanceDay(new CoinAccounts(), Day, () => 0.2, () => 0)); //5% chance
            Assert.NotNull(MarketCycle.AdvanceDay(new CoinAccounts(), Day, () => 0.2, () => 0.6)); //25% chance
        }

        [Fact]
        public void Crashes_ComeAboutOnceASeasonAtBaseAndMoreOftenWhenInvested()
        {
            int Starts(double exposure)
            {
                var random = new Random(7);
                var data = new CoinAccounts();
                int starts = 0;
                for (long day = Day; day < Day + 36500; day++)
                {
                    var before = data.MarketPhase;
                    MarketCycle.AdvanceDay(data, day, random.NextDouble, () => exposure);
                    if (before == CrisisPhase.None && data.MarketPhase == CrisisPhase.Decline) starts++;
                }
                return starts;
            }

            int none = Starts(0), full = Starts(0.6);
            Assert.InRange(36500.0 / none, 22, 33); //about 4 weeks between crashes: a 7 day cycle and a 1 in 20 daily roll
            Assert.InRange(36500.0 / full, 8, 15); //about 11 days when everyone is invested
            Assert.True(full > none * 1.2, $"{full} vs {none}");
        }

        // ---- who holds what

        private static (StockService Stocks, CoinService Coins, CoinAccount Account) Market(double cash)
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            var account = new CoinAccount { UserId = 4_000, Name = "holder", NetWorth = cash };
            coins.GetAll().Accounts.Add(account);
            var stocks = new StockService(new LocalFileService(), coins);
            stocks.GetMarket().Portfolios.Remove(account.UserId); //the stock save file keeps holdings from earlier runs
            return (stocks, coins, account);
        }

        [Fact]
        public void RiskyAssetShare_CountsOrdinaryStocksAndHousesButNotGoldOrBonds()
        {
            var (stocks, coins, account) = Market(1_000_000);
            Assert.Equal(0, stocks.RiskyAssetShare());

            stocks.Buy(account, "GOLD", "100");
            stocks.Buy(account, "BOND", "100");
            Assert.Equal(0, stocks.RiskyAssetShare(), 9); //safe havens add nothing

            string ordinary = stocks.GetMarket().Stocks.First(s => s.Role == StockRole.Normal).Symbol;
            stocks.Buy(account, ordinary, "50");
            double withStock = stocks.RiskyAssetShare();
            Assert.True(withStock > 0);

            coins.GetAll().PropertyIndex = 1;
            account.Properties.Add(new PropertyHolding { Kind = PropertyKind.House });
            Assert.True(stocks.RiskyAssetShare() > withStock);
        }

        // ---- severity

        [Theory]
        [InlineData(3, 0.38, 0.70)]
        [InlineData(4, 0.44, 0.80)]
        [InlineData(5, 0.50, 0.90)]
        [InlineData(6, 0.60, 0.99)]
        public void Fall_IsTodaysAtSeverityZeroAndTheWorstAtOne(int tier, double mild, double worst)
        {
            var stock = Listing("X", tier);
            Assert.Equal(mild, MarketCycle.FallFor(stock, 0), 9);
            Assert.Equal(worst, MarketCycle.FallFor(stock, 1), 9);
            Assert.Equal((mild + worst) / 2, MarketCycle.FallFor(stock, 0.5), 9);
        }

        [Theory]
        [InlineData(0.01)]
        [InlineData(0.07)]
        [InlineData(0.30)]
        [InlineData(0.60)]
        public void NoiseDrag_IsTheAverageLogMoveOfOneTick(double swing)
        {
            //integrate ln(1 + swing * u) over u in [-1, 1)
            const int steps = 200_000;
            double sum = 0;
            for (int i = 0; i < steps; i++) sum += Math.Log(1 + swing * ((i + 0.5) / steps * 2 - 1));
            Assert.Equal(sum / steps, Stock.NoiseDrag(swing), 6);
            Assert.True(Stock.NoiseDrag(swing) < 0);
        }

        [Theory]
        [InlineData(3, 0)]
        [InlineData(3, 1)]
        [InlineData(4, 0.25)]
        [InlineData(5, 0)]
        [InlineData(5, 0.5)]
        public void ADecline_FallsByItsStatedAmount_AndTheSpreadStaysNarrow(int tier, double severity)
        {
            //before this, a mild decline took a tier 3 stock down about 65% and a tier 5 stock to the floor, with some paths ending far above the start
            var random = new Random(1234);
            const int paths = 1500;
            var stock = Listing("X", tier, price: 100_000);
            var drift = MarketCycle.DriftFor(stock, CrisisPhase.Decline, severity);
            var ends = new double[paths];
            for (int p = 0; p < paths; p++)
            {
                var s = Listing("X", tier, price: 100_000);
                for (int t = 0; t < MarketCycle.DeclineDays * Stock.TicksPerDay; t++)
                    s.ApplyTick(random.NextDouble, drift);
                ends[p] = Math.Log(s.FairPrice / 100_000);
            }
            Array.Sort(ends);
            double expected = Math.Log(1 - MarketCycle.FallFor(stock, severity));
            Assert.InRange(ends[paths / 2], expected - 0.1, expected + 0.1); //the typical stock lands on the stated fall
            Assert.True(ends[paths * 95 / 100] < Math.Log(1.5), "a crash left a typical stock's good luck far above where it started");
        }

        [Fact]
        public void TheBottom_HasNoTrend_EvenWithDoubleSwings()
        {
            var random = new Random(99);
            var stock = Listing("X", 5);
            var bottom = MarketCycle.DriftFor(stock, CrisisPhase.Bottom, 0);
            var ends = new double[800];
            for (int p = 0; p < ends.Length; p++)
            {
                var s = Listing("X", 5, price: 100_000);
                for (int t = 0; t < MarketCycle.BottomDays * Stock.TicksPerDay; t++) s.ApplyTick(random.NextDouble, bottom);
                ends[p] = Math.Log(s.FairPrice / 100_000);
            }
            Array.Sort(ends);
            Assert.InRange(ends[ends.Length / 2], -0.15, 0.15); //the typical stock ends about where it started
        }

        [Fact]
        public void TheCrisisAnchor_FollowsTheDriftThenClearsWhenTradingIsNormalAgain()
        {
            var stock = Listing("X", 4, price: 100);
            var decline = MarketCycle.DriftFor(stock, CrisisPhase.Decline, 0);
            var random = new Random(5);

            stock.ApplyTick(random.NextDouble, decline);
            Assert.InRange(stock.CrisisAnchor, 99, 100); //started at the price and moved by one tick of drift
            for (int i = 0; i < 100; i++) stock.ApplyTick(random.NextDouble, decline);
            Assert.True(stock.CrisisAnchor < 99);

            stock.ApplyTick(random.NextDouble, MarketDrift.None);
            Assert.Equal(0, stock.CrisisAnchor);
        }

        [Fact]
        public void Crypto_HasNoAnchor_ItFollowsTheDriftAlone()
        {
            var coin = Listing("COIN", Stock.CryptoTier, price: 100);
            coin.ApplyTick(new Random(1).NextDouble, MarketCycle.DriftFor(coin, CrisisPhase.Decline, 0));
            Assert.Equal(0, coin.CrisisAnchor);
        }

        [Fact]
        public void PropertyFall_RunsFromAQuarterToSeventyPercent()
        {
            Assert.Equal(0.25, MarketCycle.PropertyFall(0), 9);
            Assert.Equal(0.70, MarketCycle.PropertyFall(1), 9);

            double index = 1;
            for (int i = 0; i < MarketCycle.DeclineDays; i++) MarketCycle.StepProperty(ref index, CrisisPhase.Decline, 1);
            Assert.Equal(0.30, index, 6);
            for (int i = 0; i < MarketCycle.RecoveryDays; i++) MarketCycle.StepProperty(ref index, CrisisPhase.Recovery, 1);
            Assert.Equal(Math.Pow(0.30, 1 - MarketCycle.RecoveryShare), index, 6); //most of it comes back, not all
        }

        [Fact]
        public void WorstCrash_TakesCryptoDownNinetyNinePercentAndRecoveryReturnsMostOfIt()
        {
            var crypto = Listing("COIN", Stock.CryptoTier);
            double Total(CrisisPhase phase, int days) => MarketCycle.DriftFor(crypto, phase, 1).LogPerTick * days * Stock.TicksPerDay;

            Assert.Equal(0.01, Math.Exp(Total(CrisisPhase.Decline, MarketCycle.DeclineDays)), 6);
            double after = Math.Exp(Total(CrisisPhase.Decline, MarketCycle.DeclineDays) + Total(CrisisPhase.Recovery, MarketCycle.RecoveryDays));
            Assert.InRange(after, 0.45, 0.55);
        }

        [Fact]
        public void Severity_IsRolledWhenACrashStarts_AndMostCrashesAreModerate()
        {
            var data = new CoinAccounts();
            var draws = new System.Collections.Generic.Queue<double>(new[] { 0.0, 0.5 });
            MarketCycle.AdvanceDay(data, Day, draws.Dequeue);
            Assert.Equal(0.25, data.CrisisSeverity, 9);

            double[] severities = Enumerable.Range(0, 1000).Select(i => { double r = i / 1000.0; return r * r; }).ToArray();
            Assert.True(severities.Count(s => s < 0.5) > 650); //more mild than harsh
        }

        [Fact]
        public void Headline_SaysHowBadItIs()
        {
            Assert.Contains("recession", MarketCycle.FreefallHeadline(0.1));
            Assert.Contains("freefall", MarketCycle.FreefallHeadline(0.4));
            Assert.Contains("wiped out", MarketCycle.FreefallHeadline(0.9));
        }

        [Fact]
        public void AdvanceDay_CutsAPhaseSavedUnderTheOldLengths()
        {
            var data = new CoinAccounts { MarketPhase = CrisisPhase.Recovery, MarketPhaseDaysLeft = 100 };
            MarketCycle.AdvanceDay(data, Day, () => 0.99);
            Assert.Equal(MarketCycle.RecoveryDays - 1, data.MarketPhaseDaysLeft);
        }

        [Fact]
        public void ACrashAndItsRecoveryFitInsideASeason()
        {
            Assert.True(MarketCycle.DeclineDays + MarketCycle.BottomDays + MarketCycle.RecoveryDays <= 10);
        }

        [Fact]
        public void PropertySwings_LastDaysSoTheyShowWithinASeason()
        {
            Assert.InRange(PropertyCatalog.MaxTrendDays, 1, 10);
            Assert.Equal(PropertyCatalog.MaxTrendDays, PropertyMarket.CutOldTrend(120)); //a trend saved when swings lasted months
            Assert.Equal(3, PropertyMarket.CutOldTrend(3));
        }

        [Fact]
        public void OrdinaryPropertyMarket_LetsAnIndexBelowTheFloorRiseButNotFallFurther()
        {
            double index = 0.3;
            int direction = -1, daysLeft = 10;
            PropertyMarket.Step(ref index, ref direction, ref daysLeft, () => 0.5);
            Assert.Equal(0.3, index, 9); //a bust doesn't push it lower, and it isn't snapped up to 0.6

            index = 0.3; direction = 1; daysLeft = 10;
            PropertyMarket.Step(ref index, ref direction, ref daysLeft, () => 0.5);
            Assert.True(index > 0.3 && index < 0.6);
        }

        // ---- patch notes and channels

        [Fact]
        public void NewSince_ReturnsTheNotesAboveTheOneAlreadyAnnounced()
        {
            string key = PatchNotes.KeyOf(PatchNotes.All[3]);
            Assert.Equal(PatchNotes.All.Take(3).Select(PatchNotes.KeyOf), PatchNotes.NewSince(key, 10).Select(PatchNotes.KeyOf));
            Assert.Equal(2, PatchNotes.NewSince(key, 2).Count);
            Assert.Equal(3, PatchNotes.CountSince(key));
        }

        [Fact]
        public void NewSince_IsEmptyForTheNewestUnknownOrMissingKey()
        {
            Assert.Empty(PatchNotes.NewSince(PatchNotes.KeyOf(PatchNotes.All[0]), 10));
            Assert.Empty(PatchNotes.NewSince("2000-01-01|a note that was reworded", 10));
            Assert.Empty(PatchNotes.NewSince(null, 10));
            Assert.Equal(0, PatchNotes.CountSince(null));
        }

        [Fact]
        public void RecentChannels_AreTheLastThreeNewestFirstWithoutDuplicates()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            coins.GetAll().RecentActiveChannelIds.Clear();
            coins.GetAll().LastActiveChannelId = 0;
            coins.GetAll().Accounts.Add(new CoinAccount { UserId = 1, Name = "a" });
            Assert.Empty(coins.RecentChannels());

            foreach (ulong channel in new ulong[] { 10, 20, 30, 20, 40 })
                coins.MarkActive(1, channel);

            Assert.Equal(new ulong[] { 40, 20, 30 }, coins.RecentChannels());
            Assert.Equal(40UL, coins.GetAll().LastActiveChannelId);
        }

        [Fact]
        public void RecentChannels_FallBackToTheLastActiveChannelOnOldSaves()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().RecentActiveChannelIds.Clear();
            coins.GetAll().LastActiveChannelId = 99;
            Assert.Equal(new ulong[] { 99 }, coins.RecentChannels());
        }
    }
}
