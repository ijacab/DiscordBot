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
