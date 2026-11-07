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

        /// <summary>How far a trade moves the price: impact = this × √(shares traded ÷ shares outstanding). Trading 10% of the shares moves it about 5%.</summary>
        public const double ImpactCoefficient = 0.16;
        /// <summary>Share of the temporary trading impact left after each 5 minute tick, so it fades over a few ticks.</summary>
        public const double ImpactDecayPerTick = 0.5;
        /// <summary>Largest move trading can cause in either direction, as a fraction of the fair price.</summary>
        public const double MaxImpact = 0.5;
        /// <summary>Fixed bid-ask spread as a fraction of the price. Buys pay half of it above the mid, sells receive half below.</summary>
        public const double Spread = 0.01;
        /// <summary>Tax taken from the gross value of every sale. The money leaves the economy, so churning the same stock costs.</summary>
        public const double SellTaxRate = 0.02;

        /// <summary>The sell tax on a gross sale value, to the cent.</summary>
        public static double TaxOn(double grossSale) => Math.Round(grossSale * SellTaxRate, 2);

        /// <summary>Shares a new stock is issued with. Trades are measured against this, so a bigger float takes more buying to move.</summary>
        public const double DefaultSharesOutstanding = 100_000;

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
        /// <summary>The quoted price everyone sees and trades at. This is <see cref="FairPrice"/> plus the trading <see cref="Impact"/>.</summary>
        public double Price { get; set; }
        public double PreviousPrice { get; set; }
        /// <summary>The price the stock is pulled back towards, so percentage swings can't compound into a slide to zero.</summary>
        public double BasePrice { get; set; }
        /// <summary>The tick-driven price, before any move caused by trading.</summary>
        public double FairPrice { get; set; }
        /// <summary>Signed temporary move caused by trading, as a fraction of <see cref="FairPrice"/>. Fades on each tick.</summary>
        public double Impact { get; set; }
        /// <summary>Total shares in the market for this stock. Trades are sized against it.</summary>
        public double SharesOutstanding { get; set; } = DefaultSharesOutstanding;
        /// <summary>Quoted price at each tick, oldest first.</summary>
        public List<double> History { get; set; } = new List<double>();

        public double PercentChange => PreviousPrice <= 0 ? 0 : (Price - PreviousPrice) / PreviousPrice * 100;

        /// <summary>
        /// Moves the fair price by a random amount within this stock's tier swing, pulls it back towards <see cref="BasePrice"/>,
        /// then lets the trading impact fade. <paramref name="random"/> returns a value in [0, 1).
        /// </summary>
        public void ApplyTick(Func<double> random)
        {
            double maxSwing = MaxSwingByTier[RiskTier];
            double swing = (random() * 2 - 1) * maxSwing; //uniform in [-maxSwing, +maxSwing)
            double moved = FairPrice * (1 + swing);
            //a fixed percentage move up or down has a negative average log change, so the price would slowly decay without this
            if (BasePrice > 0)
                moved *= Math.Pow(BasePrice / moved, MeanReversionRate);

            PreviousPrice = Price;
            FairPrice = Math.Max(MinPrice, Math.Round(moved, 2));
            Impact *= ImpactDecayPerTick;
            RefreshQuote();
        }

        /// <summary>
        /// The fill price for a trade of <paramref name="shares"/> (negative to sell) and the impact it would leave.
        /// The mid fill is the midpoint of the quote before and after the move, so a big order pays for part of its own impact.
        /// The spread is then added on a buy or taken off on a sell, so a round trip always costs <see cref="Spread"/>.
        /// Does not change the stock.
        /// </summary>
        public (double Fill, double NewImpact) PreviewTrade(int shares)
        {
            double move = ImpactCoefficient * Math.Sqrt(Math.Abs(shares) / SharesOutstanding) * Math.Sign(shares);
            double newImpact = Math.Max(-MaxImpact, Math.Min(MaxImpact, Impact + move));
            double after = PriceAt(FairPrice, newImpact);
            double mid = (Price + after) / 2;
            double fill = Math.Max(MinPrice, mid * (1 + Math.Sign(shares) * Spread / 2));
            return (Math.Round(fill, 2), newImpact);
        }

        /// <summary>Records a trade's impact on the quoted price and returns the price it filled at.</summary>
        public double ApplyTrade(int shares)
        {
            var (fill, newImpact) = PreviewTrade(shares);
            Impact = newImpact;
            RefreshQuote();
            return fill;
        }

        /// <summary>
        /// The largest number of shares whose cost fits in <paramref name="cash"/>. Cost rises with size because of impact,
        /// so this searches rather than dividing. Buying pushes the fill up, so the price is an upper bound.
        /// </summary>
        public int MaxAffordableShares(double cash)
        {
            if (cash < Price) return 0;
            int low = 0;
            int high = (int)Math.Min(int.MaxValue, Math.Floor(cash / Price));
            while (low < high)
            {
                int mid = low + (high - low + 1) / 2;
                double cost = Math.Round(mid * PreviewTrade(mid).Fill, 2);
                if (cost <= cash) low = mid;
                else high = mid - 1;
            }
            return low;
        }

        /// <summary>The quote for a fair price and impact, never below <see cref="MinPrice"/>.</summary>
        public static double PriceAt(double fairPrice, double impact) =>
            Math.Max(MinPrice, Math.Round(fairPrice * (1 + impact), 2));

        public void RefreshQuote() => Price = PriceAt(FairPrice, Impact);

        public void RecordHistory()
        {
            History.Add(Price);
            if (History.Count > MaxHistoryPoints)
                History.RemoveRange(0, History.Count - MaxHistoryPoints);
        }
    }
}
