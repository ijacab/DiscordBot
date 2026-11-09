using DiscordBot.Models;
using System;
using System.Linq;
using Xunit;

namespace Tests
{
    public class StockRetireTests
    {
        private static StockMarket MarketWith(Stock stock)
        {
            var market = new StockMarket { SchemaVersion = StockMarket.CurrentSchemaVersion };
            market.Stocks.Add(stock);
            return market;
        }

        [Fact]
        public void Retire_PaysShareholdersAtTheQuoteWithNoTax()
        {
            var stock = new Stock { Symbol = "OLD", RiskTier = 1, Price = 40, FairPrice = 50, BasePrice = 50 };
            var market = MarketWith(stock);
            market.RecordPurchase(1, "OLD", 10, 45); //adds the shares and records the cost

            var owed = market.Retire(stock);

            Assert.Equal(400, owed[1], 2);
            Assert.Equal(0, market.GetShares(1, "OLD"));
            Assert.Equal(0, market.GetAverageCost(1, "OLD"));
        }

        [Fact]
        public void Retire_ClosesShortsAtTheirEquity()
        {
            var stock = new Stock { Symbol = "OLD", RiskTier = 1, Price = 40, FairPrice = 40, BasePrice = 50 };
            var market = MarketWith(stock);
            market.AddShort(2, "OLD", 4, 50, 200); //sold at 50 with 200 margin, now 40: the gain is 40

            var owed = market.Retire(stock);

            Assert.Equal(240, owed[2], 2);
            Assert.Null(market.GetShort(2, "OLD"));
        }

        [Fact]
        public void Retire_SettlesOptionsAtTheFairPrice()
        {
            var stock = new Stock { Symbol = "OLD", RiskTier = 1, Price = 40, FairPrice = 50, BasePrice = 50 };
            var market = MarketWith(stock);
            var option = new OptionContract
            {
                UserId = 3, Symbol = "OLD", IsCall = true, Strike = 45, Contracts = 1, Premium = 10,
                ExpiresAtUtcTicks = DateTime.UtcNow.AddDays(1).Ticks
            };
            market.Options.Add(option);

            var owed = market.Retire(stock);

            Assert.Equal(option.PayoutAt(50), owed[3], 2);
            Assert.Empty(market.Options);
        }

        [Fact]
        public void Retire_RemovesTheStockAndWritesASettledLineForEachHolder()
        {
            var stock = new Stock { Symbol = "OLD", RiskTier = 2, Price = 40, FairPrice = 40, BasePrice = 50 };
            var market = MarketWith(stock);
            market.AddShares(1, "OLD", 5);
            market.AddShort(2, "OLD", 1, 40, 40);

            market.Retire(stock);

            Assert.Null(market.Find("OLD"));
            Assert.Single(market.RecentSettled[1]);
            Assert.Single(market.RecentSettled[2]);
        }

        [Fact]
        public void Retire_WithNoHoldersOnlyRemovesTheStock()
        {
            var stock = new Stock { Symbol = "OLD", RiskTier = 2, Price = 40, FairPrice = 40, BasePrice = 50 };
            var market = MarketWith(stock);

            Assert.Empty(market.Retire(stock));
            Assert.Empty(market.Stocks);
        }

        [Fact]
        public void NewMarkets_AreCreatedAtTheCurrentLayout()
        {
            Assert.Equal(StockMarket.CurrentSchemaVersion, StockMarket.CreateDefault().SchemaVersion);
            Assert.DoesNotContain(StockMarket.Pool, p => p.Tier < 3);
        }
    }
}
