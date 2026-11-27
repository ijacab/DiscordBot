using DiscordBot.Models;
using Newtonsoft.Json;
using System;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>The house tiers and businesses: the catalog, old saves, and that every kind is worth holding next to a bond.</summary>
    public class PropertyTierTests
    {
        private static double ProfitShare(PropertyKind kind) => PropertyCatalog.ProfitPerHour(kind, 1.0) / PropertyCatalog.Price(kind, 1.0);

        [Fact]
        public void OldSaves_LoadAsTheSuburbanHouse()
        {
            var holding = JsonConvert.DeserializeObject<PropertyHolding>("{\"Kind\":0,\"PurchaseCost\":992750}");
            Assert.Equal(PropertyKind.House, holding.Kind);
            Assert.Equal(PropertyCatalog.HousePrice, PropertyCatalog.Price(holding.Kind, 1.0));
            Assert.Equal("House", PropertyCatalog.DisplayName(holding.Kind));
        }

        [Fact]
        public void UnknownKinds_ReadAsAHouse_AndApartmentsCantBeBought()
        {
            Assert.Equal(PropertyKind.House, PropertyCatalog.TypeOf((PropertyKind)99).Kind);
            Assert.DoesNotContain(PropertyCatalog.Buyable, t => t.Kind == PropertyKind.Apartment);
            Assert.Null(PropertyCatalog.Find("apartment"));
        }

        [Theory]
        [InlineData("mansion", PropertyKind.Mansion)]
        [InlineData("Casinos", PropertyKind.Casino)]
        [InlineData("café", PropertyKind.Cafe)]
        [InlineData("food-truck", PropertyKind.FoodTruck)]
        [InlineData("house", PropertyKind.House)]
        [InlineData("lux", PropertyKind.Luxury)]
        [InlineData("luxury home", PropertyKind.Luxury)]
        public void Find_ReadsTheWordsPeopleType(string word, PropertyKind kind) => Assert.Equal(kind, PropertyCatalog.Find(word).Kind);

        [Fact]
        public void EveryKind_HasItsOwnNameAndWords()
        {
            var types = PropertyCatalog.Types;
            Assert.Equal(types.Count, types.Select(t => t.Kind).Distinct().Count());
            Assert.Equal(types.Sum(t => t.Words.Length), types.SelectMany(t => t.Words).Distinct().Count());
        }

        [Fact]
        public void EveryKind_PaysWellAheadOfABond()
        {
            foreach (var type in PropertyCatalog.Buyable)
                Assert.True(ProfitShare(type.Kind) >= 1.5 * Stock.BondGrowthPerHour, $"{type.Name} pays {ProfitShare(type.Kind):P3} an hour");
        }

        [Fact]
        public void EveryKind_StaysInTheHouseEconomysFairRange()
        {
            //the same limits as PropertyTests.HouseEconomy_StaysInAFairRange, which stopped rent paying a house back many times over
            double costsOfRoundTrip = PropertyCatalog.StampDutyRate + PropertyCatalog.SellingCostRate;
            foreach (var type in PropertyCatalog.Buyable)
            {
                double perHour = ProfitShare(type.Kind);
                Assert.InRange(perHour, 0.0015, 0.005);
                Assert.True(costsOfRoundTrip / perHour > 18, $"{type.Name} earns back its buying and selling costs too fast");
                Assert.True(perHour * 24 * 14 < 1.5, $"{type.Name} pays back too much in two weeks");
            }
        }

        [Fact]
        public void BiggerHouses_EarnLessPerDollar_ButAreHarderToRob()
        {
            var houses = PropertyCatalog.Buyable.Where(t => !t.IsBusiness).OrderBy(t => t.BasePrice).ToList();
            for (int i = 1; i < houses.Count; i++)
            {
                Assert.True(ProfitShare(houses[i].Kind) < ProfitShare(houses[i - 1].Kind));
                Assert.True(houses[i].Security > houses[i - 1].Security);
            }
        }

        [Fact]
        public void Businesses_EarnMoreThanTheHouseOfTheirTier_AndMoveMore()
        {
            var houses = PropertyCatalog.Buyable.Where(t => !t.IsBusiness).OrderBy(t => t.BasePrice).ToList();
            var businesses = PropertyCatalog.Buyable.Where(t => t.IsBusiness).OrderBy(t => t.BasePrice).ToList();
            Assert.Equal(houses.Count, businesses.Count);
            for (int i = 0; i < houses.Count; i++)
            {
                Assert.True(ProfitShare(businesses[i].Kind) > ProfitShare(houses[i].Kind), businesses[i].Name);
                Assert.True(businesses[i].Security < houses[i].Security, businesses[i].Name);
            }
            double houseMove = PropertyCatalog.Price(PropertyKind.House, 0.5) / PropertyCatalog.Price(PropertyKind.House, 1.0);
            double cafeMove = PropertyCatalog.Price(PropertyKind.Cafe, 0.5) / PropertyCatalog.Price(PropertyKind.Cafe, 1.0);
            Assert.True(cafeMove < houseMove, "a crash should hit a business harder");
        }

        [Fact]
        public void BusinessTakings_SwingAroundTheirAverage_AndHouseRentDoesnt()
        {
            double average = PropertyCatalog.NetRentPerHour(PropertyKind.Cafe, 1.0);
            double mean = Enumerable.Range(0, 1000).Average(i => PropertyCatalog.SwungRent(PropertyKind.Cafe, 1.0, (i + 0.5) / 1000));
            Assert.Equal(average, mean, 4);
            Assert.Equal(average * (1 - PropertyCatalog.BusinessIncomeSwing), PropertyCatalog.SwungRent(PropertyKind.Cafe, 1.0, 0), 4);
            Assert.Equal(PropertyCatalog.NetRentPerHour(PropertyKind.House, 1.0), PropertyCatalog.SwungRent(PropertyKind.House, 1.0, 0), 6);
        }

        [Fact]
        public void RentHour_PaysABusinessItsSwungTakings()
        {
            var data = new CoinAccounts { PropertyIndex = 1.0 };
            var owner = new CoinAccount { UserId = 1, Name = "a" };
            owner.Properties.Add(new PropertyHolding { Kind = PropertyKind.Cafe });
            data.Accounts.Add(owner);
            DiscordBot.Services.PropertyService.PayRentOneHour(data, DateTime.UtcNow, () => 1.0);
            Assert.Equal(PropertyCatalog.NetRentPerHour(PropertyKind.Cafe, 1.0) * (1 + PropertyCatalog.BusinessIncomeSwing), owner.NetWorth, 2);
        }
    }
}
