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
    }
}
