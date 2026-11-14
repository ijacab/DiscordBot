using DiscordBot.Models;
using DiscordBot.Services;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    /// <summary>The live ticker thread from `.stocksthread`: the ticker cards and the saved layout.</summary>
    public class StockThreadTests
    {
        private static Stock WithHistory(string symbol, IEnumerable<double> history, double basePrice, int tier = 3, StockRole role = StockRole.Normal)
        {
            var prices = history.ToList();
            double price = prices.Last();
            return new Stock { Symbol = symbol, Name = symbol, RiskTier = tier, Role = role, Price = price, FairPrice = price, PreviousPrice = price, BasePrice = basePrice, History = prices };
        }

        [Fact]
        public void RecentCard_DrawsTheLastThreeHoursOfTicks()
        {
            var history = Enumerable.Range(1, 60).Select(i => (double)i).ToList();
            var card = StockTickerCard.Build(WithHistory("ABC", history, 1), StockThreadService.RecentWindow, colourByWindow: true);

            Assert.NotNull(card.Png);
            Assert.Equal(37, card.Points.Count); //36 five-minute ticks plus the starting point
            Assert.Equal(24, card.Points[0]);
            Assert.Equal(60, card.Points.Last());
            double change = (60.0 - 24) / 24 * 100;
            Assert.Contains($"{change:+0.00;-0.00;0.00}% over the last 3h 0m", card.Text);
        }

        [Fact]
        public void AllTimeCard_DrawsEverything()
        {
            var history = Enumerable.Range(1, 30).Select(i => (double)i).ToList();
            var card = StockTickerCard.Build(WithHistory("ABC", history, 1), null);
            Assert.Equal(30, card.Points.Count);
            Assert.NotNull(card.Png);
        }

        [Fact]
        public void OnePoint_HasNoChart()
        {
            var card = StockTickerCard.Build(WithHistory("ABC", new[] { 5.0 }, 5), StockThreadService.RecentWindow, colourByWindow: true);
            Assert.Null(card.Png);
            Assert.Contains("Not enough price history yet", card.Text);
        }

        [Fact]
        public void RecentLine_FollowsTheWindowNotAllTime()
        {
            //launched at 100, down at 50 now, but up over the last 3 hours
            var history = Enumerable.Repeat(90.0, 20).Concat(Enumerable.Range(0, 37).Select(i => 40.0 + i * 10.0 / 36)).ToList();
            var stock = WithHistory("ABC", history, 100);
            var hour = StockTickerCard.Window(stock.History, StockThreadService.RecentWindow);
            Assert.Equal(37, hour.Count);

            Assert.True(stock.AllTimeChange < 0);
            Assert.Equal(StockChart.ColourForChange(1), StockTickerCard.LineColourFor(stock, hour, colourByWindow: true));
            Assert.Equal(StockChart.ColourForChange(-1), StockTickerCard.LineColourFor(stock, hour, colourByWindow: false));
        }

        [Fact]
        public void Ordered_MatchesTheStocksListing()
        {
            var stocks = new List<Stock>
            {
                WithHistory("GOLD", new[] { 1.0 }, 1, 2, StockRole.SafeHaven),
                WithHistory("ZED", new[] { 1.0 }, 1, 3),
                WithHistory("COIN", new[] { 1.0 }, 1, 6),
                WithHistory("ABC", new[] { 1.0 }, 1, 3),
                WithHistory("BOND", new[] { 1.0 }, 1, 1, StockRole.Bond),
            };
            Assert.Equal(new[] { "ABC", "ZED", "COIN", "GOLD", "BOND" }, StockTickerCard.Ordered(stocks).Select(s => s.Symbol));
        }

        private static LiveStockThread Saved(params string[] symbols)
        {
            var entry = new LiveStockThread();
            ulong id = 1;
            foreach (var (symbol, lastHour) in LiveStockThread.LayoutFor(symbols))
                entry.Tickers.Add(new TickerMessage { Symbol = symbol, LastHour = lastHour, MessageId = id++ });
            return entry;
        }

        [Fact]
        public void Layout_IsAllTimeForEachThenLastHourForEach()
        {
            var layout = LiveStockThread.LayoutFor(new[] { "A", "B" });
            Assert.Equal(new[] { ("A", false), ("B", false), ("A", true), ("B", true) }, layout);
        }

        [Fact]
        public void NeedsRepost_OnlyWhenTheStocksChanged()
        {
            Assert.False(Saved("A", "B").NeedsRepost(new[] { "A", "B" }));
            Assert.True(Saved("A", "B").NeedsRepost(new[] { "A", "B", "C" }));
            Assert.True(Saved("A", "B").NeedsRepost(new[] { "A" }));
            Assert.True(Saved("A", "B").NeedsRepost(new[] { "B", "A" }));

            var missingHour = Saved("A", "B");
            missingHour.Tickers.RemoveAt(3);
            Assert.True(missingHour.NeedsRepost(new[] { "A", "B" }));
        }

        [Fact]
        public void State_RoundTripsThroughJson()
        {
            var state = new StockThreads();
            var entry = Saved("A");
            entry.GuildId = 111111111111111111;
            entry.ThreadId = 222222222222222222;
            entry.HeaderMessageId = 333333333333333333;
            entry.LastSlot = 5_000_000;
            state.Threads.Add(entry);

            var back = JsonConvert.DeserializeObject<StockThreads>(JsonConvert.SerializeObject(state));
            var loaded = Assert.Single(back.Threads);
            Assert.Equal(entry.ThreadId, loaded.ThreadId);
            Assert.Equal(entry.HeaderMessageId, loaded.HeaderMessageId);
            Assert.Equal(entry.LastSlot, loaded.LastSlot);
            Assert.False(loaded.NeedsRepost(new[] { "A" }));
        }
    }
}
