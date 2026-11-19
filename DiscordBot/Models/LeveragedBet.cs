using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    /// <summary>
    /// A call or put (`.call`, `.put`): a leveraged bet on where one stock closes. You pay in; most of it rides at
    /// <see cref="Leverage"/> times the stock's move and the rest is the fee. When it closes it pays
    /// <c>Stake x (1 + Leverage x move)</c> on the fair price: never less than nothing, never more than
    /// <see cref="LeverageRules.MaxMultiple"/> times the stake. Only the closing price counts, so a dip in between doesn't wipe
    /// you out, and trading the stock yourself can't push it (fair price, not the traded quote).
    /// </summary>
    public class LeveragedBet
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 6);
        public ulong UserId { get; set; }
        /// <summary>Where the bet was made, so the result is posted there.</summary>
        public ulong ChannelId { get; set; }
        public string Symbol { get; set; }
        public bool IsCall { get; set; }
        public int Leverage { get; set; }
        /// <summary>Everything the player paid.</summary>
        public double Paid { get; set; }
        /// <summary>The part of it that rides. The rest was the fee, into the jackpot.</summary>
        public double Stake { get; set; }
        /// <summary>The stock's fair price when the bet was made.</summary>
        public double EntryPrice { get; set; }
        public long OpenedUtcTicks { get; set; }
        public long ExpiresUtcTicks { get; set; }

        public double Fee => Math.Round(Paid - Stake, 2);
        public DateTime ExpiresAt => new DateTime(ExpiresUtcTicks, DateTimeKind.Utc);
        public string Kind => IsCall ? "call" : "put";

        /// <summary>What it pays if it closes at this fair price.</summary>
        public double ValueAt(double fairPrice) => LeverageRules.Value(Stake, IsCall, Leverage, EntryPrice, fairPrice);
    }

    public static class LeverageRules
    {
        public static readonly int[] Leverages = { 2, 5, 10 };
        public const int DefaultLeverage = 5;
        /// <summary>A bet pays at most this many times what rides.</summary>
        public const double MaxMultiple = 10;
        public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);
        public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);
        /// <summary>
        /// On average a bet pays back this much of what was paid in, the same for every stock, leverage and length. The fee makes
        /// it so: it's worked out by simulating the real price process (<see cref="LeveragePricer"/>), so it's dear when the bet is
        /// likely to pay (a stock far below its normal price tends to bounce, so calls on it cost more; wild stocks at 10x are dear)
        /// and small when it isn't.
        /// </summary>
        public const double HouseReturn = 0.95;
        /// <summary>The fee is always at least this share of what's paid.</summary>
        public const double MinFeeShare = 0.02;
        /// <summary>A bet whose fee would take more than this share of what's paid is refused (try less leverage or a shorter bet).</summary>
        public const double MaxFeeShare = 0.80;

        /// <summary>Below this average payback the payout table warns that the bet is against the odds (it still sells; some people like pain).</summary>
        public const double PoorPayback = 0.85;

        public static bool TryParseLeverage(string text, out int leverage)
        {
            leverage = 0;
            string t = (text ?? "").Trim().ToLowerInvariant();
            return t.EndsWith("x") && int.TryParse(t.TrimEnd('x'), out leverage) && Leverages.Contains(leverage);
        }

        /// <summary>How far the stock moved the bet's way: +0.1 when a call's stock rose 10% or a put's fell 10%.</summary>
        public static double Move(bool isCall, double entry, double price) =>
            entry <= 0 ? 0 : isCall ? price / entry - 1 : 1 - price / entry;

        public static double Value(double stake, bool isCall, int leverage, double entry, double price) =>
            Math.Round(Math.Clamp(stake * (1 + leverage * Move(isCall, entry, price)), 0, stake * MaxMultiple), 2);

        /// <summary>The closing price at or past which the bet pays nothing.</summary>
        public static double WipeoutPrice(bool isCall, int leverage, double entry) =>
            isCall ? entry * (1 - 1.0 / leverage) : entry * (1 + 1.0 / leverage);

        /// <summary>The closing price at or past which it pays the most, or null when it can't get there (a put can't pass a price of 0).</summary>
        public static double? MaxOutPrice(bool isCall, int leverage, double entry)
        {
            double move = (MaxMultiple - 1) / leverage;
            if (isCall) return entry * (1 + move);
            return move < 1 ? entry * (1 - move) : (double?)null;
        }

        /// <summary>The share of what's paid that rides, for a bet expected to pay back <paramref name="expectedMultiple"/> times its stake.</summary>
        public static double RidingShare(double expectedMultiple) =>
            Math.Round(Math.Min(1 - MinFeeShare, HouseReturn / Math.Max(0.0001, expectedMultiple)), 4);

        /// <summary>The move needed at close to get back what was paid.</summary>
        public static double BreakEvenMove(double ridingShare, int leverage) => (1 / ridingShare - 1) / leverage;

        public static int TicksFor(TimeSpan duration) => Math.Max(1, (int)Math.Round(duration.TotalMinutes / 5));
    }

    /// <summary>
    /// What a call or put pays back on average per dollar that rides, found like the old options were: run the real tick process
    /// (<see cref="Stock.ApplyTick(Func{double}, MarketDrift)"/>) forward from the stock's state many times for the bet's length and
    /// average the payout at close. Cached per state.
    /// </summary>
    public static class LeveragePricer
    {
        private const int OrdinaryTickBudget = 1_000_000;
        private const int CryptoTickBudget = 2_000_000;
        private const int MinPaths = 3_000;
        private const int MaxPaths = 60_000;
        private const int MaxCached = 4_000;

        private readonly record struct Key(string Symbol, int RiskTier, StockRole Role, bool IsCall, int Leverage, int Ticks, double Fair, double Base,
            int Momentum, int Recovery, int MegaTicks, double MegaStep, double DriftLog, double DriftSwing, bool DriftReverts, double AnchorRatio);

        private static readonly Dictionary<Key, double> _cache = new Dictionary<Key, double>();
        private static readonly object _lock = new object();

        private static double AnchorRatio(Stock stock) =>
            stock.CrisisAnchor > 0 && stock.FairPrice > 0 ? Math.Round(stock.CrisisAnchor / stock.FairPrice, 3) : 0;

        public static double ExpectedMultiple(Stock stock, bool isCall, int leverage, int ticks, MarketDrift drift)
        {
            var key = new Key(stock.Symbol, stock.RiskTier, stock.Role, isCall, leverage, ticks, Math.Round(stock.FairPrice, 4), stock.BasePrice,
                stock.MomentumTicks, stock.CrashRecoveryTicks, stock.MegaTicks, stock.MegaStep, drift.LogPerTick, drift.SwingScale, drift.AllowReversion, AnchorRatio(stock));
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out double cached)) return cached;
            }
            int budget = stock.IsCrypto ? CryptoTickBudget : OrdinaryTickBudget;
            int paths = Math.Clamp(budget / ticks, MinPaths, MaxPaths);
            double value = Simulate(stock, isCall, leverage, ticks, drift, paths, seed: 71_003 + ticks * 7 + leverage * 2 + (isCall ? 1 : 0));
            lock (_lock)
            {
                if (_cache.Count >= MaxCached) _cache.Clear();
                _cache[key] = value;
            }
            return value;
        }

        /// <summary>Average payout at close per dollar riding. Public so tests can check a quote against an independent run.</summary>
        public static double Simulate(Stock stock, bool isCall, int leverage, int ticks, MarketDrift drift, int paths, int seed)
        {
            var random = new Random(seed);
            Func<double> next = random.NextDouble;
            double entry = stock.FairPrice;
            var scratch = new Stock { Symbol = stock.Symbol, RiskTier = stock.RiskTier, Role = stock.Role, BasePrice = stock.BasePrice };

            double total = 0;
            for (int path = 0; path < paths; path++)
            {
                scratch.FairPrice = entry;
                scratch.Price = entry;
                scratch.PreviousPrice = entry;
                scratch.Impact = 0;
                scratch.MomentumTicks = stock.MomentumTicks;
                scratch.CrashRecoveryTicks = stock.CrashRecoveryTicks;
                scratch.MegaTicks = stock.MegaTicks;
                scratch.MegaStep = stock.MegaStep;
                scratch.CrisisAnchor = entry * AnchorRatio(stock);
                for (int tick = 0; tick < ticks; tick++)
                    scratch.ApplyTick(next, drift);
                total += LeverageRules.Value(1, isCall, leverage, entry, scratch.FairPrice);
            }
            return total / paths;
        }
    }
}
