using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;

namespace Tests
{
    /// <summary>
    /// Options are priced by running the real tick process forward (OptionPricer). These tests run it again with other random
    /// numbers to check the house still wins on average, and that a big move beats just buying the stock.
    /// </summary>
    public class OptionPricingTests
    {
        private static Stock At(int tier, double stateOfBase, double basePrice = 100)
        {
            double price = basePrice * stateOfBase;
            return new Stock
            {
                Symbol = "T" + tier, RiskTier = tier, Role = StockRole.Normal, BasePrice = basePrice,
                FairPrice = price, Price = price, PreviousPrice = price,
            };
        }

        private static int TicksOf(TimeSpan d) => (int)Math.Round(d.TotalMinutes / 5);

        [Fact]
        public void TheHouseWinsOnAverage_ForCallsAndPuts_AcrossStatesAndDurations()
        {
            var durations = new[] { TimeSpan.FromMinutes(15), TimeSpan.FromHours(6), TimeSpan.FromHours(24) };
            foreach (int tier in new[] { 3, 5, 6 })
            foreach (double state in new[] { 0.7, 1.0, 1.4 })
            foreach (var duration in durations)
            foreach (bool isCall in new[] { true, false })
            {
                var stock = At(tier, state);
                //a crypto call is worth its rare stacked mega booms, which a plain average never sees (see the tests for it below)
                if (tier == 6 && isCall) continue;
                double premium = stock.OptionPremiumPerShare(duration, isCall);
                if (premium < 0.5) continue; //too cheap to measure (the 1 cent minimum)

                //other random numbers than the pricer used
                int paths = tier == 6 ? 40_000 : 6_000;
                double realised = OptionPricer.Simulate(stock, isCall, TicksOf(duration), MarketDrift.None, paths, seed: 2_468_013 + tier);
                double ratio = realised / premium;
                Assert.True(ratio is > 0.78 and < 0.99,
                    $"tier {tier} at {state}x base, {duration}, {(isCall ? "call" : "put")}: premium {premium:F2} but it pays {realised:F2} on average (ratio {ratio:F3})");
            }
        }

        [Fact]
        public void ACallOnTheCalmerTiers_BeatsBuyingTheStockWhenItDoubles()
        {
            foreach (int tier in new[] { 3, 4, 5 })
            {
                var stock = At(tier, 1.0);
                double premium = stock.OptionPremiumPerShare(TimeSpan.FromHours(6), isCall: true);
                double cost = premium * (1 + Stock.OptionFeeRate);
                double callMultiple = stock.FairPrice / cost; //pays one strike's worth when the price doubles

                double buyMultiple = 2 * (1 - Stock.Spread / 2) / (1 + Stock.Spread / 2) * (1 - Stock.SellTaxRate); //spread both ways and the sell tax
                Assert.True(premium < 0.4 * stock.FairPrice, $"tier {tier} premium {premium:F2}");
                Assert.True(callMultiple > buyMultiple, $"tier {tier}: the call returns {callMultiple:F2}x, buying {buyMultiple:F2}x");
            }
        }

        [Fact]
        public void APriceFarBelowItsBase_HasDearCallsAndCheapPuts_AndTheReverseAbove()
        {
            var below = At(5, 0.5);
            var above = At(5, 1.4);
            var sixHours = TimeSpan.FromHours(6);

            Assert.True(below.OptionPremiumPerShare(sixHours, true) > 5 * below.OptionPremiumPerShare(sixHours, false));
            Assert.True(above.OptionPremiumPerShare(sixHours, false) > 5 * above.OptionPremiumPerShare(sixHours, true));
        }

        [Fact]
        public void InAMarketCrash_PutsAreDearAndDearerThanCalls()
        {
            //with no pull back to the base the price wanders further, and the crash drift tilts it down
            var stock = At(4, 1.0);
            var crash = MarketCycle.DriftFor(stock, CrisisPhase.Decline, severity: 1);

            foreach (var duration in new[] { TimeSpan.FromHours(6), TimeSpan.FromHours(24) })
            {
                double put = stock.OptionPremiumPerShare(duration, false, crash);
                Assert.True(put > 2 * stock.OptionPremiumPerShare(duration, false), $"{duration}: put {put:F2}");
                Assert.True(put > 1.2 * stock.OptionPremiumPerShare(duration, true, crash), $"{duration}: put {put:F2}");
            }
        }

