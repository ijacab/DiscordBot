using DiscordBot;
using DiscordBot.Models;
using DiscordBot.Services;
using Common.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class StockTests
    {
        private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

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
                var stock = new Stock { Symbol = "X", RiskTier = tier, Price = 100, PreviousPrice = 100, FairPrice = 100 };
                stock.ApplyTick(() => roll);
                Assert.InRange(stock.Price, 100 * (1 - maxSwing) - 0.01, 100 * (1 + maxSwing) + 0.01);
                Assert.Equal(100, stock.PreviousPrice);
            }
        }

        [Fact]
        public void Tick_NeverDropsBelowMinPrice()
        {
            var stock = new Stock { Symbol = "X", RiskTier = 5, Price = 1.0, PreviousPrice = 1.0, FairPrice = 1.0 };
            for (int i = 0; i < 50; i++)
                stock.ApplyTick(() => 0.0); //worst case every time
            Assert.Equal(Stock.MinPrice, stock.Price);
        }

        [Fact]
        public void Tick_PullsTowardBasePrice()
        {
            var stock = new Stock { Symbol = "X", RiskTier = 1, Price = 50, PreviousPrice = 50, FairPrice = 50, BasePrice = 100 };
            stock.ApplyTick(() => 0.5); //no swing, so only the pull back towards the base
            Assert.InRange(stock.Price, 50.01, 100);
        }

        [Fact]
        public void Tick_DoesNotDecayOverManyRandomTicks()
        {
            var random = new Random(42);
            var stock = new Stock { Symbol = "X", RiskTier = 5, Price = 10, PreviousPrice = 10, FairPrice = 10, BasePrice = 10 };
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
            string symbol = market.Stocks.First(s => s.RiskTier == 3).Symbol;
            market.RecordPurchase(1, symbol, 2, 100);
            market.RecordPurchase(1, symbol, 2, 200);
            Assert.Equal(4, market.GetShares(1, symbol));
            Assert.Equal(150, market.GetAverageCost(1, symbol));

            market.AddShares(1, symbol, -4);
            market.RecordPurchase(1, symbol, 1, 50);
            Assert.Equal(50, market.GetAverageCost(1, symbol));
        }

        [Fact]
        public void Chart_IsPngOfFixedSize()
        {
            using var png = StockChart.RenderPng(new double[] { 10, 12, 11, 20, 15 }, FiveMinutes);
            byte[] bytes = png.ToArray();

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray()); //PNG signature
            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(bytes);
            Assert.Equal(StockChart.Width, image.Width);
            Assert.Equal(StockChart.Height, image.Height);
        }

        [Fact]
        public void Chart_FlatPricesDoNotThrow()
        {
            using var png = StockChart.RenderPng(new double[] { 5, 5, 5 }, FiveMinutes);
            Assert.True(png.Length > 0);
        }

        [Fact]
        public void Chart_NeedsAtLeastTwoPrices()
        {
            Assert.Throws<ArgumentException>(() => StockChart.RenderPng(new double[] { 5 }, FiveMinutes));
        }

        [Fact]
        public void MultiChart_IsPngOfFixedSizeWithShorterSeries()
        {
            var series = new List<IReadOnlyList<double>>
            {
                new double[] { 1000, 1200, 1100, 1500 },
                new double[] { 50_000, 52_000 }, //joined later, lines up on the right
                new double[] { 7 },              //no history yet, just a current value
            };
            using var png = StockChart.RenderMultiPng(series, TimeSpan.FromHours(1));
            byte[] bytes = png.ToArray();

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());
            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(bytes);
            Assert.Equal(StockChart.Width, image.Width);
            Assert.Equal(StockChart.Height, image.Height);
        }

        [Fact]
        public void MultiChart_ValidatesInput()
        {
            Assert.Throws<ArgumentException>(() => StockChart.RenderMultiPng(new List<IReadOnlyList<double>>(), FiveMinutes));
            Assert.Throws<ArgumentException>(() => StockChart.RenderMultiPng(new List<IReadOnlyList<double>> { new double[] { 5 } }, FiveMinutes));
            Assert.Throws<ArgumentException>(() => StockChart.RenderMultiPng(new List<IReadOnlyList<double>> { new double[] { 5, 6 }, new double[0] }, FiveMinutes));
        }

        [Fact]
        public void Chart_LongHistoryRenders()
        {
            var random = new Random(7);
            var prices = Enumerable.Range(0, Stock.MaxHistoryPoints).Select(_ => 10 + random.NextDouble() * 5).ToList();
            using var png = StockChart.RenderPng(prices, FiveMinutes);

            Assert.True(png.Length > 0);
        }

        [Theory]
        [InlineData(13, "1h 0m")]
        [InlineData(Stock.MaxHistoryPoints, "6d 23h")]
        public void FormatSpan_DescribesTheCoveredTime(int points, string expected)
        {
            Assert.Equal(expected, StockChart.FormatSpan(points, FiveMinutes));
        }

        [Fact]
        public void Unrealised_GainUsesAverageCostOfHeldShares()
        {
            var market = StockMarket.CreateDefault();
            var stock = market.Stocks.First(s => s.RiskTier == 3);
            market.RecordPurchase(1, stock.Symbol, 2, stock.Price - 10);
            var (gain, cost) = market.GetUnrealisedGain(1);

            Assert.Equal(20d, gain, 6); //10 above the buy price, times 2 shares
            Assert.Equal(2 * (stock.Price - 10), cost, 6);
        }

        [Fact]
        public void Unrealised_IgnoresHoldingsWithUnknownCost()
        {
            var market = StockMarket.CreateDefault();
            market.AddShares(1, market.Stocks.First(s => s.RiskTier == 3).Symbol, 3); //no purchase recorded, so no buy price
            var (gain, cost) = market.GetUnrealisedGain(1);

            Assert.Equal(0d, gain);
            Assert.Equal(0d, cost);
        }

        [Fact]
        public void DefaultMarket_HasExactlyOneStockInEachOfTheSixTiers()
        {
            var market = StockMarket.CreateDefault();
            //the random pool is one per tier; gold and bonds are always listed on top of it
            var pool = market.Stocks.Where(s => s.Role == StockRole.Normal).ToList();
            Assert.Equal(Enumerable.Range(1, 6), pool.Select(s => s.RiskTier).OrderBy(t => t));
            Assert.Equal(pool.Count, pool.Select(s => s.Symbol).Distinct().Count());
            Assert.All(pool, s => Assert.Contains(StockMarket.Pool, p => p.Symbol == s.Symbol && p.Tier == s.RiskTier));
            Assert.NotNull(market.Find("GOLD"));
            Assert.NotNull(market.Find("BOND"));
        }

        [Fact]
        public void DefaultMarket_SameSeedPicksTheSameStocks()
        {
            var first = StockMarket.CreateDefault(new Random(5)).Stocks.Select(s => s.Symbol);
            var second = StockMarket.CreateDefault(new Random(5)).Stocks.Select(s => s.Symbol);
            Assert.Equal(first, second);
        }

        [Fact]
        public void Pool_HasChoiceInEveryTierAndTheRequestedNames()
        {
            foreach (int tier in Enumerable.Range(1, 6))
                Assert.True(StockMarket.Pool.Count(p => p.Tier == tier) >= 2, $"tier {tier} needs a choice");
            foreach (string name in new[] { "COCK", "PUBE", "SAMOA", "JSON", "DAVD" })
                Assert.Contains(StockMarket.Pool, p => p.Symbol == name);
        }

        [Fact]
        public void DefaultMarket_ManySeasonsEventuallyVary()
        {
            var seen = Enumerable.Range(0, 30)
                .Select(i => string.Join(",", StockMarket.CreateDefault(new Random(i)).Stocks.Select(s => s.Symbol)))
                .Distinct();
            Assert.True(seen.Count() > 1);
        }

        [Fact]
        public void Portfolio_AddRemoveAndValue()
        {
            var market = StockMarket.CreateDefault();
            var stock = market.Stocks.First(s => s.RiskTier == 3);
            string symbol = stock.Symbol;
            market.AddShares(1, symbol, 4);
            market.AddShares(1, symbol, -1);

            Assert.Equal(3, market.GetShares(1, symbol));
            Assert.Equal(3 * stock.Price, market.GetPortfolioValue(1));

            market.AddShares(1, symbol, -3);
            Assert.Equal(0, market.GetShares(1, symbol));
            Assert.Throws<InvalidOperationException>(() => market.AddShares(1, symbol, -1));
        }

        /// <summary>A stock with the default 100k shares, so trading 10% of them moves the price by about 5%.</summary>
        private static Stock Tradable(int tier = 3, double price = 50) =>
            new Stock { Symbol = "X", RiskTier = tier, Price = price, FairPrice = price, PreviousPrice = price, BasePrice = price };

        [Fact]
        public void Trading_BuyingPushesThePriceUpAndSellingPushesItDown()
        {
            var stock = Tradable();
            stock.ApplyTrade(1000);
            double afterBuy = stock.Price;
            Assert.True(afterBuy > 50);

            stock.ApplyTrade(-1000);
            Assert.True(stock.Price < afterBuy);
        }

        [Fact]
        public void Trading_BiggerTradesMoveThePriceFurther()
        {
            var small = Tradable();
            var big = Tradable();
            small.ApplyTrade(100);
            big.ApplyTrade(400);

            //4x the shares is 2x the move, because the impact grows with the square root of the trade's value
            Assert.Equal(2 * small.Impact, big.Impact, 4);
        }

        [Fact]
        public void Trading_SameShareCountMovesLessInAStockWithMoreShares()
        {
            var thin = Tradable();
            var deep = Tradable();
            deep.SharesOutstanding = 10 * thin.SharesOutstanding;
            thin.ApplyTrade(1000);
            deep.ApplyTrade(1000);

            //ten times the shares in the market, so the same trade is a tenth of the size
            Assert.Equal(thin.Impact / Math.Sqrt(10), deep.Impact, 6);
        }

        [Fact]
        public void Trading_ImpactFadesOnEachTick()
        {
            var stock = Tradable();
            stock.ApplyTrade(1000);
            double impact = stock.Impact;

            stock.ApplyTick(() => 0.5); //no swing, so only the decay moves
            Assert.Equal(impact * Stock.ImpactDecayPerTick, stock.Impact, 6);

            for (int i = 0; i < 10; i++)
                stock.ApplyTick(() => 0.5);
            Assert.InRange(stock.Impact, 0, impact * 0.01);
        }

        [Fact]
        public void Trading_ImpactIsCappedInBothDirections()
        {
            var stock = Tradable(tier: 5, price: 10);
            stock.ApplyTrade(100_000_000);
            Assert.Equal(Stock.MaxImpact, stock.Impact, 6);

            stock.ApplyTrade(-300_000_000);
            Assert.Equal(-Stock.MaxImpact, stock.Impact, 6);
        }

        [Fact]
        public void Trading_FillIsBetweenThePriceBeforeAndAfterTheTrade()
        {
            var stock = Tradable();
            double before = stock.Price;
            double fill = stock.ApplyTrade(1000);

            Assert.InRange(fill, before, stock.Price);
        }

        [Fact]
        public void Trading_BuyingAndSellingInOneTickCostsTheSpread()
        {
            var stock = Tradable();
            double bought = stock.ApplyTrade(1000);
            double sold = stock.ApplyTrade(-1000);

            //the impact nets out, so what's left is the spread, plus a little from the square root curve
            double cost = (bought - sold) / bought;
            Assert.InRange(cost, Stock.Spread * 0.9, Stock.Spread * 1.2);
        }

        [Fact]
        public void SellTax_IsTwoPercentOfTheGrossSale()
        {
            Assert.Equal(20, Stock.TaxOn(1000));
            Assert.Equal(0, Stock.TaxOn(0));
        }

        [Fact]
        public void SellTax_MakesARoundTripCostAboutThreePercent()
        {
            var stock = Tradable();
            double cost = Math.Round(1000 * stock.PreviewTrade(1000).Fill, 2);
            stock.ApplyTrade(1000);
            double gross = Math.Round(1000 * stock.PreviewTrade(-1000).Fill, 2);
            double net = gross - Stock.TaxOn(gross);

            //about 1% spread plus 2% tax, with the impact netting out
            double loss = (cost - net) / cost;
            Assert.InRange(loss, 0.025, 0.035);
        }

        [Fact]
        public void Trading_SpreadMakesBuysDearerThanSellsAtTheSameMoment()
        {
            var buy = Tradable().PreviewTrade(1);
            var sell = Tradable().PreviewTrade(-1);

            Assert.InRange(buy.Fill - sell.Fill, 0.4, 0.7); //about 1% of a $50 share
        }

        [Fact]
        public void Trading_MaxAffordableSharesNeverSpendsMoreThanTheCash()
        {
            var stock = Tradable(tier: 5, price: 10);
            int max = stock.MaxAffordableShares(1000);

            Assert.True(max >= 1);
            Assert.True(Math.Round(max * stock.PreviewTrade(max).Fill, 2) <= 1000);
            Assert.True(Math.Round((max + 1) * stock.PreviewTrade(max + 1).Fill, 2) > 1000);
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
