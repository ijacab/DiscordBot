using Common.Helpers;
using DiscordBot.Models;
using DiscordBot.Services;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Linq;
using Xunit;

namespace Tests
{
    public class ShortAndOptionTests
    {
        private static Func<double> Rolls(params double[] values)
        {
            int next = 0;
            return () => values[next++]; //throws if the stock draws more numbers than expected
        }

        private static Stock Coin(double price = 100) =>
            new Stock { Symbol = "COIN", RiskTier = Stock.CryptoTier, Price = price, FairPrice = price, PreviousPrice = price, BasePrice = price, SharesOutstanding = Stock.CryptoSharesOutstanding };

        private static Stock Normal(int tier = 3, double price = 100) =>
            new Stock { Symbol = "NRM", RiskTier = tier, Price = price, FairPrice = price, PreviousPrice = price, BasePrice = price };

        // ---- crypto

        [Fact]
        public void Crypto_CrashDropsHardAndStartsTheRecovery()
        {
            var coin = Coin();
            coin.ApplyTick(Rolls(0.5, 0.0, 0.0)); //swing, event (crash), size (smallest)

            Assert.Equal(65, coin.FairPrice, 2);
            Assert.Equal(Stock.CryptoRecoveryTicks, coin.CrashRecoveryTicks);
        }

        [Fact]
        public void Crypto_PumpRisesAtLeastFortyPercent()
        {
            var coin = Coin();
            coin.ApplyTick(Rolls(0.5, 0.03, 0.0)); //swing, event (pump), size (smallest)

            Assert.InRange(coin.FairPrice, 138, 141); //+40%, less a touch of the slow pull back to 100
            Assert.Equal(0, coin.CrashRecoveryTicks);
        }

        [Fact]
        public void Crypto_WhileRecoveringOnlyDriftsAndDoesNotPumpOrReverse()
        {
            var coin = Coin();
            coin.BasePrice = 200; //far above, but nothing pulls it up during the recovery
            coin.CrashRecoveryTicks = 3;
            coin.ApplyTick(Rolls(0.999999)); //only the swing is drawn: no pump or crash roll

            Assert.Equal(2, coin.CrashRecoveryTicks);
            Assert.InRange(coin.FairPrice, 100, 100 * (1 + Stock.CryptoRecoverySwing) + 0.01);
        }

        [Fact]
        public void Crypto_AfterTheRecoveryItIsPulledBackSlowly()
        {
            var coin = Coin();
            coin.BasePrice = 200;
            coin.ApplyTick(Rolls(0.5, 0.5)); //no swing, no event

            double expected = 100 * Math.Pow(2, Stock.CryptoMeanReversionRate);
            Assert.Equal(expected, coin.FairPrice, 2);
            Assert.True(Stock.CryptoMeanReversionRate < Stock.MeanReversionRate);
        }

        [Fact]
        public void Crypto_NeverFallsBelowTheMinimumPrice()
        {
            var coin = Coin(1.5);
            coin.ApplyTick(Rolls(0.5, 0.0, 0.999999)); //biggest crash
            Assert.True(coin.Price >= Stock.MinPrice);
        }

        [Fact]
        public void Crypto_IsWilderThanEveryOtherTier()
        {
            Assert.Equal(Stock.CryptoTier, Stock.MaxTier);
            Assert.All(Enumerable.Range(1, 5), tier => Assert.True(Stock.MaxSwingByTier[Stock.CryptoTier] > Stock.MaxSwingByTier[tier]));
            Assert.True(Coin().TickVolatility > Normal(5).TickVolatility);
        }

        [Fact]
        public void OtherTiersNeverRollACrashOrPump()
        {
            var stock = Normal(5);
            stock.ApplyTick(Rolls(0.0)); //one draw only
            Assert.Equal(0, stock.CrashRecoveryTicks);
        }

        // ---- all time change and colour

        [Fact]
        public void AllTimeChange_IsMeasuredFromTheLaunchPrice()
        {
            var stock = Normal(price: 100);
            stock.Price = 125;
            Assert.Equal(25, stock.AllTimeChange, 6);
            stock.Price = 80;
            Assert.Equal(-20, stock.AllTimeChange, 6);
        }

        [Fact]
        public void ChartColour_IsGreenAtOrAboveAndRedBelow()
        {
            var green = new Rgba32(87, 242, 135);
            var red = new Rgba32(237, 66, 69);
            Assert.Equal(green, StockChart.ColourForChange(5));
            Assert.Equal(green, StockChart.ColourForChange(0));
            Assert.Equal(red, StockChart.ColourForChange(-0.01));
        }

