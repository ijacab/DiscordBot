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
    /// Market-wide crashes (the GFC), about once every two months at the base chance, more often the more of the players' wealth
    /// sits in ordinary stocks and houses (<see cref="ChanceFor"/>). A crash runs Decline, Bottom and Recovery, and moves stocks
    /// and property together, harder or softer depending on a severity rolled when it starts. Gold rallies in a decline; bonds
    /// ignore it. Stock prices and property recover most of the way but not all of it.
    /// Random draws in <see cref="AdvanceDay"/> are the daily roll and, when a crash starts, its severity, so tests can control them.
    /// </summary>
    public static class MarketCycle
    {
        /// <summary>
        /// Chance per UTC day that a crash starts while the market is normal, when players hold no stocks or houses. With the
        /// 7 day cycle that is about one crash every 4 weeks (one a season), and about one every 11 days when players are heavily invested.
        /// </summary>
        public const double DailyChance = 1.0 / 20;
        /// <summary>The chance is this many times the base when <see cref="FullExposureShare"/> of player wealth is in ordinary stocks and houses.</summary>
        public const double MaxChanceMultiple = 5;
        /// <summary>Share of all player wealth in ordinary stocks and houses that gives the full multiple.</summary>
        public const double FullExposureShare = 0.6;
        /// <summary>Days after a crash starts before another one can start. The cycle itself spaces them.</summary>
        public const int CooldownDays = 0;
        //a season lasts 2 to 4 weeks, so a whole crash (a week) has to fit inside one
        public const int DeclineDays = 2;
        public const int BottomDays = 1;
        public const int RecoveryDays = 4;

        /// <summary>Total fall over a decline for crypto in the mildest crash (severity 0) and in the worst (severity 1).</summary>
        public const double CryptoFall = 0.60;
        public const double CryptoWorstFall = 0.99;
        /// <summary>The worst fall of an ordinary stock is this plus <see cref="WorstFallPerTier"/> for each tier above 3: 70%, 80%, 90%.</summary>
        public const double WorstFallBase = 0.70;
        public const double WorstFallPerTier = 0.10;
        /// <summary>Total fall for an ordinary stock is this plus <see cref="TierFallPerTier"/> per risk tier: 26% in tier 1, 50% in tier 5.</summary>
        public const double TierFallBase = 0.20;
        public const double TierFallPerTier = 0.06;
        /// <summary>Share of the lost value (in log terms) that comes back over the recovery.</summary>
        public const double RecoveryShare = 0.85;
        /// <summary>How far gold rises over a decline.</summary>
        public const double GoldRise = 0.20;
        /// <summary>Swings are this many times normal in the bottom.</summary>
        public const double BottomSwingScale = 2;

        /// <summary>Property index multiplier over the decline of the mildest crash (a 25% fall). The worst crash takes 70%.</summary>
        public const double PropertyDeclineTotal = 0.75;
        public const double PropertyWorstFall = 0.70;

        /// <summary>How much a crash of this severity (0 to 1) takes off property over the decline.</summary>
        public static double PropertyFall(double severity) =>
            (1 - PropertyDeclineTotal) + (PropertyWorstFall - (1 - PropertyDeclineTotal)) * Math.Clamp(severity, 0, 1);

        /// <summary>The share of wealth in ordinary stocks and houses that decides the crash chance: 0 to 1.</summary>
        public static double ExposureOf(double riskyValue, double totalValue) =>
            totalValue <= 0 ? 0 : Math.Clamp(riskyValue / totalValue, 0, 1);

        /// <summary>Chance of a crash starting on a day, from that exposure: the base with nothing held, up to the maximum multiple.</summary>
        public static double ChanceFor(double exposure) =>
            DailyChance * (1 + (MaxChanceMultiple - 1) * Math.Clamp(exposure / FullExposureShare, 0, 1));

        /// <summary>
        /// Total fall of an ordinary stock over a decline: today's tier fall in the mildest crash (severity 0), up to 70% to 90%
        /// by tier (99% for crypto) in the worst (severity 1).
        /// </summary>
        public static double FallFor(Stock stock, double severity = 0)
        {
            double mild = stock.IsCrypto ? CryptoFall : TierFallBase + TierFallPerTier * stock.RiskTier;
            double worst = stock.IsCrypto ? CryptoWorstFall : Math.Max(mild, WorstFallBase + WorstFallPerTier * (stock.RiskTier - 3));
            return mild + (worst - mild) * Math.Clamp(severity, 0, 1);
        }

        /// <summary>The drift a stock gets on each tick in this phase of a crash of this severity.</summary>
        public static MarketDrift DriftFor(Stock stock, CrisisPhase phase, double severity = 0)
        {
            if (stock.Role == StockRole.Bond) return MarketDrift.None;
            bool safeHaven = stock.Role == StockRole.SafeHaven;

            switch (phase)
            {
                case CrisisPhase.Decline:
                    if (safeHaven)
                        return new MarketDrift(Math.Log(1 + GoldRise) / DeclineDays / Stock.TicksPerDay, 1, false);
                    return new MarketDrift(Math.Log(1 - FallFor(stock, severity)) / DeclineDays / Stock.TicksPerDay, 1, false);

                case CrisisPhase.Bottom:
                    return new MarketDrift(0, BottomSwingScale, false);

                case CrisisPhase.Recovery:
                    if (safeHaven) return MarketDrift.None; //gold drifts back to its base
                    return new MarketDrift(-Math.Log(1 - FallFor(stock, severity)) * RecoveryShare / RecoveryDays / Stock.TicksPerDay, 1, false);

                default:
                    return MarketDrift.None;
            }
        }

        /// <summary>
        /// Moves the property index for one day of a crisis of this severity. The recovery gives back <see cref="RecoveryShare"/> of
        /// the fall in log terms, like stocks. The index has no limit but <see cref="PropertyCatalog.IndexFloor"/>.
        /// </summary>
        public static void StepProperty(ref double index, CrisisPhase phase, double severity = 0)
        {
            double kept = 1 - PropertyFall(severity);
            double factor = 1;
            if (phase == CrisisPhase.Decline) factor = Math.Pow(kept, 1.0 / DeclineDays);
            else if (phase == CrisisPhase.Recovery) factor = Math.Pow(Math.Pow(1 / kept, RecoveryShare), 1.0 / RecoveryDays);
            index = Math.Max(PropertyCatalog.IndexFloor, index * factor);
        }

        /// <summary>The days a phase lasts.</summary>
        public static int LengthOf(CrisisPhase phase) =>
            phase == CrisisPhase.Decline ? DeclineDays : phase == CrisisPhase.Bottom ? BottomDays : phase == CrisisPhase.Recovery ? RecoveryDays : 0;

        /// <summary>
        /// Advances the crisis by one UTC day, starting one at random if none is running. Returns a headline to post, or null.
        /// <paramref name="random"/> is drawn only when a crash could start: the roll, then the crash's severity.
        /// <paramref name="exposure"/> (0 to 1, see <see cref="ExposureOf"/>) is only asked for on a day a roll is made.
        /// </summary>
        public static string AdvanceDay(CoinAccounts data, long day, Func<double> random, Func<double> exposure = null)
        {
            if (data.MarketPhase == CrisisPhase.None)
            {
                if (day < data.NextCrisisAllowedDay || random() >= ChanceFor(exposure?.Invoke() ?? 0)) return null;
                data.NextCrisisAllowedDay = day + CooldownDays;
                double roll = random();
                data.CrisisSeverity = roll * roll; //most crashes are moderate, a few are brutal
                return Begin(data, CrisisPhase.Decline, DeclineDays, FreefallHeadline(data.CrisisSeverity));
            }

            //a crash saved under longer phases is cut to the current length
            data.MarketPhaseDaysLeft = Math.Min(data.MarketPhaseDaysLeft, LengthOf(data.MarketPhase));
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

        /// <summary>The headline when a crash starts: how bad it is, from the severity.</summary>
        public static string FreefallHeadline(double severity)
        {
            string how = severity < 0.25 ? "📉 **A recession hits.** Prices are sliding."
                : severity < 0.6 ? "📉 **Markets are in freefall.** Stocks and property are crashing."
                : "🔥 **A GFC: markets are being wiped out.** Stocks and property are collapsing.";
            return how + " Gold is rising and government bonds are steady, so they're safer places to put money. The more stocks and houses players hold, the likelier a crash.";
        }

        private static string Begin(CoinAccounts data, CrisisPhase phase, int days, string headline)
        {
            data.MarketPhase = phase;
            data.MarketPhaseDaysLeft = days;
            return headline;
        }
    }
}
