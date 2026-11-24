using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>`.sell property all`.</summary>
    public class SellAllPropertyTests
    {
        private static (CoinAccounts Data, CoinAccount Account, PropertyService Properties) Setup(int houses, double cash = 0, double debt = 0)
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            var account = new CoinAccount { UserId = 77, Name = "owner", NetWorth = cash, Debt = debt };
            for (int i = 0; i < houses; i++) account.Properties.Add(new PropertyHolding { Kind = PropertyKind.House });
            coins.GetAll().Accounts.Add(account);
            return (coins.GetAll(), account, new PropertyService(coins));
        }

        [Fact]
        public async Task SellAll_SellsEveryHouseAtMarketLessCosts()
        {
            var (data, account, properties) = Setup(3, cash: 1_000);
            double each = PropertyCatalog.SaleValue(PropertyKind.House, data.PropertyIndex);

            var (sold, left, proceeds) = await properties.SellAll(account);

            Assert.Equal((3, 0), (sold, left));
            Assert.Equal(3 * each, proceeds, 2);
            Assert.Empty(account.Properties);
            Assert.Equal(1_000 + 3 * each, account.NetWorth, 2);
            Assert.Equal(3, account.Stats.PropertiesSold);
        }

        [Fact]
        public async Task SellAll_PaysDebtFirst_LikeAnyOtherSale()
        {
            var (data, account, properties) = Setup(2, cash: 0, debt: 50_000);
            double each = PropertyCatalog.SaleValue(PropertyKind.House, data.PropertyIndex);

            await properties.SellAll(account);

            Assert.Equal(0, Math.Max(0, 50_000 - 2 * each) - account.Debt, 2);
            Assert.Equal(Math.Max(0, 2 * each - 50_000), account.NetWorth, 2);
        }

        [Fact]
        public async Task SellAll_LeavesAHouseWithSquattersInAndSellsTheRest()
        {
            var (_, account, properties) = Setup(3);
            var squatted = account.Properties[1];
            account.AddDebuff(PowerUpIds.Squatted, DateTime.UtcNow, 2, squatted.Id);

            var (sold, left, _) = await properties.SellAll(account);

            Assert.Equal((2, 1), (sold, left));
            Assert.Same(squatted, Assert.Single(account.Properties));
        }

        [Fact]
        public async Task SellAll_RefusesWhenThereIsNothingToSellOrEveryHouseHasSquatters()
        {
            var (_, none, properties) = Setup(0);
            await Assert.ThrowsAsync<BadInputException>(() => properties.SellAll(none));

            var (_, stuck, properties2) = Setup(2);
            foreach (var house in stuck.Properties) stuck.AddDebuff(PowerUpIds.Squatted, DateTime.UtcNow, 2, house.Id);
            await Assert.ThrowsAsync<BadInputException>(() => properties2.SellAll(stuck));
            Assert.Equal(2, stuck.Properties.Count);
        }
    }
}