        [Fact]
        public void Chart_AcceptsAColourOverride()
        {
            using var png = StockChart.RenderPng(new double[] { 10, 12, 11 }, TimeSpan.FromMinutes(5), new Rgba32(1, 2, 3));
            Assert.True(png.Length > 0);
            using var multi = StockChart.RenderMultiPng(new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<double>> { new double[] { 1, 2, 3 } }, TimeSpan.FromMinutes(5), null, new Rgba32(1, 2, 3));
            Assert.True(multi.Length > 0);
        }

        // ---- durations

        [Theory]
        [InlineData("30m", 30)]
        [InlineData("48h", 48 * 60)]
        [InlineData("2d", 2 * 24 * 60)]
        [InlineData("6H", 6 * 60)]
        [InlineData(" 15m ", 15)]
        public void Duration_ParsesMinutesHoursAndDays(string text, int minutes)
        {
            Assert.True(DurationParser.TryParse(text, out var span));
            Assert.Equal(TimeSpan.FromMinutes(minutes), span);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("48")]
        [InlineData("h")]
        [InlineData("0h")]
        [InlineData("-5m")]
        [InlineData("1.5h")]
        [InlineData("3w")]
        [InlineData("abc")]
        public void Duration_RejectsAnythingElse(string text)
        {
            Assert.False(DurationParser.TryParse(text, out _));
        }

        // ---- shorts

        [Fact]
        public void Short_ProfitsWhenThePriceFalls_AndLosesWhenItRises()
        {
            var position = new ShortPosition { Shares = 10, EntryPrice = 50, Margin = 500 };
            Assert.Equal(600, position.EquityAt(40), 6); //margin + 10 gain per share
            Assert.Equal(400, position.EquityAt(60), 6);
            Assert.Equal(500, position.EquityAt(50), 6);
        }

        [Fact]
        public void Short_EquityNeverGoesNegative_AndIsLiquidatedAtDouble()
        {
            var position = new ShortPosition { Shares = 10, EntryPrice = 50, Margin = 500 };
            Assert.False(position.IsLiquidatedAt(99.99));
            Assert.True(position.IsLiquidatedAt(100));
            Assert.Equal(0, position.EquityAt(100), 6);
            Assert.Equal(0, position.EquityAt(500), 6);
        }

        [Fact]
        public void Short_AddingAveragesTheEntryAndSumsTheMargin()
        {
            var market = StockMarket.CreateDefault();
            market.AddShort(1, "X", 10, 50, 500);
            market.AddShort(1, "X", 10, 70, 700);

            var position = market.GetShort(1, "X");
            Assert.Equal(20, position.Shares);
            Assert.Equal(60, position.EntryPrice, 6);
            Assert.Equal(1200, position.Margin, 6);
        }

        [Fact]
        public void Short_PartialCoverReleasesProportionalMargin_AndFullCoverRemovesIt()
        {
            var market = StockMarket.CreateDefault();
            market.AddShort(1, "X", 10, 50, 500);

            Assert.Equal(200, market.ReduceShort(1, "X", 4), 6);
            Assert.Equal(6, market.GetShort(1, "X").Shares);
            Assert.Equal(300, market.GetShort(1, "X").Margin, 6);

            Assert.Equal(300, market.ReduceShort(1, "X", 6), 6);
            Assert.Null(market.GetShort(1, "X"));
            Assert.Throws<InvalidOperationException>(() => market.ReduceShort(1, "X", 1));
        }

        [Fact]
        public void Short_CannotCoverMoreThanShorted()
        {
            var market = StockMarket.CreateDefault();
            market.AddShort(1, "X", 3, 50, 150);
            Assert.Throws<InvalidOperationException>(() => market.ReduceShort(1, "X", 4));
        }

        [Fact]
        public void Short_CountsTowardPortfolioValueAtItsEquity()
        {
            var market = StockMarket.CreateDefault();
            var stock = market.Stocks.First(s => s.RiskTier == 3);
            market.AddShort(1, stock.Symbol, 10, stock.Price, 10 * stock.Price);
            stock.Price = stock.Price - 5;

            Assert.Equal(10 * (stock.Price + 5) + 50, market.GetPortfolioValue(1), 6);
        }

        [Fact]
        public void MaxShortable_FitsMarginPlusTaxInCash()
        {
            var stock = Normal(3, 50);
            double cash = 1_000;
            int shares = stock.MaxShortableShares(cash);

            double Needed(int n) { double g = Math.Round(n * stock.PreviewTrade(-n).Fill, 2); return g + Stock.TaxOn(g); }
            Assert.True(shares > 0);
            Assert.True(Needed(shares) <= cash);
            Assert.True(Needed(shares + 1) > cash);
        }

