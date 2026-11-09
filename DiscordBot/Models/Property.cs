using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    public enum PropertyKind
    {
        House,
        Apartment
    }

    /// <summary>A property the player owns. Its value comes from the shared market index, not from what they paid.</summary>
    public class PropertyHolding
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public PropertyKind Kind { get; set; }
        /// <summary>Everything paid for it, including stamp duty and conveyancing.</summary>
        public double PurchaseCost { get; set; }
        public long PurchasedUtcTicks { get; set; }
        /// <summary>Whether it is rented out. Rent is only paid while it is rented out.</summary>
        public bool LetOut { get; set; }
    }

    /// <summary>
    /// Approximate national median prices and rents for Australia, with the costs of buying, selling and letting.
    /// Tune the numbers here.
    /// </summary>
    public static class PropertyCatalog
    {
        public const double HousePrice = 950_000;
        public const double ApartmentPrice = 720_000;
        /// <summary>Gross rent per hour while let, before the vacancy allowance and agency fee. A house pays 1% of the $1,000 an hour base income.</summary>
        public const double HouseRentPerHour = 10;
        public const double ApartmentRentPerHour = 8;

        /// <summary>Stamp duty and conveyancing, paid on top of the price when buying.</summary>
        public const double StampDutyRate = 0.045;
        /// <summary>Agent commission and marketing, taken off the price when selling.</summary>
        public const double SellingCostRate = 0.03;
        /// <summary>Property management cut of the rent collected.</summary>
        public const double AgencyFeeRate = 0.08;
        /// <summary>Share of the year a let property sits empty.</summary>
        public const double VacancyRate = 0.04;
        /// <summary>Council rates and maintenance, a share of the current value per year. Charged whether or not it is rented out.</summary>
        public const double HoldingCostPerYear = 0.015;

        /// <summary>The market index can't go below or above these, so prices can't run away.</summary>
        public const double IndexMin = 0.6;
        public const double IndexMax = 1.6;
        /// <summary>Daily drift of the index in the current boom or bust, as a share of the index.</summary>
        public const double TrendPerDay = 0.0025;
        /// <summary>Daily random movement on top of the drift.</summary>
        public const double NoisePerDay = 0.0025;
        public const int MinTrendDays = 60;
        public const int MaxTrendDays = 120;
        /// <summary>Apartments move this much more, in percent, than houses.</summary>
        public const double ApartmentVolatility = 1.2;
        /// <summary>At most this many hours of rent are paid at once (a week), if the bot was down for longer.</summary>
        public const int MaxRentCatchUpHours = 7 * 24;
        /// <summary>At most this many days of holding costs are charged at once, if the bot was down for longer.</summary>
        public const int MaxHoldingCatchUpDays = 7;

        public static double BasePrice(PropertyKind kind) => kind == PropertyKind.House ? HousePrice : ApartmentPrice;

        public static double RentPerHour(PropertyKind kind) => kind == PropertyKind.House ? HouseRentPerHour : ApartmentRentPerHour;

        public static string DisplayName(PropertyKind kind) => kind == PropertyKind.House ? "House" : "Apartment";

        /// <summary>Current market price, before costs.</summary>
        public static double Price(PropertyKind kind, double index)
        {
            double moved = kind == PropertyKind.House ? index : Math.Pow(index, ApartmentVolatility);
            return Math.Round(BasePrice(kind) * moved, 2);
        }

        /// <summary>Total paid to buy at this index: the price plus stamp duty and conveyancing.</summary>
        public static double BuyCost(PropertyKind kind, double index) => Math.Round(Price(kind, index) * (1 + StampDutyRate), 2);

        /// <summary>What it would sell for, after agent commission and marketing. This is its value in net worth.</summary>
        public static double SaleValue(PropertyKind kind, double index) => Math.Round(Price(kind, index) * (1 - SellingCostRate), 2);

        /// <summary>Gross rent for one hour while let, after the vacancy allowance and before the agency fee.</summary>
        public static double GrossRentPerHour(PropertyKind kind) => RentPerHour(kind) * (1 - VacancyRate);

        /// <summary>Holding cost for one day on a property at this index.</summary>
        public static double DailyHoldingCost(PropertyKind kind, double index) => Price(kind, index) * HoldingCostPerYear / 365.0;
    }

    /// <summary>The shared market index and the day-by-day moves that drive property prices.</summary>
    public static class PropertyMarket
    {
        /// <summary>One step of the market: moves the index by the current trend plus noise, and flips the trend when its days run out.</summary>
        public static void Step(ref double index, ref int trendDirection, ref int trendDaysLeft, Func<double> random)
        {
            if (trendDaysLeft <= 0)
            {
                trendDirection = trendDirection >= 0 ? -1 : 1; //booms and busts alternate
                trendDaysLeft = PropertyCatalog.MinTrendDays + (int)(random() * (PropertyCatalog.MaxTrendDays - PropertyCatalog.MinTrendDays + 1));
            }

            double noise = (random() * 2 - 1) * PropertyCatalog.NoisePerDay;
            double move = trendDirection * PropertyCatalog.TrendPerDay + noise;
            index = Math.Min(PropertyCatalog.IndexMax, Math.Max(PropertyCatalog.IndexMin, index * (1 + move)));
            trendDaysLeft--;
        }

        public static long DayNumber(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / 86400L;

        public static long HourNumber(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / 3600L;
    }
}
