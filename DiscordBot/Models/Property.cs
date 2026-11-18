using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    public enum PropertyKind
    {
        House,
        /// <summary>No longer sold. Kept so old saves load; <see cref="Services.PropertyService.BuyBackApartments"/> pays them out.</summary>
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
        //every property is rented out automatically; old saves' "LetOut" flag is ignored when loading
    }

    /// <summary>
    /// Approximate national median prices and rents for Australia, with the costs of buying, selling and letting.
    /// Tune the numbers here.
    /// </summary>
    public static class PropertyCatalog
    {
        public const double HousePrice = 950_000;
        public const double ApartmentPrice = 720_000;
        /// <summary>Gross rent per hour as a share of the current market price: 3.5% for a house, 4% for an apartment.</summary>
        public const double HouseGrossYieldPerHour = 0.035;
        public const double ApartmentGrossYieldPerHour = 0.04;

        /// <summary>Stamp duty and conveyancing, paid on top of the price when buying.</summary>
        public const double StampDutyRate = 0.045;
        /// <summary>Agent commission and marketing, taken off the price when selling.</summary>
        public const double SellingCostRate = 0.03;
        /// <summary>Property management cut of the rent collected.</summary>
        public const double AgencyFeeRate = 0.08;
        /// <summary>Share of the year a let property sits empty.</summary>
        public const double VacancyRate = 0.04;
        /// <summary>
        /// Council rates and maintenance, a share of the current value every hour, charged with the rent. About a third of the gross
        /// rent, like rates are a fraction of a real rent, scaled to the game's hours.
        /// </summary>
        public const double HoldingCostPerHour = 0.01;

        /// <summary>
        /// The market index has no ceiling and no ordinary floor: it can run as far as the daily moves take it. This is only a
        /// safety net so a price can never reach zero (a 99% fall in one day is still possible).
        /// </summary>
        public const double IndexFloor = 0.01;
        /// <summary>Daily drift of the index in the current boom or bust, as a share of the index. A season is 2 to 4 weeks, so a boom or bust lasts days.</summary>
        public const double TrendPerDay = 0.02;
        /// <summary>
        /// Daily random move on top of the drift, in log terms: a Laplace draw with this scale, so most days move a couple of percent
        /// and the odd day moves a lot, up or down. There is no limit on a day's move.
        /// </summary>
        public const double NoisePerDay = 0.02;
        /// <summary>Chance per day of a shock day, whose random move is <see cref="ShockScale"/> instead: 10% to 30% or more, either way.</summary>
        public const double ShockChance = 0.04;
        public const double ShockScale = 0.12;
        public const int MinTrendDays = 4;
        public const int MaxTrendDays = 8;
        /// <summary>Apartments move this much more, in percent, than houses.</summary>
        public const double ApartmentVolatility = 1.2;
        /// <summary>At most this many hours of rent are paid at once (a week), if the bot was down for longer.</summary>
        public const int MaxRentCatchUpHours = 7 * 24;

        public static double BasePrice(PropertyKind kind) => kind == PropertyKind.House ? HousePrice : ApartmentPrice;

        public static double YieldPerHour(PropertyKind kind) => kind == PropertyKind.House ? HouseGrossYieldPerHour : ApartmentGrossYieldPerHour;

        /// <summary>Gross rent for one hour while let, before the vacancy allowance and agency fee. Follows the market price.</summary>
        public static double RentPerHour(PropertyKind kind, double index) => Price(kind, index) * YieldPerHour(kind);

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
        public static double GrossRentPerHour(PropertyKind kind, double index) => RentPerHour(kind, index) * (1 - VacancyRate);

        /// <summary>What the landlord is paid for one hour: gross rent less the agency's cut.</summary>
        public static double NetRentPerHour(PropertyKind kind, double index) => GrossRentPerHour(kind, index) * (1 - AgencyFeeRate);

        /// <summary>Holding cost for one hour on a property at this index.</summary>
        public static double HourlyHoldingCost(PropertyKind kind, double index) => Price(kind, index) * HoldingCostPerHour;
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

            //a shock day (rare) swings far harder; draws are the shock roll, then the move (0.5 is no move)
            bool shock = random() < PropertyCatalog.ShockChance;
            double noise = Laplace(random(), shock ? PropertyCatalog.ShockScale : PropertyCatalog.NoisePerDay);
            index = Math.Max(PropertyCatalog.IndexFloor, index * (1 + trendDirection * PropertyCatalog.TrendPerDay) * Math.Exp(noise));
            trendDaysLeft--;
        }

        /// <summary>A symmetric, fat-tailed log move for a uniform draw <paramref name="u"/> in [0, 1): 0 at 0.5, and as far as the draw is extreme.</summary>
        public static double Laplace(double u, double scale)
        {
            double centred = Math.Clamp(u - 0.5, -0.499999999, 0.499999999);
            return -scale * Math.Sign(centred) * Math.Log(1 - 2 * Math.Abs(centred));
        }

        /// <summary>A trend saved when booms and busts lasted months is cut to the current length, or it would run at today's speed for ages.</summary>
        public static int CutOldTrend(int trendDaysLeft) => Math.Min(trendDaysLeft, PropertyCatalog.MaxTrendDays);

        public static long DayNumber(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / 86400L;

        public static long HourNumber(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / 3600L;
    }
}