        [Fact]
        public void MaxShortable_IsZeroWhenCashCannotCoverOneShare()
        {
            Assert.Equal(0, Normal(3, 50).MaxShortableShares(10));
        }

        // ---- options

        [Fact]
        public void Option_CallPaysAboveTheStrike_PutPaysBelow()
        {
            var call = new OptionContract { IsCall = true, Strike = 50, Contracts = 2 };
            var put = new OptionContract { IsCall = false, Strike = 50, Contracts = 2 };

            Assert.Equal(2 * 100 * 5, call.PayoutAt(55), 6);
            Assert.Equal(0, call.PayoutAt(45), 6);
            Assert.Equal(2 * 100 * 5, put.PayoutAt(45), 6);
            Assert.Equal(0, put.PayoutAt(55), 6);
            Assert.Equal(0, call.PayoutAt(50), 6);
        }

        [Fact]
        public void Option_PremiumRisesWithTimeAndWithRisk()
        {
            var steady = Normal(2, 100);
            var wild = Normal(5, 100);
            var coin = Coin(100);

            Assert.True(steady.OptionPremiumPerShare(TimeSpan.FromHours(24)) > steady.OptionPremiumPerShare(TimeSpan.FromHours(1)));
            Assert.True(wild.OptionPremiumPerShare(TimeSpan.FromHours(6)) > steady.OptionPremiumPerShare(TimeSpan.FromHours(6)));
            Assert.True(coin.OptionPremiumPerShare(TimeSpan.FromHours(6)) > wild.OptionPremiumPerShare(TimeSpan.FromHours(6)));
        }

        [Fact]
        public void Option_PremiumHasAMinimum()
        {
            Assert.Equal(Stock.MinOptionPremiumPerShare, Normal(1, 1).OptionPremiumPerShare(TimeSpan.FromMinutes(15)));
        }

        [Fact]
        public void Option_PremiumUsesFairPriceNotTheTradedQuote()
        {
            var stock = Normal(3, 100);
            double before = stock.OptionPremiumPerShare(TimeSpan.FromHours(6));
            stock.ApplyTrade(20_000); //pumps the quote, not the fair price
            Assert.True(stock.Price > stock.FairPrice);
            Assert.Equal(before, stock.OptionPremiumPerShare(TimeSpan.FromHours(6)));
        }

        [Fact]
        public void Option_ExpiryTimeIsKept()
        {
            var expiry = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var option = new OptionContract { ExpiresAtUtcTicks = expiry.Ticks };
            Assert.Equal(expiry, option.ExpiresAt);
            Assert.Equal(DateTimeKind.Utc, option.ExpiresAt.Kind);
        }

        [Fact]
        public void Option_OpenContractsCountAtWhatTheyWouldPayNow()
        {
            var market = StockMarket.CreateDefault();
            var stock = market.Stocks.First(s => s.RiskTier == 3);
            market.Options.Add(new OptionContract { UserId = 1, Symbol = stock.Symbol, IsCall = true, Strike = stock.FairPrice - 3, Contracts = 1 });
            market.Options.Add(new OptionContract { UserId = 1, Symbol = stock.Symbol, IsCall = false, Strike = stock.FairPrice - 3, Contracts = 1 }); //out of the money

            Assert.Equal(300, market.GetPortfolioValue(1), 6);
        }

        // ---- positions

        [Fact]
        public void HasPositions_SeesSharesShortsAndOptions()
        {
            var market = StockMarket.CreateDefault();
            Assert.False(market.HasPositions(1));

            market.AddShares(1, "X", 1);
            Assert.True(market.HasPositions(1));
            market.AddShares(1, "X", -1);
            Assert.False(market.HasPositions(1));

            market.AddShort(1, "X", 1, 10, 10);
            Assert.True(market.HasPositions(1));
            market.ReduceShort(1, "X", 1);
            Assert.False(market.HasPositions(1));

            market.Options.Add(new OptionContract { UserId = 1, Symbol = "X", Strike = 10, Contracts = 1 });
            Assert.True(market.HasPositions(1));
            Assert.False(market.HasPositions(2));
        }

        [Fact]
        public void RecentSettled_KeepsOnlyTheNewestFew()
        {
            var market = StockMarket.CreateDefault();
            for (int i = 0; i < StockMarket.RecentSettledKept + 3; i++)
                market.AddSettledResult(1, $"result {i}");

            var kept = market.RecentSettled[1];
            Assert.Equal(StockMarket.RecentSettledKept, kept.Count);
            Assert.Equal($"result {StockMarket.RecentSettledKept + 2}", kept[0]);
        }
    }
}
