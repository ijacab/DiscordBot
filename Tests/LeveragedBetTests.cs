using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class LeveragedBetTests
    {
        private static Stock At(int tier, double ratio) => new Stock
        {
            Symbol = $"T{tier}x{ratio}", Name = "test", RiskTier = tier, Role = StockRole.Normal, BasePrice = 100, FairPrice = 100 * ratio, Price = 100 * ratio,
        };

        [Fact]
        public void Payout_IsLeverageTimesTheMove_FlooredAtZeroAndCapped()
        {
            Assert.Equal(1_500, LeverageRules.Value(1_000, true, 5, 100, 110), 2);   //+10% at 5x is +50%
            Assert.Equal(500, LeverageRules.Value(1_000, true, 5, 100, 90), 2);
            Assert.Equal(0, LeverageRules.Value(1_000, true, 5, 100, 79), 2);        //wiped out past -20%
            Assert.Equal(10_000, LeverageRules.Value(1_000, true, 5, 100, 1_000), 2); //capped at 10x
            Assert.Equal(1_500, LeverageRules.Value(1_000, false, 5, 100, 90), 2);   //a put wins on the drop
            Assert.Equal(80, LeverageRules.WipeoutPrice(true, 5, 100), 6);
            Assert.Equal(120, LeverageRules.WipeoutPrice(false, 5, 100), 6);
            Assert.Equal(280, LeverageRules.MaxOutPrice(true, 5, 100).Value, 6);
            Assert.Null(LeverageRules.MaxOutPrice(false, 5, 100)); //a 5x put tops out at 6x when the stock hits 0
            Assert.Equal(10, LeverageRules.MaxOutPrice(false, 10, 100).Value, 6);
        }

        [Theory]
        [InlineData(3, 1.0, 5, 12)]
        [InlineData(3, 0.85, 10, 72)]
        [InlineData(4, 1.15, 5, 12)]
        [InlineData(5, 1.0, 2, 72)]
        [InlineData(5, 1.0, 10, 12)]
        [InlineData(6, 1.0, 5, 12)]
        [InlineData(6, 0.85, 2, 72)]
        public void TheHouseKeepsItsEdge_WithAnIndependentSimulation(int tier, double ratio, int leverage, int ticks)
        {
            foreach (bool isCall in new[] { true, false })
            {
                var stock = At(tier, ratio);
                double expected = LeveragePricer.ExpectedMultiple(stock, isCall, leverage, ticks, MarketDrift.None);
                double riding = LeverageRules.RidingShare(expected);
                if (1 - riding > LeverageRules.MaxFeeShare) continue; //not for sale
                int paths = stock.IsCrypto ? 40_000 : 20_000;
                double realised = LeveragePricer.Simulate(stock, isCall, leverage, ticks, MarketDrift.None, paths, seed: 9_001 + tier);
                double paybackPerDollarPaid = realised * riding;
                string what = $"tier {tier} at {ratio} {leverage}x {(isCall ? "call" : "put")}";
                Assert.True(paybackPerDollarPaid < 1.0, $"{what}: pays back {paybackPerDollarPaid:0.000} per dollar");
                //the quote players see is close to what really happens
                Assert.True(Math.Abs(realised - expected) <= 0.12 * Math.Max(expected, 0.1), $"{what}: quoted {expected:0.000}, realised {realised:0.000}");
            }
        }

        [Fact]
        public void BreakEven_AccountsForTheFee()
        {
            //half of it rides at 5x: the stock has to rise 20% just to get back what was paid
            Assert.Equal(0.2, LeverageRules.BreakEvenMove(0.5, 5), 6);
            Assert.Equal(0.98, LeverageRules.RidingShare(0.5), 6); //the minimum fee
        }

        private static (StockService Stocks, CoinAccount Account, Stock Stock) Market(double cash)
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var stocks = new StockService(new LocalFileService(), coins);
            stocks.ClearAll().Wait();
            coins.GetAll().Accounts.Clear();
            var account = new CoinAccount { UserId = 5, Name = "gambler", NetWorth = cash };
            coins.GetAll().Accounts.Add(account);
            return (stocks, account, stocks.GetMarket().Stocks.First(s => s.Role == StockRole.Normal && !s.IsCrypto));
        }

        [Fact]
        public void OpenBet_TakesTheCash_SendsTheFeeToTheJackpot_AndCountsInTheirWorth()
        {
            var (stocks, account, stock) = Market(10_000);
            var (bet, quote) = stocks.OpenBet(account, 0, stock.Symbol, true, "1000", 5, TimeSpan.FromHours(1));

            Assert.Equal(9_000, account.NetWorth, 2);
            Assert.Equal(1_000, bet.Paid);
            Assert.Equal(Math.Round(1_000 * quote.RidingShare, 2), bet.Stake);
            Assert.True(bet.Fee >= 1_000 * LeverageRules.MinFeeShare - 0.01);
            Assert.True(stocks.HasPositions(account.UserId));
            Assert.Equal(bet.Stake, stocks.GetPortfolioValue(account.UserId), 2);
        }

        [Theory]
        [InlineData("NaN")]
        [InlineData("-5")]
        [InlineData("0")]
        [InlineData("lots")]
        public void OpenBet_RefusesNonsenseAmounts(string amount)
        {
            var (stocks, account, stock) = Market(10_000);
            Assert.ThrowsAny<Exception>(() => stocks.OpenBet(account, 0, stock.Symbol, true, amount, 5, TimeSpan.FromHours(1)));
            Assert.Equal(10_000, account.NetWorth);
        }

        [Fact]
        public void OpenBet_RefusesMoreThanYourCashAndSafeHavens()
        {
            var (stocks, account, stock) = Market(10_000);
            Assert.Throws<BadInputException>(() => stocks.OpenBet(account, 0, stock.Symbol, true, "10001", 5, TimeSpan.FromHours(1)));
            Assert.Throws<BadInputException>(() => stocks.OpenBet(account, 0, "GOLD", true, "100", 5, TimeSpan.FromHours(1)));
            Assert.Throws<BadInputException>(() => stocks.OpenBet(account, 0, stock.Symbol, true, "100", 3, TimeSpan.FromHours(1)));
            Assert.Throws<BadInputException>(() => stocks.OpenBet(account, 0, stock.Symbol, true, "100", 5, TimeSpan.FromHours(48)));
        }

        [Fact]
        public void PayoutTable_ShowsWipeoutBreakEvenAndMax()
        {
            string text = CommandManager.BetPayoffText(true, 5, 100, 1_000, 950, TimeSpan.FromHours(1));
            Assert.Contains("$80", text);       //wiped out at -20%
            Assert.Contains("$0", text);
            Assert.Contains("$280", text);      //maxes out at +180%
            Assert.Contains("$9,500", text);    //10x what rides
            Assert.Contains("$50 fee", text);
            Assert.Contains("+1.1%", text);     //break even after the fee
        }
    }
}
