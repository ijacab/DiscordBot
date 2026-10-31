using DiscordBot;
using DiscordBot.Models;
using DiscordBot.Services;
using Common.Services;
using System;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class StockTests
    {
        [Theory]
        [InlineData(1, 0.01)]
        [InlineData(2, 0.03)]
        [InlineData(3, 0.07)]
        [InlineData(4, 0.15)]
        [InlineData(5, 0.30)]
        public void Tick_StaysWithinTierSwing(int tier, double maxSwing)
        {
            foreach (double roll in new[] { 0.0, 0.25, 0.5, 0.999999 })
            {
                var stock = new Stock { Symbol = "X", RiskTier = tier, Price = 100, PreviousPrice = 100 };
                stock.ApplyTick(() => roll);
                Assert.InRange(stock.Price, 100 * (1 - maxSwing) - 0.01, 100 * (1 + maxSwing) + 0.01);
                Assert.Equal(100, stock.PreviousPrice);
            }
        }

        [Fact]
        public void Tick_NeverDropsBelowMinPrice()
        {
            var stock = new Stock { Symbol = "X", RiskTier = 5, Price = 1.0, PreviousPrice = 1.0 };
            for (int i = 0; i < 50; i++)
                stock.ApplyTick(() => 0.0); //worst case every time
            Assert.Equal(Stock.MinPrice, stock.Price);
        }

        [Fact]
        public void Tick_PullsTowardBasePrice()
        {
            var stock = new Stock { Symbol = "X", RiskTier = 1, Price = 50, PreviousPrice = 50, BasePrice = 100 };
            stock.ApplyTick(() => 0.5); //no swing, so only the pull back towards the base
            Assert.InRange(stock.Price, 50.01, 100);
        }

        [Fact]
        public void Tick_DoesNotDecayOverManyRandomTicks()
        {
            var random = new Random(42);
            var stock = new Stock { Symbol = "X", RiskTier = 5, Price = 10, PreviousPrice = 10, BasePrice = 10 };
            double logSum = 0;
            int count = 0;
            for (int i = 0; i < 100_000; i++)
            {
                stock.ApplyTick(random.NextDouble);
                if (i >= 1_000)
                {
                    logSum += Math.Log(stock.Price / stock.BasePrice);
                    count++;
                }
            }

            //averages around the base price rather than drifting down
            Assert.InRange(logSum / count, -0.5, 0.5);
        }

        [Fact]
        public void RecordHistory_KeepsOnlyRecentPoints()
        {
            var stock = new Stock { Symbol = "X", RiskTier = 1, Price = 10, BasePrice = 10 };
            for (int i = 0; i < Stock.MaxHistoryPoints + 5; i++)
                stock.RecordHistory();

            Assert.Equal(Stock.MaxHistoryPoints, stock.History.Count);
        }

        [Fact]
        public void Purchases_BlendAverageCost_AndResetWhenSoldOut()
        {
            var market = StockMarket.CreateDefault();
            market.RecordPurchase(1, "SCORT", 2, 100);
            market.RecordPurchase(1, "SCORT", 2, 200);
            Assert.Equal(4, market.GetShares(1, "SCORT"));
            Assert.Equal(150, market.GetAverageCost(1, "SCORT"));

            market.AddShares(1, "SCORT", -4);
            market.RecordPurchase(1, "SCORT", 1, 50);
            Assert.Equal(50, market.GetAverageCost(1, "SCORT"));
        }

        [Fact]
        public void Chart_HasOneRowPerLevelPlusAxisAndLabelsMinAndMax()
        {
            string chart = StockChart.Render(new double[] { 10, 12, 11, 20, 15 });
            var lines = chart.Split('\n');

            Assert.Equal(StockChart.Height + 1, lines.Length);
            Assert.Contains("$20.00", lines[0]);
            Assert.Contains("$10.00", lines[StockChart.Height - 1]);
        }

        [Fact]
        public void Chart_FlatPricesDoNotThrow()
        {
            string chart = StockChart.Render(new double[] { 5, 5, 5 });
            Assert.Equal(StockChart.Height + 1, chart.Split('\n').Length);
        }

        [Fact]
        public void Chart_LongHistoryKeepsEveryRowTheSameWidth()
        {
            var random = new Random(7);
            var prices = Enumerable.Range(0, Stock.MaxHistoryPoints).Select(_ => 10 + random.NextDouble() * 5).ToList();
            var lines = StockChart.Render(prices).Split('\n');

            Assert.Equal(StockChart.Height + 1, lines.Length);
            foreach (string line in lines)
                Assert.Equal(lines[0].Length, line.Length);
        }

        [Fact]
        public void Unrealised_GainUsesAverageCostOfHeldShares()
        {
            var market = StockMarket.CreateDefault();
            market.RecordPurchase(1, "SCORT", 2, 40);
            var (gain, cost) = market.GetUnrealisedGain(1);

            Assert.Equal(20d, gain); //(50 - 40) * 2
            Assert.Equal(80d, cost);
        }

        [Fact]
        public void Unrealised_IgnoresHoldingsWithUnknownCost()
        {
            var market = StockMarket.CreateDefault();
            market.AddShares(1, "SCORT", 3); //no purchase recorded, so no buy price
            var (gain, cost) = market.GetUnrealisedGain(1);

            Assert.Equal(0d, gain);
            Assert.Equal(0d, cost);
        }

        [Fact]
        public void DefaultMarket_HasScortAndAllFiveTiers()
        {
            var market = StockMarket.CreateDefault();
            Assert.NotNull(market.Find("scort"));
            Assert.Equal(Enumerable.Range(1, 5), market.Stocks.Select(s => s.RiskTier).Distinct().OrderBy(t => t));
            Assert.Equal(market.Stocks.Count, market.Stocks.Select(s => s.Symbol).Distinct().Count());
        }

        [Fact]
        public void Portfolio_AddRemoveAndValue()
        {
            var market = StockMarket.CreateDefault();
            var scort = market.Find("SCORT");
            market.AddShares(1, "SCORT", 4);
            market.AddShares(1, "SCORT", -1);

            Assert.Equal(3, market.GetShares(1, "SCORT"));
            Assert.Equal(3 * scort.Price, market.GetPortfolioValue(1));

            market.AddShares(1, "SCORT", -3);
            Assert.Equal(0, market.GetShares(1, "SCORT"));
            Assert.Throws<InvalidOperationException>(() => market.AddShares(1, "SCORT", -1));
        }
    }

    public class InterestTests
    {
        private static CoinService CreateService(bool prestigeDisabled) =>
            new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = prestigeDisabled });

        [Fact]
        public void Interest_FlagOn_IsFlatTwoPercent()
        {
            var service = CreateService(prestigeDisabled: true);
            Assert.Equal(2, Constants.InterestPercentage);

            Assert.Equal(2_000, service.CalculateAddedInterest(new CoinAccount { NetWorth = 100_000 }));
            //doesn't scale down, even beyond the old prestige threshold, and ignores prestige level
            Assert.Equal(40_000, service.CalculateAddedInterest(new CoinAccount { NetWorth = 2_000_000, PrestigeLevel = 3 }));
        }

        [Fact]
        public void Interest_FlagOff_KeepsOldScalingFormula()
        {
            var service = CreateService(prestigeDisabled: false);
            //at the prestige threshold the percentage has scaled to 0, leaving only the flat amount
            Assert.Equal(1000, service.CalculateAddedInterest(new CoinAccount { NetWorth = 100_000 }));
        }

        [Fact]
        public void PrestigeThreshold_FlagOn_IgnoresPrestigeLevel()
        {
            var account = new CoinAccount { PrestigeLevel = 2 };
            Assert.Equal(100_000, account.GetPrestigeThreshold(prestigeDisabled: true));
            Assert.Equal(10_000_000, account.GetPrestigeThreshold(prestigeDisabled: false));
        }
    }
}
