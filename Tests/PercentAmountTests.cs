using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>Amounts written as a share, like ".buy MOONX 50%".</summary>
    public class PercentAmountTests
    {
        private static (StockService Stocks, CoinAccount Account, string Symbol) Setup(double cash, ulong id)
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var stocks = new StockService(new LocalFileService(), coins);
            stocks.ClearAll().Wait();
            var account = new CoinAccount { UserId = id, Name = "pct", NetWorth = cash };
            coins.GetAll().Accounts.Add(account);
            return (stocks, account, stocks.GetMarket().Stocks.First(s => s.Role == StockRole.Normal).Symbol);
        }

        [Theory]
        [InlineData("50%", 0.5)]
        [InlineData("12.5%", 0.125)]
        [InlineData("100%", 1.0)]
        public void ParsesAPercentage(string text, double expected)
        {
            Assert.True(StockService.TryParsePercent(text, out double fraction));
            Assert.Equal(expected, fraction, 9);
        }

        [Theory]
        [InlineData("10")]
        [InlineData("max")]
        public void AnythingWithoutAPercentSignIsNotOne(string text) => Assert.False(StockService.TryParsePercent(text, out _));

        [Theory]
        [InlineData("0%")]
        [InlineData("-5%")]
        [InlineData("101%")]
        [InlineData("%")]
        [InlineData("abc%")]
        public void ABadPercentageIsASyntaxError(string text) => Assert.Throws<BadSyntaxException>(() => StockService.TryParsePercent(text, out _));

        [Fact]
        public void BuyingWithHalfYourCash_SpendsAboutHalfAndNoMore()
        {
            var (stocks, account, symbol) = Setup(100_000, 910_001);
            var (_, shares, cost) = stocks.Buy(account, symbol, "50%");

            Assert.True(shares > 0);
            Assert.True(cost <= 50_000, $"spent {cost}");
            Assert.True(cost > 50_000 * 0.9, $"spent only {cost}");
            Assert.Equal(100_000 - cost, account.NetWorth, 2);
        }

        [Fact]
        public void BuyingWithAllYourCash_IsTheSameAsMax()
        {
            var (stocks, account, symbol) = Setup(100_000, 910_002);
            int expected = stocks.GetMarket().Find(symbol).MaxAffordableShares(100_000, Stock.Spread, int.MaxValue);
            Assert.Equal(expected, stocks.Buy(account, symbol, "100%").Shares);
        }

        [Fact]
        public void ATinyPercentageThatCantBuyAShare_IsRefusedAndNothingChanges()
        {
            var (stocks, account, symbol) = Setup(1_000, 910_004);
            Assert.Throws<BadInputException>(() => stocks.Buy(account, symbol, "0.01%"));
            Assert.Equal(1_000, account.NetWorth);
        }

        [Fact]
        public void SellingAPercentageOfAHolding_RoundsDown()
        {
            var (stocks, account, symbol) = Setup(1_000_000, 910_005);
            stocks.GetMarket().AddShares(account.UserId, symbol, 7);

            Assert.Equal(3, stocks.Sell(account, symbol, "50%").Shares);
            Assert.Equal(4, stocks.GetMarket().GetShares(account.UserId, symbol));
            Assert.Equal(4, stocks.Sell(account, symbol, "100%").Shares);
        }

        [Fact]
        public void SellingAPercentageThatIsUnderOneShare_IsRefused()
        {
            var (stocks, account, symbol) = Setup(1_000_000, 910_006);
            stocks.GetMarket().AddShares(account.UserId, symbol, 3);
            Assert.Throws<BadInputException>(() => stocks.Sell(account, symbol, "10%"));
            Assert.Equal(3, stocks.GetMarket().GetShares(account.UserId, symbol));
        }

        [Fact]
        public void ShortingAndCoveringAndOptionsTakeAPercentageToo()
        {
            var (stocks, account, symbol) = Setup(200_000, 910_007);
            var (_, shorted, margin, tax) = stocks.Short(account, symbol, "25%");
            Assert.True(shorted > 0);
            Assert.True(margin + tax <= 50_000);

            Assert.Equal(shorted / 2, stocks.Cover(account, symbol, "50%").Shares);

            double cash = account.NetWorth;
            var (bet, _) = stocks.OpenBet(account, 0, symbol, true, "50%", 2, TimeSpan.FromHours(1));
            Assert.Equal(Math.Floor(cash * 0.5 * 100) / 100, bet.Paid, 2);
            Assert.Equal(cash - bet.Paid, account.NetWorth, 2);
        }
    }
}
