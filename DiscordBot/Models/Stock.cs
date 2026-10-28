using System;
using System.Collections.Generic;

namespace DiscordBot.Models
{
    public class Stock
    {
        public const double MinPrice = 1.00;
        public const int MinTier = 1;
        public const int MaxTier = 5;

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

        public double PercentChange => PreviousPrice <= 0 ? 0 : (Price - PreviousPrice) / PreviousPrice * 100;

        /// <summary>
        /// Moves the price by a random amount within this stock's tier swing. <paramref name="random"/> returns a value in [0, 1).
        /// </summary>
        public void ApplyTick(Func<double> random)
        {
            double maxSwing = MaxSwingByTier[RiskTier];
            double swing = (random() * 2 - 1) * maxSwing; //uniform in [-maxSwing, +maxSwing)
            PreviousPrice = Price;
            Price = Math.Max(MinPrice, Math.Round(Price * (1 + swing), 2));
        }
    }
}
