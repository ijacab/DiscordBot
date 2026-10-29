using System;
using System.Collections.Generic;

namespace DiscordBot.Models
{
    public class Stock
    {
        public const double MinPrice = 1.00;
        public const int MinTier = 1;
        public const int MaxTier = 5;
        /// <summary>Fraction of the gap between the price and <see cref="BasePrice"/> closed on each tick.</summary>
        public const double MeanReversionRate = 0.05;
        /// <summary>One price per 5 minute tick, so this is 7 days.</summary>
        public const int MaxHistoryPoints = 7 * 24 * 12;

        /// <summary>Max swing (fraction of price, up or down) allowed in a single 5 minute tick for each risk tier.</summary>
        public static readonly IReadOnlyDictionary<int, double> MaxSwingByTier = new Dictionary<int, double>
        {
            { 1, 0.01 },
            { 2, 0.03 },
            { 3, 0.07 },
            { 4, 0.15 },
            { 5, 0.30 }
        };

        public string Symbol { get; set; }
        public string Name { get; set; }
        public int RiskTier { get; set; }
        public double Price { get; set; }
        public double PreviousPrice { get; set; }
        /// <summary>The price the stock is pulled back towards, so percentage swings can't compound into a slide to zero.</summary>
        public double BasePrice { get; set; }
        /// <summary>Price at each tick, oldest first.</summary>
        public List<double> History { get; set; } = new List<double>();

        public double PercentChange => PreviousPrice <= 0 ? 0 : (Price - PreviousPrice) / PreviousPrice * 100;

        /// <summary>
        /// Moves the price by a random amount within this stock's tier swing, then pulls it back towards <see cref="BasePrice"/>.
        /// <paramref name="random"/> returns a value in [0, 1).
        /// </summary>
        public void ApplyTick(Func<double> random)
        {
            double maxSwing = MaxSwingByTier[RiskTier];
            double swing = (random() * 2 - 1) * maxSwing; //uniform in [-maxSwing, +maxSwing)
            double moved = Price * (1 + swing);
            //a fixed percentage move up or down has a negative average log change, so the price would slowly decay without this
            if (BasePrice > 0)
                moved *= Math.Pow(BasePrice / moved, MeanReversionRate);

            PreviousPrice = Price;
            Price = Math.Max(MinPrice, Math.Round(moved, 2));
        }

        public void RecordHistory()
        {
            History.Add(Price);
            if (History.Count > MaxHistoryPoints)
                History.RemoveRange(0, History.Count - MaxHistoryPoints);
        }
    }
}
