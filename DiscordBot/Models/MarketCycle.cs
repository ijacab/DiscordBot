using System;
using System.Collections.Generic;

namespace DiscordBot.Models
{
    /// <summary>Where the market is in a crash. None is normal trading.</summary>
    public enum CrisisPhase
    {
        None,
        /// <summary>Prices fall over about two weeks. Gold rises and bonds carry on.</summary>
        Decline,
        /// <summary>A few days of no trend, with wild swings.</summary>
        Bottom,
        /// <summary>Months of slow recovery. Prices settle short of where they were.</summary>
        Recovery
    }

    /// <summary>A market-wide push applied to a stock on every tick, on top of its normal move.</summary>
    public readonly struct MarketDrift
    {
        public static readonly MarketDrift None = new MarketDrift(0, 1, true);

        public MarketDrift(double logPerTick, double swingScale, bool allowReversion)
        {
            LogPerTick = logPerTick;
            SwingScale = swingScale;
            AllowReversion = allowReversion;
        }

        /// <summary>Added to the log move each tick.</summary>
        public double LogPerTick { get; }
        /// <summary>Multiplies the normal swing. 1 is normal.</summary>
        public double SwingScale { get; }
        /// <summary>Whether the price is still pulled back towards its base.</summary>
        public bool AllowReversion { get; }
    }

    /// <summary>
    /// Rare market-wide crashes, roughly once every two years. A crash runs Decline, Bottom and Recovery, and moves stocks
    /// and property together. Gold rallies in a decline; bonds ignore it. Stock prices and property stay where they fall.
    /// Random draws in <see cref="AdvanceDay"/> are the daily roll only, so tests can control it.
    /// </summary>
    public static class MarketCycle
    {
        /// <summary>Chance per UTC day that a crash starts, if none is running and the cooldown has passed. About once every two years.</summary>
        public const double DailyChance = 1.0 / 730;
        /// <summary>Days after a crash starts before another one can start.</summary>
        public const int CooldownDays = 730;
        public const int DeclineDays = 14;
        public const int BottomDays = 5;
        public const int RecoveryDays = 120;

        /// <summary>Total fall over a decline for crypto.</summary>
        public const double CryptoFall = 0.60;
        /// <summary>Total fall for an ordinary stock is this plus <see cref="TierFallPerTier"/> per risk tier: 26% in tier 1, 50% in tier 5.</summary>
        public const double TierFallBase = 0.20;
        public const double TierFallPerTier = 0.06;
        /// <summary>Share of the lost value (in log terms) that comes back over the recovery.</summary>
        public const double RecoveryShare = 0.6;
        /// <summary>How far gold rises over a decline.</summary>
        public const double GoldRise = 0.20;
        /// <summary>Swings are this many times normal in the bottom.</summary>
        public const double BottomSwingScale = 2;

        /// <summary>Property index multiplier over a decline and over a recovery.</summary>
        public const double PropertyDeclineTotal = 0.75;
        public const double PropertyRecoveryTotal = 1.15;

        /// <summary>Total fall of an ordinary stock over a decline.</summary>
        public static double FallFor(Stock stock) => stock.IsCrypto ? CryptoFall : TierFallBase + TierFallPerTier * stock.RiskTier;

        /// <summary>The drift a stock gets on each tick in this phase.</summary>
        public static MarketDrift DriftFor(Stock stock, CrisisPhase phase)
        {
            if (stock.Role == StockRole.Bond) return MarketDrift.None;
            bool safeHaven = stock.Role == StockRole.SafeHaven;

            switch (phase)
            {
                case CrisisPhase.Decline:
                    if (safeHaven)
                        return new MarketDrift(Math.Log(1 + GoldRise) / DeclineDays / Stock.TicksPerDay, 1, false);
                    return new MarketDrift(Math.Log(1 - FallFor(stock)) / DeclineDays / Stock.TicksPerDay, 1, false);

                case CrisisPhase.Bottom:
                    return new MarketDrift(0, BottomSwingScale, false);

                case CrisisPhase.Recovery:
                    if (safeHaven) return MarketDrift.None; //gold drifts back to its base
                    return new MarketDrift(-Math.Log(1 - FallFor(stock)) * RecoveryShare / RecoveryDays / Stock.TicksPerDay, 1, false);

                default:
                    return MarketDrift.None;
            }
        }

        /// <summary>Moves the property index for one day of a crisis. Apartments still move more than houses, as the index is raised to a power.</summary>
        public static void StepProperty(ref double index, CrisisPhase phase)
        {
            double factor = 1;
            if (phase == CrisisPhase.Decline) factor = Math.Pow(PropertyDeclineTotal, 1.0 / DeclineDays);
            else if (phase == CrisisPhase.Recovery) factor = Math.Pow(PropertyRecoveryTotal, 1.0 / RecoveryDays);
            index = Math.Min(PropertyCatalog.IndexMax, Math.Max(PropertyCatalog.IndexMin, index * factor));
        }

        /// <summary>
        /// Advances the crisis by one UTC day, starting one at random if none is running. Returns a headline to post, or null.
        /// <paramref name="random"/> is only drawn when a crash could start.
        /// </summary>
        public static string AdvanceDay(CoinAccounts data, long day, Func<double> random)
        {
            if (data.MarketPhase == CrisisPhase.None)
            {
                if (day < data.NextCrisisAllowedDay || random() >= DailyChance) return null;
                data.NextCrisisAllowedDay = day + CooldownDays;
                return Begin(data, CrisisPhase.Decline, DeclineDays,
                    "📉 **Markets are in freefall.** Stocks and property are sliding. Gold is rising and government bonds are steady, so they're safer places to put money.");
            }

            data.MarketPhaseDaysLeft--;
            if (data.MarketPhaseDaysLeft > 0) return null;

            switch (data.MarketPhase)
            {
                case CrisisPhase.Decline:
                    return Begin(data, CrisisPhase.Bottom, BottomDays,
                        "🪨 **The bottom is in.** Prices have stopped falling, but they're still jumping around.");
                case CrisisPhase.Bottom:
                    return Begin(data, CrisisPhase.Recovery, RecoveryDays,
                        "📈 **Recovery is under way.** Prices are climbing back, but slowly.");
                default:
                    data.MarketPhase = CrisisPhase.None;
                    data.MarketPhaseDaysLeft = 0;
                    data.CrisisResetPending = true;
                    return "✅ **The crisis is over.** Prices have settled at their new level, which is below where they were before.";
            }
        }

        /// <summary>
        /// Stocks that were hit keep their new level as their base, so the old launch price stops pulling them back. Gold and bonds keep theirs.
        /// </summary>
        public static void ResetBasePrices(IEnumerable<Stock> stocks)
        {
            foreach (var stock in stocks)
                if (stock.Role == StockRole.Normal)
                    stock.BasePrice = stock.FairPrice;
        }

        private static string Begin(CoinAccounts data, CrisisPhase phase, int days, string headline)
        {
            data.MarketPhase = phase;
            data.MarketPhaseDaysLeft = days;
            return headline;
        }
    }
}
