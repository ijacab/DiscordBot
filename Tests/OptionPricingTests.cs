using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
                double premium = stock.OptionPremiumPerShare(duration, isCall);
                if (premium < 0.5) continue; //too cheap to measure (the 1 cent minimum)

                //other random numbers than the pricer used
                int paths = tier == 6 ? 12_000 : 6_000;
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

            //a crypto in the middle of a moon streak isn't priced like a quiet one
            var quiet = At(6, 1.0);
            var mooning = At(6, 1.0);
            mooning.MomentumTicks = 5;
            Assert.True(mooning.OptionPremiumPerShare(TimeSpan.FromHours(1)) > quiet.OptionPremiumPerShare(TimeSpan.FromHours(1)));
        }

        [Fact]
        public void APayoutIsCappedAtTenTimesTheStrike()
        {
            var call = new OptionContract { IsCall = true, Strike = 30, Contracts = 2 };
            Assert.Equal(2 * 100 * 30 * 3, call.PayoutAt(120), 6); //+90 is 3x the strike, under the cap
            Assert.Equal(2 * 100 * 30 * 10, call.PayoutAt(30 * 40), 6);
        }

        [Fact]
        public void AnyQuoteIsFastEnoughToDoOnTheGatewayTask()
        {
            var crypto = At(6, 1.0);
            var clock = Stopwatch.StartNew();
            crypto.OptionPremiumPerShare(TimeSpan.FromDays(3), true);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"a 3 day crypto call took {clock.Elapsed.TotalSeconds:F1}s to price");
        }

        /// <summary>The spread of a stock's 6 hour return (root mean square of end/start - 1), by running the real tick process from its base.</summary>
        private static double SixHourSpread(int tier, int paths = 6_000)
        {
            var random = new Random(55_000 + tier);
            double sumSquares = 0;
            for (int path = 0; path < paths; path++)
            {
                var stock = At(tier, 1.0);
                for (int tick = 0; tick < 72; tick++) stock.ApplyTick(random.NextDouble);
                double move = stock.FairPrice / 100 - 1;
                sumSquares += move * move;
            }
            return Math.Sqrt(sumSquares / paths);
        }

        [Fact]
        public void TheRewardIsSimilarAtEveryTier_PremiumFollowsHowMuchTheStockMoves()
        {
            //a call costs about the same share of what the stock typically moves, so a move of the same size in its own terms
            //pays about the same multiple on a calm stock as on a wild one
            foreach (int tier in new[] { 3, 4, 5, 6 })
            {
                double premiumShare = At(tier, 1.0).OptionPremiumPerShare(TimeSpan.FromHours(6), true) / 100;
                double perMove = premiumShare / SixHourSpread(tier);
                Assert.InRange(perMove, 0.22, 0.60);
            }
        }

        [Fact]
        public void HoldingCrypto_IsNotAMoneyMachine()
        {
            //moons and crashes are equally likely and big, so the average price stays near the launch price
            var random = new Random(31_415);
            const int paths = 4_000;
            double sumPrice = 0, sumGrowth = 0;
            for (int path = 0; path < paths; path++)
            {
                var coin = At(6, 1.0);
                for (int tick = 0; tick < 1_000; tick++) coin.ApplyTick(random.NextDouble);
                double start = coin.FairPrice;
                sumPrice += start / 100;
                for (int tick = 0; tick < 72; tick++) coin.ApplyTick(random.NextDouble);
                sumGrowth += coin.FairPrice / start;
            }

            Assert.True(sumPrice / paths < 1.4, $"the average crypto price is {sumPrice / paths:F2}x its launch price");
            Assert.True(sumGrowth / paths < 1.4, $"holding crypto for 6 hours returns {sumGrowth / paths:F2}x on average");
        }

        [Fact]
        public void ACallOnCrypto_BeatsBuyingItWhenItDoubles()
        {
            var coin = At(6, 1.0, basePrice: 30);
            double premium = coin.OptionPremiumPerShare(TimeSpan.FromHours(6), true);
            double callMultiple = coin.FairPrice / (premium * (1 + Stock.OptionFeeRate));
            double buyMultiple = 2 * (1 - Stock.Spread / 2) / (1 + Stock.Spread / 2) * (1 - Stock.SellTaxRate);
            Assert.True(callMultiple > buyMultiple, $"the call returns {callMultiple:F2}x, buying {buyMultiple:F2}x");
        }
    }
}
