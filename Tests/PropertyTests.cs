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
            //60 + 0.5 * 61 = 90 days, minus the day just used
            Assert.Equal(89, daysLeft);
            Assert.True(index < 1.0);
        }

        [Fact]
        public void Step_StaysInsideTheIndexBounds()
        {
            double index = 1.0;
            int direction = 1, daysLeft = 100_000;
            for (int i = 0; i < 2_000; i++)
                PropertyMarket.Step(ref index, ref direction, ref daysLeft, Fixed(0.999999));
            Assert.Equal(PropertyCatalog.IndexMax, index, 10);

            direction = -1;
            for (int i = 0; i < 5_000; i++)
                PropertyMarket.Step(ref index, ref direction, ref daysLeft, Fixed(0.000001));
            Assert.Equal(PropertyCatalog.IndexMin, index, 10);
        }

        [Fact]
        public void DailyMove_IsSmall()
        {
            //the most a day can move is the trend plus the noise, so a day is never a casino swing
            Assert.True(PropertyCatalog.TrendPerDay + PropertyCatalog.NoisePerDay <= 0.005);
        }

        [Fact]
        public void DayNumber_CountsWholeUtcDays()
        {
            long day = PropertyMarket.DayNumber(Now);
            Assert.Equal(day + 1, PropertyMarket.DayNumber(Now.AddDays(1)));
            Assert.Equal(day, PropertyMarket.DayNumber(Now.Date.AddHours(23)));
        }

        [Fact]
        public void Rent_IsPaidOnlyWhileLetAndLessTheAgencyFee()
        {
            var account = new CoinAccount { UserId = 1, NetWorth = 0 };
            account.Properties.Add(new PropertyHolding { Kind = PropertyKind.House, LetOut = true });
            var data = new CoinAccounts { PropertyIndex = 1.0 };
            data.Accounts.Add(account);

            PropertyService.PayRentOneHour(data);

            double gross = PropertyCatalog.GrossRentPerHour(PropertyKind.House);
            Assert.Equal(gross * (1 - PropertyCatalog.AgencyFeeRate), account.NetWorth, 6);
        }

        [Fact]
        public void Rent_ForAHouseIsAboutRightAfterVacancyAndAgency()
        {
            //$10 an hour gross, less 4% empty and 8% to the agency, is about $8.83 an hour
            double netRentPerHour = PropertyCatalog.GrossRentPerHour(PropertyKind.House) * (1 - PropertyCatalog.AgencyFeeRate);
            Assert.InRange(netRentPerHour, 8.7, 9.0);
            Assert.InRange(PropertyCatalog.RentPerHour(PropertyKind.House) / 1000, 0.009, 0.011); //1% of the $1,000 an hour base income
        }

        [Fact]
        public void HoldingCost_IsChargedWhenNotLet()
        {
            var account = new CoinAccount { UserId = 1, NetWorth = 1_000 };
            account.Properties.Add(new PropertyHolding { Kind = PropertyKind.Apartment, LetOut = false });
            var data = new CoinAccounts { PropertyIndex = 1.0 };
            data.Accounts.Add(account);

            PropertyService.PayHoldingOneDay(data);

            Assert.Equal(1_000 - PropertyCatalog.DailyHoldingCost(PropertyKind.Apartment, 1.0), account.NetWorth, 6);
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
