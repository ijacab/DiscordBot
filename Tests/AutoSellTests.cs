using Common.Services;
using DiscordBot;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>What `.autosell on` sells to pay off debt.</summary>
    public class AutoSellTests
    {
        private static DebtService.StockLot Lot(string symbol, int shares, double net) =>
            new DebtService.StockLot { Symbol = symbol, Shares = shares, NetPerShare = net };

        private static (PropertyHolding, double) Home(PropertyKind kind, double value) => (new PropertyHolding { Kind = kind }, value);

        [Fact]
        public void Plan_NoDebtSellsNothing()
        {
            var plan = DebtService.Plan(0, new[] { Lot("AAA", 100, 10) }, new[] { Home(PropertyKind.House, 900_000) });
            Assert.True(plan.IsEmpty);
        }

        [Fact]
        public void Plan_SellsOnlyEnoughStockFromTheBiggestHoldingFirst()
        {
            var plan = DebtService.Plan(500, new[] { Lot("SMALL", 10, 10), Lot("BIG", 1000, 10) }, new[] { Home(PropertyKind.House, 900_000) });

            Assert.Single(plan.Stocks);
            Assert.Equal("BIG", plan.Stocks[0].Symbol);
            Assert.InRange(plan.Stocks[0].Shares, 50, 52); //$500 plus a small buffer for the price falling as it sells
            Assert.Empty(plan.Properties);
        }

        [Fact]
        public void Plan_SellsPropertyOnlyWhenStocksFallShort_TheCheapestThatCovers()
        {
            var apartment = Home(PropertyKind.Apartment, 700_000);
            var house = Home(PropertyKind.House, 900_000);

            var plan = DebtService.Plan(600_000, new[] { Lot("AAA", 100, 10) }, new[] { house, apartment });

            Assert.Equal(100, plan.Stocks.Single().Shares);
            Assert.Same(apartment.Item1, plan.Properties.Single());
        }

        [Fact]
        public void Plan_SellsTheBiggestPropertyFirstWhenNoneCoversTheDebtAlone()
        {
            var apartment = Home(PropertyKind.Apartment, 700_000);
            var house = Home(PropertyKind.House, 900_000);

            var plan = DebtService.Plan(1_200_000, new List<DebtService.StockLot>(), new[] { apartment, house });

            Assert.Equal(new[] { house.Item1, apartment.Item1 }, plan.Properties);
        }

        [Fact]
        public void Plan_SellsEverythingWhenTheDebtIsBiggerThanItAll()
        {
            var plan = DebtService.Plan(10_000_000, new[] { Lot("AAA", 100, 10) }, new[] { Home(PropertyKind.House, 900_000) });
            Assert.Equal(100, plan.Stocks.Single().Shares);
            Assert.Single(plan.Properties);
        }

        [Fact]
        public async System.Threading.Tasks.Task AutoSell_LeavesAPropertyWithSquattersInAlone()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            var account = new CoinAccount { UserId = 1, Name = "owner", NetWorth = 0, Debt = 50_000, AutoSellForDebt = true };
            var squatted = new PropertyHolding { Kind = PropertyKind.House };
            var free = new PropertyHolding { Kind = PropertyKind.House };
            account.Properties.Add(squatted);
            account.Properties.Add(free);
            account.AddDebuff(PowerUpIds.Squatted, DateTime.UtcNow, 2, squatted.Id);
            coins.GetAll().Accounts.Add(account);
            var debts = new DebtService(coins, new StockService(new LocalFileService(), coins), new PropertyService(coins));

            await debts.AutoSellForDebts(new ulong[0]);

            Assert.Single(account.Properties);
            Assert.Same(squatted, account.Properties[0]); //the one the squatters are in is kept, the other was sold
        }

        private static (DebtService Debts, CoinAccount Account, StockService Stocks) Indebted(double invested, double debt, ulong id)
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var stocks = new StockService(new LocalFileService(), coins);
            stocks.ClearAll().Wait();
            coins.GetAll().Accounts.Clear();
            var account = new CoinAccount { UserId = id, Name = "debtor", NetWorth = invested, AutoSellForDebt = true };
            coins.GetAll().Accounts.Add(account);
            stocks.Buy(account, stocks.GetMarket().Stocks.First(x => x.Role == StockRole.Normal).Symbol, "max");
            account.NetWorth = 0;
            account.Debt = debt;
            return (new DebtService(coins, stocks, new PropertyService(coins)), account, stocks);
        }

        [Theory]
        [InlineData(1_000_000, 5_000)]
        [InlineData(1_000_000, 400_000)]
        [InlineData(50_000, 40_000)]
        [InlineData(10_000_000, 3_000_000)]
        public async System.Threading.Tasks.Task AutoSell_SellsEnoughStocksToClearTheDebtExactly(double invested, double debt)
        {
            var (debts, account, stocks) = Indebted(invested, debt, 920_000 + (ulong)debt % 1000);

            await debts.AutoSellForDebts(new ulong[0]);

            Assert.Equal(0, account.Debt, 2); //not a small remainder left over from the price moving as it sold
            Assert.True(stocks.GetMarket().GetHoldings(account.UserId).Any(), "it sold everything instead of only what was needed");
        }

        [Fact]
        public void Plan_GoesStocksThenShortsThenOptionsThenProperty()
        {
            var shorts = new[] { new DebtService.ShortLot { Symbol = "SHRT", Shares = 100, NetPerShare = 10 } };
            var options = new[] { new DebtService.OptionLot { Id = "opt1", Value = 500 }, new DebtService.OptionLot { Id = "dud", Value = 0 } };

            var small = DebtService.Plan(1_500, new[] { Lot("AAA", 100, 10) }, new[] { Home(PropertyKind.House, 900_000) }, shorts, options);
            Assert.Single(small.Stocks);
            Assert.Single(small.Shorts);
            Assert.Empty(small.Options);
            Assert.Empty(small.Properties);

            var big = DebtService.Plan(2_600, new[] { Lot("AAA", 100, 10) }, new[] { Home(PropertyKind.House, 900_000) }, shorts, options);
            Assert.Equal(100, big.Shorts.Single().Shares);
            Assert.Equal(new[] { "opt1" }, big.Options); //a worthless option is never sold
            Assert.Single(big.Properties);
        }

        [Fact]
        public async System.Threading.Tasks.Task AutoSell_CoversShortsAndCashesOutOptionsToPayDebt()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var stocks = new StockService(new LocalFileService(), coins);
            await stocks.ClearAll();
            coins.GetAll().Accounts.Clear();
            var account = new CoinAccount { UserId = 77, Name = "shorter", NetWorth = 1_000_000, AutoSellForDebt = true };
            coins.GetAll().Accounts.Add(account);
            var normal = stocks.GetMarket().Stocks.Where(x => x.Role == StockRole.Normal).ToList();
            stocks.Short(account, normal[0].Symbol, "100");
            stocks.OpenBet(account, 0, normal[1].Symbol, true, "10000", 2, TimeSpan.FromHours(1));
            stocks.OpenBet(account, 0, normal[1].Symbol, false, "10000", 2, TimeSpan.FromHours(1));
            normal[1].FairPrice *= 1.6; //the call is now well up and the put wiped out
            double shortEquity = stocks.GetMarket().GetShort(account.UserId, normal[0].Symbol).EquityAt(normal[0].Price);
            double callValue = stocks.GetMarket().GetBets(account.UserId).Single(b => b.IsCall).ValueAt(normal[1].FairPrice);
            account.NetWorth = 0;
            account.Debt = Math.Floor((shortEquity + callValue) * 0.9);

            var lines = await new DebtService(coins, stocks, new PropertyService(coins)).AutoSellForDebts(new ulong[0]);

            Assert.Equal(0, account.Debt, 2);
            Assert.Null(stocks.GetMarket().GetShort(account.UserId, normal[0].Symbol));
            Assert.DoesNotContain(stocks.GetMarket().GetBets(account.UserId), b => b.IsCall);
            Assert.Contains(stocks.GetMarket().GetBets(account.UserId), b => !b.IsCall); //worthless, so kept
            Assert.Contains("short", lines.Single());
        }
    }
}
