using Common.Services;
using DiscordBot.Exceptions;
using DiscordBot;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>
    /// `.buy max` / `.short max` with billions of cash used to loop forever (an int binary search overflowed) inside the stock
    /// lock, which froze the bot. These guard against that, and against share counts passing int.MaxValue.
    /// </summary>
    public class StockOverflowTests
    {
        private static Stock Normal(double price) =>
            new Stock { Symbol = "NRM", RiskTier = 3, Price = price, FairPrice = price, PreviousPrice = price, BasePrice = price };

        /// <summary>Runs the code on another thread and fails, rather than hanging the test run, if it doesn't finish.</summary>
        private static T Finishes<T>(Func<T> work)
        {
            var task = Task.Run(work);
            Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "Still running after 10 seconds: it's stuck in a loop.");
            return task.Result;
        }

        [Fact]
        public void MaxAffordableShares_WithBillionsFinishesAndFitsTheCash()
        {
            var stock = Normal(1);
            int shares = Finishes(() => stock.MaxAffordableShares(1e12));
            Assert.True(shares > 0);
            Assert.True(Math.Round((double)shares * stock.PreviewTrade(shares).Fill, 2) <= 1e12);
        }

        [Fact]
        public void MaxAffordableShares_StopsAtTheCap()
        {
            Assert.Equal(5_000, Finishes(() => Normal(1).MaxAffordableShares(1e12, Stock.Spread, 5_000)));
            Assert.Equal(0, Normal(1).MaxAffordableShares(1e12, Stock.Spread, 0));
        }

        [Fact]
        public void MaxShortableShares_WithBillionsFinishesAndFitsTheCash()
        {
            var stock = Normal(50);
            int shares = Finishes(() => stock.MaxShortableShares(1e12));
            Assert.True(shares > 0);
            double gross = Math.Round((double)shares * stock.PreviewTrade(-shares).Fill, 2);
            Assert.True(gross + Stock.TaxOn(gross) <= 1e12);
        }

        [Fact]
        public void Holdings_CantPassIntMaxValue()
        {
            var market = new StockMarket();
            market.AddShares(1, "NRM", int.MaxValue - 10);
            Assert.Throws<BadInputException>(() => market.RecordPurchase(1, "NRM", 11, 5));
            Assert.Throws<BadInputException>(() => market.AddShares(1, "NRM", 11));
            Assert.Equal(int.MaxValue - 10, market.GetShares(1, "NRM"));

            market.AddShort(2, "NRM", int.MaxValue - 1, 5, 100);
            Assert.Throws<BadInputException>(() => market.AddShort(2, "NRM", 2, 5, 100));
            Assert.Equal(int.MaxValue - 1, market.GetShort(2, "NRM").Shares);
        }

        [Fact]
        public void StockService_BuyAndShortMaxWithTrillionsFinish()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var stocks = new StockService(new LocalFileService(), coins);
            stocks.ClearAll().Wait();
            var symbol = stocks.GetMarket().Stocks.First(s => s.Role == StockRole.Normal).Symbol;

            var buyer = new CoinAccount { UserId = 900_001, Name = "rich", NetWorth = 1e13 };
            coins.GetAll().Accounts.Add(buyer);
            var bought = Finishes(() => stocks.Buy(buyer, symbol, "max"));
            Assert.True(bought.Shares > 0);
            Assert.True(buyer.NetWorth >= 0);

            //the holding is now as big as the market tracks, or the rest of the cash can't buy more: either way no hang
            double cashBefore = buyer.NetWorth;
            int heldBefore = stocks.GetMarket().GetShares(buyer.UserId, symbol);
            if (heldBefore == int.MaxValue)
            {
                Assert.Throws<BadInputException>(() => stocks.Buy(buyer, symbol, "max"));
                Assert.Equal(cashBefore, buyer.NetWorth);
                Assert.Equal(heldBefore, stocks.GetMarket().GetShares(buyer.UserId, symbol));
            }

            var shorter = new CoinAccount { UserId = 900_002, Name = "richer", NetWorth = 1e13 };
            coins.GetAll().Accounts.Add(shorter);
            var shorted = Finishes(() => stocks.Short(shorter, symbol, "max"));
            Assert.True(shorted.Shares > 0);
            Assert.True(shorter.NetWorth >= 0);
            stocks.ClearAll().Wait();
        }

        [Fact]
        public void StockService_RefusesABuyThatWouldOverflowTheHolding()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var stocks = new StockService(new LocalFileService(), coins);
            stocks.ClearAll().Wait();
            var symbol = stocks.GetMarket().Stocks.First(s => s.Role == StockRole.Normal).Symbol;
            var buyer = new CoinAccount { UserId = 900_003, Name = "full", NetWorth = 1e15 };
            coins.GetAll().Accounts.Add(buyer);
            stocks.GetMarket().AddShares(buyer.UserId, symbol, int.MaxValue - 5);

            Assert.Throws<BadInputException>(() => stocks.Buy(buyer, symbol, "6"));
            Assert.Equal(1e15, buyer.NetWorth);
            Assert.Equal(int.MaxValue - 5, stocks.GetMarket().GetShares(buyer.UserId, symbol));

            var filled = stocks.Buy(buyer, symbol, "max");
            Assert.Equal(5, filled.Shares);
            Assert.Throws<BadInputException>(() => stocks.Buy(buyer, symbol, "max"));
            stocks.ClearAll().Wait();
        }

        // ---- watchdog

        private static readonly DateTime Start = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Watchdog_HealthyBotIsLeftAlone()
        {
            var now = Start.AddHours(2);
            Assert.Null(HangWatchdog.ShouldRestart(now, Start, now.AddMinutes(-1), now.AddMinutes(-1)));
        }

        [Fact]
        public void Watchdog_RestartsAfterTenMinutesOffline()
        {
            var now = Start.AddHours(2);
            Assert.NotNull(HangWatchdog.ShouldRestart(now, Start, now.AddMinutes(-10), now.AddMinutes(-1)));
            Assert.Null(HangWatchdog.ShouldRestart(now, Start, now.AddMinutes(-9), now.AddMinutes(-1)));
        }

        [Fact]
        public void Watchdog_RestartsWhenTheMinuteLoopIsStuck()
        {
            var now = Start.AddHours(2);
            Assert.NotNull(HangWatchdog.ShouldRestart(now, Start, now, now.AddMinutes(-10)));
        }

        [Fact]
        public void Watchdog_GivesStartupTenMinutes()
        {
            Assert.Null(HangWatchdog.ShouldRestart(Start.AddMinutes(9), Start, Start, Start));
            Assert.NotNull(HangWatchdog.ShouldRestart(Start.AddMinutes(10), Start, Start, Start));
        }
    }
}