        [Fact]
        public void LongerAndWilderCostMore()
        {
            var sixHours = TimeSpan.FromHours(6);
            Assert.True(At(3, 1).OptionPremiumPerShare(TimeSpan.FromMinutes(15)) < At(3, 1).OptionPremiumPerShare(TimeSpan.FromHours(1)));
            Assert.True(At(3, 1).OptionPremiumPerShare(sixHours) < At(4, 1).OptionPremiumPerShare(sixHours));
            Assert.True(At(4, 1).OptionPremiumPerShare(sixHours) < At(5, 1).OptionPremiumPerShare(sixHours));
            Assert.True(At(5, 1).OptionPremiumPerShare(sixHours) < At(6, 1).OptionPremiumPerShare(sixHours));
        }

        [Fact]
        public void AQuoteIsSteadyForTheSameStateAndChangesWithTheState()
        {
            var a = At(4, 1.0);
            var b = At(4, 1.0);
            Assert.Equal(a.OptionPremiumPerShare(TimeSpan.FromHours(6)), b.OptionPremiumPerShare(TimeSpan.FromHours(6)));

            b.FairPrice = 110;
            Assert.NotEqual(a.OptionPremiumPerShare(TimeSpan.FromHours(6)), b.OptionPremiumPerShare(TimeSpan.FromHours(6)));

            //a crypto in the middle of a mega boom (3 more ticks of it to come) isn't priced like a quiet one
            var quiet = At(6, 1.0);
            var booming = At(6, 1.0);
            booming.MegaTicks = 3;
            booming.MegaStep = 0.8;
            Assert.True(booming.OptionPremiumPerShare(TimeSpan.FromHours(1)) > 2 * quiet.OptionPremiumPerShare(TimeSpan.FromHours(1)));
            //and an ordinary coin mid-streak is still priced from where its streak takes it
            var mooning = At(5, 1.0);
            mooning.MomentumTicks = 5;
            Assert.True(mooning.OptionPremiumPerShare(TimeSpan.FromHours(1)) >= At(5, 1.0).OptionPremiumPerShare(TimeSpan.FromHours(1)));
        }

        [Fact]
        public void APayoutHasNoCap_ACallPaysTheWholeMove()
        {
            var call = new OptionContract { IsCall = true, Strike = 30, Contracts = 2 };
            Assert.Equal(2 * 100 * 30 * 3, call.PayoutAt(120), 6); //+90 is 3x the strike
            Assert.Equal(2 * 100 * 30 * 39, call.PayoutAt(30 * 40), 6); //a 40x boom pays 39 strikes
            Assert.Equal(2 * 100 * 30 * 99_999, call.PayoutAt(30 * 100_000), 2);
        }

        [Fact]
        public void APutPaysAtMostTheStrike()
        {
            var put = new OptionContract { IsCall = false, Strike = 30, Contracts = 2 };
            Assert.Equal(2 * 100 * 29.99, put.PayoutAt(0.01), 2); //a crypto at its cent floor
            Assert.Equal(0, put.PayoutAt(31));
        }

        [Fact]
        public void AnyQuoteIsFastEnoughToDoOnTheGatewayTask()
        {
            var crypto = At(6, 1.0);
            var clock = Stopwatch.StartNew();
            crypto.OptionPremiumPerShare(TimeSpan.FromDays(3), true);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"a 3 day crypto call took {clock.Elapsed.TotalSeconds:F1}s to price");
        }

        /// <summary>The typical size of a stock's 6 hour move (the median of |end/start - 1|), by running the real tick process from its base. The median ignores the rare giant crypto moves.</summary>
        private static double SixHourTypicalMove(int tier, int paths = 6_000)
        {
            var random = new Random(55_000 + tier);
            var moves = new double[paths];
            for (int path = 0; path < paths; path++)
            {
                var stock = At(tier, 1.0);
                for (int tick = 0; tick < 72; tick++) stock.ApplyTick(random.NextDouble);
                moves[path] = Math.Abs(stock.FairPrice / 100 - 1);
            }
            Array.Sort(moves);
            return moves[paths / 2];
        }

        [Fact]
        public void TheRewardIsSimilarAtEveryOrdinaryTier_PremiumFollowsHowMuchTheStockMoves()
        {
            //a call costs about the same share of what the stock typically moves, so a move of the same size in its own terms
            //pays about the same multiple on a calm stock as on a wild one. Crypto is left out: its price is mostly its rare giant moves
            foreach (int tier in new[] { 3, 4, 5 })
            {
                double premiumShare = At(tier, 1.0).OptionPremiumPerShare(TimeSpan.FromHours(6), true) / 100;
                double perMove = premiumShare / SixHourTypicalMove(tier);
                Assert.InRange(perMove, 0.2, 0.9);
            }
        }

