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
        /// <summary>
        /// Options longer than this many ticks (24 hours) are priced as this long. The price has forgotten where it started by then, and
        /// a premium barely changes beyond it (a crypto call costs the same for 24 hours as for 3 days), while a longer run is slow.
        /// </summary>
        public const int MaxPricedTicks = 288;
        private const int OrdinaryTickBudget = 1_000_000;
        private const int CryptoTickBudget = 2_000_000;
        private const int QuietTickBudget = 1_000_000;
        private const int MinPaths = 2_000;
        private const int MaxPaths = 40_000;
        private const int MaxCached = 4_000;

        private readonly record struct Key(string Symbol, int RiskTier, StockRole Role, bool IsCall, int Ticks, double Fair, double Base,
            int Momentum, int Recovery, int MegaTicks, double MegaStep, double DriftLog, double DriftSwing, bool DriftReverts);

        private static readonly Dictionary<Key, double> _cache = new Dictionary<Key, double>();
        private static readonly object _lock = new object();

        /// <summary>The average payoff per share of an at-the-money option over <paramref name="ticks"/> 5 minute ticks, before any house edge.</summary>
        public static double FairValuePerShare(Stock stock, bool isCall, int ticks, MarketDrift drift)
        {
            ticks = Math.Clamp(ticks, 1, MaxPricedTicks);
            var key = new Key(stock.Symbol, stock.RiskTier, stock.Role, isCall, ticks, stock.FairPrice, stock.BasePrice,
                stock.MomentumTicks, stock.CrashRecoveryTicks, stock.MegaTicks, stock.MegaStep, drift.LogPerTick, drift.SwingScale, drift.AllowReversion);
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out double cached)) return cached;
            }

            int budget = stock.IsCrypto ? CryptoTickBudget : OrdinaryTickBudget;
            int paths = Math.Clamp(budget / ticks, MinPaths, MaxPaths);
            int seed = 40_503 + ticks * 2 + (isCall ? 1 : 0);
            double value = stock.IsCrypto && isCall
                ? CryptoCallValue(stock, ticks, drift, paths, seed)
                : Simulate(stock, isCall, ticks, drift, paths, seed);

            lock (_lock)
            {
                if (_cache.Count >= MaxCached) _cache.Clear();
                _cache[key] = value;
            }
            return value;
        }

        /// <summary>The most a quote is ever worth per share. Far more than anyone could pay; it only keeps a call in a crash, where nothing fades a boom, from overflowing.</summary>
        public const double MaxFairValuePerShare = 1e15;

        /// <summary>
        /// An uncapped call on crypto. Its value is dominated by rare giant booms, and by two or three of them stacking, which no simulation
        /// of a few thousand paths ever sees, so a plain average comes out low and jumps about by a third between seeds. Instead it uses
        /// <c>call = E[price at expiry] - strike + put</c> (which always holds): the put is bounded and simulated as usual, and the expected
        /// price is the simulated price with mega booms switched off times a boom factor worked out exactly (<see cref="BoomFactor"/>).
        /// One run gives the answer for every shorter expiry too, and a call never costs less than a shorter one: a coin that starts quiet
        /// is likelier to boom in the next hour than after a streak has had time to start.
        /// </summary>
        public static double CryptoCallValue(Stock stock, int ticks, MarketDrift drift, int paths, int seed)
        {
            double strike = stock.FairPrice;
            var scratch = new Stock { Symbol = stock.Symbol, RiskTier = stock.RiskTier, Role = stock.Role, BasePrice = stock.BasePrice };
            void Reset()
            {
                scratch.FairPrice = strike;
                scratch.Price = strike;
                scratch.PreviousPrice = strike;
                scratch.Impact = 0;
                scratch.MomentumTicks = stock.MomentumTicks;
                scratch.CrashRecoveryTicks = stock.CrashRecoveryTicks;
                scratch.MegaTicks = stock.MegaTicks;
                scratch.MegaStep = stock.MegaStep;
            }

            //the put for every expiry up to this one, with booms (a boom only ever makes a put worth nothing)
            var put = new double[ticks];
            var random = new Random(seed - 1);
            Func<double> next = random.NextDouble;
            for (int path = 0; path < paths; path++)
            {
                Reset();
                for (int tick = 0; tick < ticks; tick++)
                {
                    scratch.ApplyTick(next, drift);
                    put[tick] += Math.Max(0, strike - scratch.FairPrice);
                }
            }

            //the expected price for every expiry with no booms, how often each tick is an idle one, and the pull back towards the base
            int quietPaths = Math.Clamp(QuietTickBudget / ticks, MinPaths, MaxPaths / 2);
            random = new Random(seed + 17);
            next = random.NextDouble;
            scratch.DisableMegaBoom = true;
            var price = new double[ticks];
            var idle = new double[ticks];
            var reversion = new double[ticks];
            for (int path = 0; path < quietPaths; path++)
            {
                Reset();
                for (int tick = 0; tick < ticks; tick++)
                {
                    int idleBefore = scratch.PricingIdleTicks;
                    double reversionBefore = scratch.PricingReversionSum;
                    scratch.ApplyTick(next, drift);
                    price[tick] += scratch.FairPrice;
                    idle[tick] += scratch.PricingIdleTicks - idleBefore;
                    reversion[tick] += scratch.PricingReversionSum - reversionBefore;
                }
            }
            for (int tick = 0; tick < ticks; tick++)
            {
                idle[tick] /= quietPaths;
                reversion[tick] /= quietPaths;
            }

            double best = 0;
            for (int expiry = 1; expiry <= ticks; expiry++)
            {
                double expectedPrice = price[expiry - 1] / quietPaths * BoomFactor(idle, reversion, expiry);
                best = Math.Max(best, expectedPrice - strike + put[expiry - 1] / paths);
            }
            return Math.Min(MaxFairValuePerShare, best);
        }

        /// <summary>
        /// How much mega booms multiply the expected price at expiry, <paramref name="ticks"/> ticks from now. A boom starts on an idle tick with chance <see cref="Stock.CryptoMegaChance"/>
        /// and adds its log size to the price over its next few ticks, and the price then fades back towards the base. Log sizes add, so the
        /// expected price is the product over booms of what each is worth at expiry (<see cref="MegaGrowth"/>). A boom keeps the next one from
        /// starting until it ends, so this walks back from the expiry: <c>F[t] = (1-p) F[t+1] + p * average over lengths of growth * F[t+length]</c>.
        /// <paramref name="idleShare"/> is how often each tick is an idle one (a streak or a rebound stops a boom starting) and
        /// <paramref name="reversion"/> the average pull towards the base on each tick.
        /// </summary>
        public static double BoomFactor(double[] idleShare, double[] reversion, int ticks)
        {
            //what is left of a log move after the pull on every tick from i on
            var remaining = new double[ticks + 1];
            remaining[ticks] = 1;
            for (int i = ticks - 1; i >= 0; i--) remaining[i] = remaining[i + 1] * (1 - reversion[i]);

            var factor = new double[ticks + 1];
            factor[ticks] = 1;
            int lengths = Stock.CryptoMegaMaxTicks - Stock.CryptoMegaMinTicks + 1;
            for (int tick = ticks - 1; tick >= 0; tick--)
            {
                double chance = Stock.CryptoMegaChance * idleShare[tick];
                double starts = 0;
                for (int length = Stock.CryptoMegaMinTicks; length <= Stock.CryptoMegaMaxTicks; length++)
                {
                    //the move is spread over its first ticks and isn't pulled back until it ends. One cut short by the expiry has only part of it in
                    int ticksLeft = ticks - tick;
                    double kept = ticksLeft >= length ? remaining[tick + length] : (double)ticksLeft / length;
                    starts += MegaGrowth(kept) * factor[Math.Min(ticks, tick + length)] / lengths;
                }
                factor[tick] = (1 - chance) * factor[tick + 1] + chance * starts;
            }
            return factor[0];
        }

        /// <summary>
        /// The expected value of <c>e^(kept * size)</c> for a mega boom size (<see cref="Stock.MegaSize"/>): the multiple the price has when the
        /// fraction <paramref name="kept"/> of the boom's log move is still there. Closed form for the exponential tail cut off at the biggest size.
        /// </summary>
        public static double MegaGrowth(double kept)
        {
            double range = Stock.CryptoMegaMaxLog - Stock.CryptoMegaMinLog;
            double smallest = Math.Exp(kept * Stock.CryptoMegaMinLog);
            double gap = 1 - kept;
            if (gap < 1e-9) return smallest * (range + 1);
            double atCutOff = Math.Exp(-gap * range); //the cut-off size's own chance, e^-range, times e^(kept * range)
            return smallest * ((1 - atCutOff) / gap + atCutOff);
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
                scratch.MegaTicks = stock.MegaTicks;
                scratch.MegaStep = stock.MegaStep;
                for (int tick = 0; tick < ticks; tick++)
                    scratch.ApplyTick(next, drift);
                double end = scratch.FairPrice;
                double payoff = isCall ? Math.Max(0, end - strike) : Math.Max(0, strike - end);
                total += payoff; //there is no cap (see OptionContract.PayoutAt)
            }
            return total / paths;
        }
    }
}
