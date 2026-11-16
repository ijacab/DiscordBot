using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    /// <summary>Crypto's mega booms and crashes (10x up to 100,000x), its 1 cent floor and its deep trading impact.</summary>
    public class CryptoMegaTests
    {
        private static Func<double> Rolls(params double[] values)
        {
            int next = 0;
            return () => values[next++]; //throws if the stock draws more numbers than expected
        }

        private static Stock Coin(double price = 100, double basePrice = 100) => new Stock
        {
            Symbol = "COIN", RiskTier = Stock.CryptoTier, Role = StockRole.Normal, Price = price, FairPrice = price, PreviousPrice = price,
            BasePrice = basePrice, SharesOutstanding = Stock.CryptoSharesOutstanding,
        };

        //idle roll: below 0.0225 a small crash, below 0.045 a small moon, then a mega crash (to 0.049), then a mega boom (to 0.053)
        private const double MegaCrashRoll = 0.046, MegaBoomRoll = 0.051;

        [Fact]
        public void AMegaBoom_RampsOverTwoToSixTicks_AndStartsAtTenTimes()
        {
            var coin = Coin();
            //noise, event (mega boom), size (smallest: 10x), length (2 ticks)
            coin.ApplyTick(Rolls(0.5, MegaBoomRoll, 0.0, 0.0));
            Assert.Equal(1, coin.MegaTicks);
            Assert.Equal(Math.Sqrt(10) * 100, coin.FairPrice, 1); //half of the 10x in log terms

            coin.ApplyTick(Rolls(0.5)); //only the noise is drawn mid move
            Assert.Equal(0, coin.MegaTicks);
            Assert.Equal(1000, coin.FairPrice, 0); //10x on 100
        }

        [Fact]
        public void AMegaCrash_DividesThePriceAndDoesNotStartTheSmallCrashRebound()
        {
            var coin = Coin();
            //noise, event (mega crash), size (smallest), length (6 ticks)
            coin.ApplyTick(Rolls(0.5, MegaCrashRoll, 0.0, 0.999999));
            Assert.Equal(-5, coin.MegaTicks);
            for (int i = 0; i < 5; i++) coin.ApplyTick(Rolls(0.5));

            Assert.Equal(0, coin.MegaTicks);
            Assert.Equal(10, coin.FairPrice, 0); //a tenth
            Assert.Equal(0, coin.CrashRecoveryTicks); //no special rebound: it fades back at the normal pull
        }

        [Fact]
        public void TheSmallStreaksKeepTheirOdds()
        {
            var moon = Coin();
            moon.ApplyTick(Rolls(0.5, 0.03, 0.0, 0.0)); //a small moon at 0.03, as before
            Assert.True(moon.MomentumTicks > 0);
            Assert.Equal(0, moon.MegaTicks);

            var crash = Coin();
            crash.ApplyTick(Rolls(0.5, 0.0, 0.0, 0.999999));
            Assert.True(crash.MomentumTicks < 0);
            Assert.Equal(0, crash.MegaTicks);
        }

        [Fact]
        public void MegaSizes_AreAtLeastTenX_OneInTenReachesAHundredAndNoneAreCapped_UntilAHundredThousand()
        {
            Assert.Equal(Math.Log(10), Stock.MegaSize(0), 9);
            int n = 100_000, over100 = 0, over1000 = 0, over10000 = 0;
            double max = 0;
            for (int i = 0; i < n; i++)
            {
                double size = Stock.MegaSize((i + 0.5) / n);
                max = Math.Max(max, size);
                if (size >= Math.Log(100)) over100++;
                if (size >= Math.Log(1000)) over1000++;
                if (size >= Math.Log(10_000)) over10000++;
            }
            Assert.InRange(over100 / (double)n, 0.095, 0.105);
            Assert.InRange(over1000 / (double)n, 0.0095, 0.0105);
            Assert.InRange(over10000 / (double)n, 0.00095, 0.00105);
            Assert.Equal(Math.Log(100_000), max, 6); //nothing goes past 100,000x
            Assert.Equal(Math.Log(100_000), Stock.MegaSize(0.9999999999), 6);
        }

        [Fact]
        public void ASeason_HasAboutTenBoomsAndTenCrashes_SomeReachHundredsOfTimes_AndNothingBreaks()
        {
            const int seasons = 200, ticks = 6_048; //three weeks of 5 minute ticks
            var random = new Random(8_675_309);
            int events = 0, big = 0, huge = 0, seasonsWithA100xBoom = 0;
            double highest = 0, lowest = double.MaxValue;
            for (int s = 0; s < seasons; s++)
            {
                var coin = Coin(30, 30);
                bool hundredx = false;
                int before = 0;
                for (int t = 0; t < ticks; t++)
                {
                    coin.ApplyTick(random.NextDouble);
                    if (coin.MegaTicks != 0 && before == 0)
                    {
                        events++;
                        double size = coin.MegaStep * (Math.Abs(coin.MegaTicks) + 1);
                        if (size >= Math.Log(100)) big++;
                        if (size >= Math.Log(1000)) huge++;
                        if (coin.MegaTicks > 0 && size >= Math.Log(100)) hundredx = true;
                    }
                    before = coin.MegaTicks;
                    highest = Math.Max(highest, coin.FairPrice);
                    lowest = Math.Min(lowest, coin.FairPrice);
                    Assert.True(coin.FairPrice >= Stock.CryptoMinPrice && !double.IsNaN(coin.FairPrice) && !double.IsInfinity(coin.FairPrice));
                }
                if (hundredx) seasonsWithA100xBoom++;
            }

            double perSeason = events / (double)seasons;
            Assert.InRange(perSeason, 17, 25); //about 10 booms and 10 crashes a season
            Assert.InRange(big / (double)events, 0.07, 0.13); //about 1 in 10 reaches 100x
            Assert.InRange(huge / (double)events, 0.004, 0.02); //about 1 in 100 reaches 1,000x
            Assert.True(seasonsWithA100xBoom > seasons * 0.4, $"only {seasonsWithA100xBoom} of {seasons} seasons had a 100x boom");
            Assert.True(highest > 30 * 100, $"never went past {highest}");
            Assert.True(lowest <= 1, $"never crashed below $1 (lowest {lowest})");
        }

        [Fact]
        public void CryptoHasAOneCentFloor_OtherStocksKeepADollar()
        {
            var coin = Coin(0.02, 30);
            coin.ApplyTick(Rolls(0.5, MegaCrashRoll, 0.0, 0.0)); //a mega crash over 2 ticks on a $0.02 coin: the first step is a third
            Assert.Equal(Stock.CryptoMinPrice, coin.FairPrice);
            Assert.Equal(Stock.CryptoMinPrice, coin.Price);

            var normal = new Stock { Symbol = "N", RiskTier = 3, Price = 1.0, FairPrice = 1.0, PreviousPrice = 1.0, BasePrice = 50 };
            Assert.Equal(Stock.MinPrice, normal.FloorPrice);
            normal.ApplyTick(Rolls(0.0)); //the biggest drop
            Assert.True(normal.FairPrice >= Stock.MinPrice);
        }

        [Fact]
        public void PricesUnderADollarAreKeptToFourDecimals()
        {
            var coin = Coin(0.0134, 30);
            coin.RefreshQuote();
            Assert.Equal(0.0134, coin.Price);
            Assert.Equal(0.1235, Stock.RoundPrice(0.12349), 6);
            Assert.Equal(12.35, Stock.RoundPrice(12.3456), 6);
            Assert.True(coin.PreviewTrade(10).Fill > 0.0134); //bought at the real price, not rounded down to a cent

            Assert.Equal("0.0134", Stock.FormatPrice(0.0134));
            Assert.Equal("12.35", Stock.FormatPrice(12.3456));
        }

        [Fact]
        public void CryptoImpactHasNoFiftyPercentCap_AndSellsStopAtNinetyFivePercentDown()
        {
            var coin = Coin(10, 10);
            var ordinary = new Stock { Symbol = "N", RiskTier = 3, Price = 10, FairPrice = 10, PreviousPrice = 10, BasePrice = 10, SharesOutstanding = Stock.CryptoSharesOutstanding };

            Assert.Equal(Stock.MaxImpact, ordinary.PreviewTrade(1_000_000).NewImpact); //capped
            Assert.True(coin.PreviewTrade(1_000_000).NewImpact > 1); //not capped: 50 floats' worth, 1.13

            Assert.Equal(-Stock.CryptoMaxSellImpact, coin.PreviewTrade(-50_000_000).NewImpact);
            Assert.True(coin.PreviewTrade(-50_000_000).Fill >= Stock.CryptoMinPrice);
        }

        [Fact]
        public void ABigCryptoOrderPaysForItsOwnImpact_MoreThanTheMidpoint()
        {
            var coin = Coin(10, 10);
            //selling 100,000 shares (5 floats): the quote falls 36% by the end of the order, and the fill is the average along the way
            var (fill, impact) = coin.PreviewTrade(-100_000, spread: 0);
            double midpointFill = (coin.Price + 10 * (1 + impact)) / 2;
            Assert.True(fill < midpointFill, $"fill {fill} vs midpoint {midpointFill}");
            Assert.True(fill > 10 * (1 + impact)); //still above where the quote ends up

            double buy = coin.PreviewTrade(100_000, spread: 0).Fill;
            Assert.True(buy > (10 + 10 * (1 + 0.16 * Math.Sqrt(5))) / 2); //and a big buy pays more than the midpoint
        }

        [Fact]
        public void CryptoCanBeBoughtAndSoldNormallyWhenSmall()
        {
            var coin = Coin(30, 30);
            var (buyFill, _) = coin.PreviewTrade(100);
            var (sellFill, _) = coin.PreviewTrade(-100);
            Assert.InRange(buyFill, 30.0, 30.5);
            Assert.InRange(sellFill, 29.5, 30.0);
        }

        [Theory]
        [InlineData(0, "0%")]
        [InlineData(250, "+250%")]
        [InlineData(-90, "-90%")]
        [InlineData(12_345, "+12.3K%")]
        [InlineData(-4_500_000, "-4.5M%")]
        [InlineData(2_000_000_000, "+2B%")]
        public void ChartPercentLabels_StayShortForGiantMoves(double percent, string expected) =>
            Assert.Equal(expected, StockChart.FormatPercentLabel(percent));

        [Fact]
        public void AThousandXBoomStillDrawsAChart()
        {
            var history = new List<double> { 30 };
            for (int i = 0; i < 40; i++) history.Add(history[^1] * (i < 10 ? 1.9 : 0.97));
            double first = history[0];
            var series = new List<IReadOnlyList<double>> { history.Select(p => (p - first) / first * 100).ToList() };
            using var png = StockChart.RenderMultiPng(series, TimeSpan.FromMinutes(5), StockChart.FormatPercentLabel);
            Assert.True(png.Length > 1_000);
        }
    }
}