        [Fact]
        public void CryptoCalls_AreDear_AndNeverCheaperForALongerExpiry()
        {
            var coin = At(6, 1.0);
            double previous = 0;
            foreach (var duration in new[] { TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24) })
            {
                double premium = coin.OptionPremiumPerShare(duration, true) / 100;
                Assert.True(premium >= previous * 0.97, $"{duration}: {premium:F2} of the price, after {previous:F2}"); //each is its own simulation, so allow for a little noise
                previous = premium;
            }
            //with no cap a call is worth the rare giant booms, so a day long one costs more than the coin itself
            Assert.InRange(previous, 1.0, 10.0);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(6)]
        public void TheUncappedCryptoCallPrice_MatchesAPlainSimulation_WhereAPlainSimulationCanSeeTheTail(int ticks)
        {
            //over a few ticks at most one boom fits in, so a plain average over lots of paths is a fair check on the exact boom factor
            var coin = At(6, 1.0);
            double exact = OptionPricer.CryptoCallValue(coin, ticks, MarketDrift.None, paths: 20_000, seed: 77);
            double plain = OptionPricer.Simulate(coin, true, ticks, MarketDrift.None, paths: 600_000, seed: 31_337);
            Assert.InRange(exact / plain, 0.8, 1.25);
        }

        [Fact]
        public void TheUncappedCryptoCallQuote_IsSteadyAcrossSeeds()
        {
            //the plain average of the same call swings by a third from seed to seed
            var coin = At(6, 1.0);
            var quotes = new[] { 1, 2, 3, 4 }.Select(i => OptionPricer.CryptoCallValue(coin, 72, MarketDrift.None, paths: 10_000, seed: i * 1_000)).ToArray();
            Assert.True(quotes.Max() / quotes.Min() < 1.1, string.Join(", ", quotes.Select(q => q.ToString("F1"))));
        }

        [Fact]
        public void TheBoomFactor_IsOneWithNoBoomAndGrowsWithTheTimeAndTheSize()
        {
            var idle = Enumerable.Repeat(1.0, 72).ToArray();
            var none = new double[72];
            Assert.Equal(1.0, OptionPricer.BoomFactor(new double[72], none, 72), 9);
            Assert.True(OptionPricer.BoomFactor(idle, none, 12) < OptionPricer.BoomFactor(idle, none, 72));
            //a boom that fades away leaves less behind
            var fades = Enumerable.Repeat(0.1, 72).ToArray();
            Assert.True(OptionPricer.BoomFactor(idle, fades, 72) < OptionPricer.BoomFactor(idle, none, 72));
            //the growth of a boom: the whole size is worth about 100x on average, and none of it nothing
            Assert.Equal(1.0, OptionPricer.MegaGrowth(0), 9);
            Assert.InRange(OptionPricer.MegaGrowth(1), 90, 115);
        }

        [Fact]
        public void TheMegaGrowthFormula_MatchesTheDrawnSizes()
        {
            var random = new Random(4);
            foreach (double kept in new[] { 0.3, 0.7, 1.0 })
            {
                double sum = 0;
                const int draws = 400_000;
                for (int i = 0; i < draws; i++) sum += Math.Exp(kept * Stock.MegaSize(random.NextDouble()));
                double expected = OptionPricer.MegaGrowth(kept);
                Assert.InRange(sum / draws / expected, kept < 1 ? 0.95 : 0.4, kept < 1 ? 1.05 : 2.0); //kept 1 is heavy tailed
            }
        }

        [Fact]
        public void HoldingCrypto_KeepsItsTypicalPriceNearLaunch_ButItsAverageRunsHigher()
        {
            //the median price stays near the launch price (so most of the time it is flat), while rare mega booms pull the average up
            var random = new Random(31_415);
            const int paths = 3_000;
            var prices = new double[paths];
            for (int path = 0; path < paths; path++)
            {
                var coin = At(6, 1.0);
                for (int tick = 0; tick < 1_500; tick++) coin.ApplyTick(random.NextDouble);
                prices[path] = coin.FairPrice / 100;
            }
            Array.Sort(prices);
            Assert.InRange(prices[paths / 2], 0.5, 1.5);
            Assert.True(prices[paths - 1] > 1, "no boom at all");
        }

    }
}
