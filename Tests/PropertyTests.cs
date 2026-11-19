using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>
    /// Pricing, costs, the market step and daily rent. Buy, sell and the tick that saves are left out because they write
    /// coinaccounts.json, the same as the raffle tests avoid.
    /// </summary>
    public class PropertyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

        private static Func<double> Fixed(double value) => () => value;

        [Fact]
        public void AtBaseIndex_PricesAreTheCatalogMedians()
        {
            Assert.Equal(PropertyCatalog.HousePrice, PropertyCatalog.Price(PropertyKind.House, 1.0));
            Assert.Equal(PropertyCatalog.ApartmentPrice, PropertyCatalog.Price(PropertyKind.Apartment, 1.0));
        }

        [Fact]
        public void ApartmentsMoveMoreThanHouses()
        {
            double houseMove = PropertyCatalog.Price(PropertyKind.House, 1.2) / PropertyCatalog.Price(PropertyKind.House, 1.0) - 1;
            double apartmentMove = PropertyCatalog.Price(PropertyKind.Apartment, 1.2) / PropertyCatalog.Price(PropertyKind.Apartment, 1.0) - 1;
            Assert.True(apartmentMove > houseMove);
        }

        [Fact]
        public void BuyCost_IsPriceWithStampDutyOnTop()
        {
            Assert.Equal(950_000 * 1.045, PropertyCatalog.BuyCost(PropertyKind.House, 1.0), 2);
        }

        [Fact]
        public void SaleValue_IsPriceLessSellingCosts()
        {
            Assert.Equal(950_000 * 0.97, PropertyCatalog.SaleValue(PropertyKind.House, 1.0), 2);
        }

        [Fact]
        public void BuyingAndSellingStraightAway_LosesMoreThanTheCosts()
        {
            //there is no instant gain: the buyer pays 4.5% on the way in and gets 3% less on the way out
            double spent = PropertyCatalog.BuyCost(PropertyKind.Apartment, 1.0);
            double back = PropertyCatalog.SaleValue(PropertyKind.Apartment, 1.0);
            Assert.True(back < spent);
            Assert.Equal(spent - back, PropertyCatalog.ApartmentPrice * (PropertyCatalog.StampDutyRate + PropertyCatalog.SellingCostRate), 1);
        }

        [Fact]
        public void Step_DriftsWithTheTrend()
        {
            double index = 1.0;
            int direction = 1, daysLeft = 10;
            PropertyMarket.Step(ref index, ref direction, ref daysLeft, Fixed(0.5)); //noise is zero at 0.5
            Assert.Equal(1.0 + PropertyCatalog.TrendPerDay, index, 10);
            Assert.Equal(9, daysLeft);
        }

        [Fact]
        public void Step_FlipsToABustWhenTheBoomRunsOut()
        {
            double index = 1.0;
            int direction = 1, daysLeft = 0;
            PropertyMarket.Step(ref index, ref direction, ref daysLeft, Fixed(0.5));
            Assert.Equal(-1, direction);
            //MinTrendDays + half the range, minus the day just used
            int length = PropertyCatalog.MinTrendDays + (int)(0.5 * (PropertyCatalog.MaxTrendDays - PropertyCatalog.MinTrendDays + 1));
            Assert.Equal(length - 1, daysLeft);
            Assert.True(index < 1.0);
        }

        [Fact]
        public void Step_HasNoCeilingOrOrdinaryFloor_ButNeverReachesZero()
        {
            //the old bounds were 0.6 and 1.6 of the start
            double index = 1.0;
            int direction = 1, daysLeft = 100_000;
            for (int i = 0; i < 100; i++)
                PropertyMarket.Step(ref index, ref direction, ref daysLeft, Fixed(0.99));
            Assert.True(index > 1.6, $"index {index}");

            direction = -1;
            for (int i = 0; i < 100_000; i++)
                PropertyMarket.Step(ref index, ref direction, ref daysLeft, Fixed(0.01));
            Assert.True(index < 0.6);
            Assert.True(index >= PropertyCatalog.IndexFloor);
        }

        [Fact]
        public void Laplace_IsZeroAtTheMiddleSymmetricAndHasNoLimit()
        {
            Assert.Equal(0, PropertyMarket.Laplace(0.5, 0.02), 12);
            Assert.Equal(-PropertyMarket.Laplace(0.9, 0.02), PropertyMarket.Laplace(0.1, 0.02), 12);
            Assert.True(PropertyMarket.Laplace(0.99999, 0.02) > PropertyMarket.Laplace(0.999, 0.02));
            Assert.True(double.IsFinite(PropertyMarket.Laplace(1, 0.02)) && double.IsFinite(PropertyMarket.Laplace(0, 0.02)));
        }

        [Fact]
        public void DailyMoves_AreUsuallySmallButAShockDayCanMoveHousesALot()
        {
            var random = new Random(77);
            double index = 1.0;
            int direction = 1, daysLeft = 100;
            var moves = new System.Collections.Generic.List<double>();
            for (int i = 0; i < 20_000; i++)
            {
                double before = index;
                PropertyMarket.Step(ref index, ref direction, ref daysLeft, random.NextDouble);
                moves.Add(Math.Abs(Math.Log(index / before)));
                daysLeft = 100;
            }
            moves.Sort();
            Assert.InRange(moves[moves.Count / 2], 0.01, 0.06); //a typical day
            Assert.True(moves[moves.Count * 995 / 1000] > 0.10, "no day moved more than 10%");
            Assert.True(moves[moves.Count - 1] > 0.30, "no day moved more than 30%");
        }

        [Fact]
        public void DayNumber_CountsWholeUtcDays()
        {
            long day = PropertyMarket.DayNumber(Now);
            Assert.Equal(day + 1, PropertyMarket.DayNumber(Now.AddDays(1)));
            Assert.Equal(day, PropertyMarket.DayNumber(Now.Date.AddHours(23)));
        }

        [Fact]
        public void Rent_IsPaidOnEveryPropertyLessTheAgencyFee()
        {
            var account = new CoinAccount { UserId = 1, NetWorth = 0 };
            account.Properties.Add(new PropertyHolding { Kind = PropertyKind.House });
            var data = new CoinAccounts { PropertyIndex = 1.0 };
            data.Accounts.Add(account);

            PropertyService.PayRentOneHour(data);

            double gross = PropertyCatalog.GrossRentPerHour(PropertyKind.House, 1.0);
            Assert.Equal(gross * (1 - PropertyCatalog.AgencyFeeRate), account.NetWorth, 6);
        }

        [Fact]
        public void Rent_ForAHouseIsAboutRightAfterVacancyAndAgency()
        {
            //3.5% of the $950,000 price per hour is $33,250 gross, about $29,366 after the vacancy and agency
            double netRentPerHour = PropertyCatalog.GrossRentPerHour(PropertyKind.House, 1.0) * (1 - PropertyCatalog.AgencyFeeRate);
            Assert.InRange(netRentPerHour, 29_300, 29_400);
        }

        [Fact]
        public void Rent_IsTheHourlyShareOfThePrice()
        {
            Assert.Equal(PropertyCatalog.HouseGrossYieldPerHour * PropertyCatalog.BasePrice(PropertyKind.House), PropertyCatalog.RentPerHour(PropertyKind.House, 1.0), 6);
            Assert.Equal(PropertyCatalog.ApartmentGrossYieldPerHour * PropertyCatalog.BasePrice(PropertyKind.Apartment), PropertyCatalog.RentPerHour(PropertyKind.Apartment, 1.0), 6);
        }

        [Fact]
        public void Rent_FollowsTheMarketPrice()
        {
            Assert.Equal(1.2, PropertyCatalog.RentPerHour(PropertyKind.House, 1.2) / PropertyCatalog.RentPerHour(PropertyKind.House, 1.0), 6);
        }

        [Fact]
        public void Rent_IsPaidOnPropertySavedAsNotRentedOut()
        {
            //saves from before renting was automatic still carry "LetOut": false
            var data = Newtonsoft.Json.JsonConvert.DeserializeObject<CoinAccounts>(
                "{\"PropertyIndex\":1.0,\"Accounts\":[{\"UserId\":1,\"NetWorth\":0,\"Properties\":[{\"Kind\":1,\"LetOut\":false}]}]}");

            PropertyService.PayRentOneHour(data);

            double net = PropertyCatalog.GrossRentPerHour(PropertyKind.Apartment, 1.0) * (1 - PropertyCatalog.AgencyFeeRate);
            Assert.Equal(net, data.Accounts[0].NetWorth, 6);
        }

        [Fact]
        public void HoldingCost_IsChargedHourly()
        {
            var account = new CoinAccount { UserId = 1, NetWorth = 1_000 };
            account.Properties.Add(new PropertyHolding { Kind = PropertyKind.Apartment });
            var data = new CoinAccounts { PropertyIndex = 1.0 };
            data.Accounts.Add(account);

            PropertyService.PayHoldingOneHour(data);

            //cash pays what it can and the rest goes on debt: cash never goes negative
            Assert.Equal(0, account.NetWorth, 6);
            Assert.Equal(PropertyCatalog.HourlyHoldingCost(PropertyKind.Apartment, 1.0) - 1_000, account.Debt, 2);
        }

        [Fact]
        public void Rent_IsNotPaidForAccountsWithNoProperty()
        {
            var account = new CoinAccount { UserId = 1, NetWorth = 500 };
            var data = new CoinAccounts { PropertyIndex = 1.0 };
            data.Accounts.Add(account);

            PropertyService.PayRentOneHour(data);

            Assert.Equal(500, account.NetWorth);
        }
    }
}
