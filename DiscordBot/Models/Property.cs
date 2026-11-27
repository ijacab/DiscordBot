using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    /// <summary>
    /// What a property is. The numbers are saved, so never reorder them: <see cref="House"/> (0) is the suburban house every old
    /// save holds, <see cref="Apartment"/> (1) is only for old saves, and the tiers after were added later.
    /// </summary>
    public enum PropertyKind
    {
        /// <summary>The suburban house: the one house there used to be.</summary>
        House = 0,
        /// <summary>No longer sold. Kept so old saves load; <see cref="Services.PropertyService.BuyBackApartments"/> pays them out.</summary>
        Apartment = 1,
        Shack = 2,
        Luxury = 3,
        Mansion = 4,
        Penthouse = 5,
        FoodTruck = 6,
        Cafe = 7,
        Nightclub = 8,
        Casino = 9,
        Bank = 10,
    }

    /// <summary>One kind of property: its price at the base index, what it earns and costs an hour, and how hard it is to rob.</summary>
    public sealed class PropertyType
    {
        public PropertyKind Kind { get; init; }
        public string Name { get; init; }
        public string Emoji { get; init; }
        /// <summary>Words that pick it in `.buy property luxury` (the first is shown).</summary>
        public string[] Words { get; init; }
        public double BasePrice { get; init; }
        /// <summary>Gross rent (or takings) an hour as a share of the current price, before vacancy and the agency.</summary>
        public double GrossYieldPerHour { get; init; }
        /// <summary>Rates, wages and upkeep an hour as a share of the current price.</summary>
        public double HoldingCostPerHour { get; init; }
        /// <summary>Its price moves as the market index to this power, so 1.5 booms and crashes harder than a house.</summary>
        public double Volatility { get; init; } = 1;
        /// <summary>A business's takings swing each hour between 1 - this and 1 + this times the average.</summary>
        public double IncomeSwing { get; init; }
        /// <summary>The defence a raid on it has to beat, added to the owner's gang (<see cref="RaidRules.DefencePower"/>).</summary>
        public double Security { get; init; }
        public bool IsBusiness { get; init; }
        public bool Buyable { get; init; } = true;
    }

    /// <summary>A property the player owns. Its value comes from the shared market index, not from what they paid.</summary>
    public class PropertyHolding
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public PropertyKind Kind { get; set; }
        /// <summary>Everything paid for it, including stamp duty and conveyancing.</summary>
        public double PurchaseCost { get; set; }
        public long PurchasedUtcTicks { get; set; }
        /// <summary>After a raid gets through, nobody can rob it again until then (UTC ticks), except with revenge (<see cref="RaidRules.PropertyProtection"/>).</summary>
        public long RaidProtectedUntilUtcTicks { get; set; }
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
        /// <summary>
        /// Gross rent per hour as a share of the current market price: 0.7% for a house (apartments are gone; theirs only prices old
        /// saves). After vacancy, the agency and <see cref="HoldingCostPerHour"/> that is about 0.32% an hour, 7.6% a day, so over two
        /// weeks a house returns roughly twice what a bond does after its 7.5% of buying and selling costs, and flipping one within
        /// a few days barely pays. It was 3.5% (2.1% an hour net, a house paying back 6x its price in two weeks), far too strong.
        /// Tune here; <c>PropertyTests.HouseEconomy_StaysInAFairRange</c> keeps it from running away again.
        /// </summary>
        public const double HouseGrossYieldPerHour = 0.007;
        public const double ApartmentGrossYieldPerHour = 0.008;

        /// <summary>Stamp duty and conveyancing, paid on top of the price when buying.</summary>
        public const double StampDutyRate = 0.045;
        /// <summary>Agent commission and marketing, taken off the price when selling.</summary>
        public const double SellingCostRate = 0.03;
        /// <summary>Property management cut of the rent collected.</summary>
        public const double AgencyFeeRate = 0.08;
        /// <summary>Share of the year a let property sits empty.</summary>
        public const double VacancyRate = 0.04;
        /// <summary>
        /// Council rates and maintenance, a share of the current value every hour, charged with the rent. A bit over a third of the
        /// gross rent, like rates are a fraction of a real rent, scaled to the game's hours.
        /// </summary>
        public const double HoldingCostPerHour = 0.003;

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

        /// <summary>Holding cost an hour for a business: wages and upkeep run a bit higher than a house's rates.</summary>
        public const double BusinessHoldingCostPerHour = 0.0035;
        /// <summary>Businesses' prices move as the index to this power: they boom and crash harder than houses.</summary>
        public const double BusinessVolatility = 1.5;
        /// <summary>A business's takings each hour are the average times a random 1 - this to 1 + this.</summary>
        public const double BusinessIncomeSwing = 0.5;

        /// <summary>
        /// Every kind, cheapest first within houses then businesses. Bigger places earn a little less per dollar but are much harder
        /// to rob; businesses earn more than houses and swing more. <c>PropertyTierTests</c> keeps every one ahead of a bond.
        /// </summary>
        public static readonly IReadOnlyList<PropertyType> Types = new List<PropertyType>
        {
            House(PropertyKind.Shack, "Shack", "🛖", 150_000, 0.0075, 30, "shack", "hut"),
            House(PropertyKind.House, "House", "🏠", HousePrice, HouseGrossYieldPerHour, 60, "house", "suburban", "home"),
            House(PropertyKind.Luxury, "Luxury home", "🏡", 4_000_000, 0.0065, 110, "luxury", "lux"),
            House(PropertyKind.Mansion, "Mansion", "🏰", 15_000_000, 0.0060, 180, "mansion"),
            House(PropertyKind.Penthouse, "Penthouse", "🌆", 50_000_000, 0.0055, 270, "penthouse", "ph"),
            Business(PropertyKind.FoodTruck, "Food truck", "🚚", 250_000, 0.0085, 20, "foodtruck", "truck", "food"),
            Business(PropertyKind.Cafe, "Café", "☕", 1_200_000, 0.0082, 45, "cafe", "café", "coffee"),
            Business(PropertyKind.Nightclub, "Nightclub", "🪩", 6_000_000, 0.0078, 90, "nightclub", "club"),
            Business(PropertyKind.Casino, "Casino", "🎰", 25_000_000, 0.0075, 160, "casino"),
            Business(PropertyKind.Bank, "Bank", "🏦", 80_000_000, 0.0071, 260, "bank"),
            new PropertyType { Kind = PropertyKind.Apartment, Name = "Apartment", Emoji = "🏢", Words = new[] { "apartment" }, BasePrice = ApartmentPrice,
                GrossYieldPerHour = ApartmentGrossYieldPerHour, HoldingCostPerHour = HoldingCostPerHour, Volatility = ApartmentVolatility, Security = 40, Buyable = false },
        };

        private static PropertyType House(PropertyKind kind, string name, string emoji, double price, double yield, double security, params string[] words) =>
            new PropertyType { Kind = kind, Name = name, Emoji = emoji, Words = words, BasePrice = price, GrossYieldPerHour = yield, HoldingCostPerHour = HoldingCostPerHour, Security = security };

        private static PropertyType Business(PropertyKind kind, string name, string emoji, double price, double yield, double security, params string[] words) =>
            new PropertyType { Kind = kind, Name = name, Emoji = emoji, Words = words, BasePrice = price, GrossYieldPerHour = yield, HoldingCostPerHour = BusinessHoldingCostPerHour,
                Volatility = BusinessVolatility, IncomeSwing = BusinessIncomeSwing, Security = security, IsBusiness = true };

        /// <summary>The type of a kind. An unknown number (a newer save read by older code) reads as a house.</summary>
        public static PropertyType TypeOf(PropertyKind kind) => Types.FirstOrDefault(t => t.Kind == kind) ?? Types.First(t => t.Kind == PropertyKind.House);

        public static IEnumerable<PropertyType> Buyable => Types.Where(t => t.Buyable);

        /// <summary>The buyable kind a word names ("luxury", "cafe", "bank"), or null.</summary>
        public static PropertyType Find(string word)
        {
            string w = (word ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "");
            foreach (string tryWord in new[] { w, w.TrimEnd('s'), w.EndsWith("es") ? w.Substring(0, w.Length - 2) : w })
            {
                var type = Buyable.FirstOrDefault(t => t.Words.Contains(tryWord));
                if (type != null) return type;
            }
            return null;
        }

        public static double BasePrice(PropertyKind kind) => TypeOf(kind).BasePrice;

        public static double YieldPerHour(PropertyKind kind) => TypeOf(kind).GrossYieldPerHour;

        /// <summary>Gross rent for one hour while let, before the vacancy allowance and agency fee. Follows the market price.</summary>
        public static double RentPerHour(PropertyKind kind, double index) => Price(kind, index) * YieldPerHour(kind);

        public static string DisplayName(PropertyKind kind) => TypeOf(kind).Name;

        /// <summary>"🏰 Mansion".</summary>
        public static string Label(PropertyKind kind) => $"{TypeOf(kind).Emoji} {TypeOf(kind).Name}";

        /// <summary>Current market price, before costs.</summary>
        public static double Price(PropertyKind kind, double index)
        {
            var type = TypeOf(kind);
            double moved = type.Volatility == 1 ? index : Math.Pow(index, type.Volatility);
            return Math.Round(type.BasePrice * moved, 2);
        }

        /// <summary>Total paid to buy at this index: the price plus stamp duty and conveyancing.</summary>
        public static double BuyCost(PropertyKind kind, double index) => Math.Round(Price(kind, index) * (1 + StampDutyRate), 2);

        /// <summary>What it would sell for, after agent commission and marketing. This is its value in net worth.</summary>
        public static double SaleValue(PropertyKind kind, double index) => Math.Round(Price(kind, index) * (1 - SellingCostRate), 2);

        /// <summary>Gross rent for one hour while let, after the vacancy allowance and before the agency fee.</summary>
        public static double GrossRentPerHour(PropertyKind kind, double index) => RentPerHour(kind, index) * (1 - VacancyRate);

        /// <summary>What the landlord is paid for one hour on average: gross rent less the agency's cut. A business swings around it.</summary>
        public static double NetRentPerHour(PropertyKind kind, double index) => GrossRentPerHour(kind, index) * (1 - AgencyFeeRate);

        /// <summary>Holding cost for one hour on a property at this index.</summary>
        public static double HourlyHoldingCost(PropertyKind kind, double index) => Price(kind, index) * TypeOf(kind).HoldingCostPerHour;

        /// <summary>What it earns an hour on average after its holding cost: rent (or takings) less the agency, less rates and upkeep.</summary>
        public static double ProfitPerHour(PropertyKind kind, double index) => NetRentPerHour(kind, index) - HourlyHoldingCost(kind, index);

        /// <summary>One hour's takings for a business: the average times a random swing (<paramref name="u"/> uniform in 0..1). A house's rent doesn't swing.</summary>
        public static double SwungRent(PropertyKind kind, double index, double u)
        {
            var type = TypeOf(kind);
            return NetRentPerHour(kind, index) * (1 + type.IncomeSwing * (2 * u - 1));
        }
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
