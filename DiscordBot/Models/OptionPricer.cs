using System;
using System.Collections.Generic;

namespace DiscordBot.Models
{
    /// <summary>
    /// What an at-the-money call or put is worth, found by running the real price process (<see cref="Stock.ApplyTick(Func{double}, MarketDrift)"/>)
    /// forward from the stock's current state many times and averaging what the option would pay. So the price follows how the
    /// stock really moves: it is pulled back towards its base, a crypto bursts and rebounds, and a crash drifts it. A stock far
    /// below its base has dear calls and cheap puts, and the other way round. The same random numbers are used for every quote,
    /// so a quote is steady for a given state, and results are cached until the state changes.
    /// </summary>
    public static class OptionPricer
    {
        /// <summary>The longest option, in ticks (3 days).</summary>
        public const int MaxPricedTicks = 864;
        private const int OrdinaryTickBudget = 1_000_000;
        private const int CryptoTickBudget = 3_000_000;
        private const int MinPaths = 2_000;
        private const int MaxPaths = 12_000;
        private const int MaxCached = 4_000;

        private readonly record struct Key(string Symbol, int RiskTier, StockRole Role, bool IsCall, int Ticks, double Fair, double Base,
            int Momentum, int Recovery, double DriftLog, double DriftSwing, bool DriftReverts);

        private static readonly Dictionary<Key, double> _cache = new Dictionary<Key, double>();
        private static readonly object _lock = new object();

        /// <summary>The average payoff per share of an at-the-money option over <paramref name="ticks"/> 5 minute ticks, before any house edge.</summary>
        public static double FairValuePerShare(Stock stock, bool isCall, int ticks, MarketDrift drift)
        {
            ticks = Math.Clamp(ticks, 1, MaxPricedTicks);
            var key = new Key(stock.Symbol, stock.RiskTier, stock.Role, isCall, ticks, stock.FairPrice, stock.BasePrice,
                stock.MomentumTicks, stock.CrashRecoveryTicks, drift.LogPerTick, drift.SwingScale, drift.AllowReversion);
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out double cached)) return cached;
            }

            int budget = stock.IsCrypto ? CryptoTickBudget : OrdinaryTickBudget;
            int paths = Math.Clamp(budget / ticks, MinPaths, MaxPaths);
            double value = Simulate(stock, isCall, ticks, drift, paths, seed: 40_503 + ticks * 2 + (isCall ? 1 : 0));

            lock (_lock)
            {
                if (_cache.Count >= MaxCached) _cache.Clear();
                _cache[key] = value;
            }
            return value;
        }

        /// <summary>Runs the process forward and averages the payoff. Public so tests can check a quote against an independent run.</summary>
        public static double Simulate(Stock stock, bool isCall, int ticks, MarketDrift drift, int paths, int seed)
        {
            var random = new Random(seed);
            Func<double> next = random.NextDouble;
            double strike = stock.FairPrice;
            var scratch = new Stock { Symbol = stock.Symbol, RiskTier = stock.RiskTier, Role = stock.Role, BasePrice = stock.BasePrice };

            double total = 0;
            for (int path = 0; path < paths; path++)
            {
                scratch.FairPrice = strike;
                scratch.Price = strike;
                scratch.PreviousPrice = strike;
                scratch.Impact = 0;
                scratch.MomentumTicks = stock.MomentumTicks;
                scratch.CrashRecoveryTicks = stock.CrashRecoveryTicks;
                for (int tick = 0; tick < ticks; tick++)
                    scratch.ApplyTick(next, drift);
                double end = scratch.FairPrice;
                double payoff = isCall ? Math.Max(0, end - strike) : Math.Max(0, strike - end);
                total += Math.Min(payoff, strike * Stock.MaxOptionPayoutMultiple); //as OptionContract.PayoutAt
            }
            return total / paths;
        }
    }
}
