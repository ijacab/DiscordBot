using System;
using System.Collections.Generic;

namespace DiscordBot.Models
{
    /// <summary>What kind of listing a stock is. Safe havens and bonds are not hit the way ordinary stocks are in a crisis.</summary>
    public enum StockRole
    {
        /// <summary>An ordinary company stock, from the random pool.</summary>
        Normal,
        /// <summary>Gold: rallies while the market falls, then fades back to its base.</summary>
        SafeHaven,
        /// <summary>Government bonds: a small steady yield, almost no swing, unaffected by a crisis.</summary>
        Bond
    }

    public class Stock
    {
        public const double MinPrice = 1.00;
        /// <summary>5 minute ticks in a day.</summary>
        public const int TicksPerDay = 24 * 60 / 5;
        /// <summary>Bond yield per day, as a log move. About 0.02% a day, or 7% a year.</summary>
        public const double BondGrowthPerDay = 0.0002;
        /// <summary>Bond noise per tick, as a log move. Tiny, so the yield is what shows.</summary>
        public const double BondNoise = 0.0001;
        public const int MinTier = 1;
        public const int MaxTier = 6;
        /// <summary>The crypto tier: calm-ish noise, then sudden bursts of mooning or crashing, and a rebound after a crash.</summary>
        public const int CryptoTier = 6;
        /// <summary>Normal crypto noise per tick, as a log move (about ±12%). Symmetric in log space, so it has no downward drift.</summary>
        public const double CryptoSwing = 0.12;
        /// <summary>Chance per idle tick that a moon streak starts.</summary>
        public const double CryptoMoonChance = 0.025;
        /// <summary>Chance per idle tick that a crash streak starts.</summary>
        public const double CryptoCrashChance = 0.02;
        /// <summary>A streak lasts this many ticks (15 to 40 minutes).</summary>
        public const int CryptoStreakMinTicks = 3, CryptoStreakMaxTicks = 8;
        /// <summary>Each tick of a streak moves the price by this log amount (+10% to +42% up, or the same down).</summary>
        public const double CryptoStepMin = 0.10, CryptoStepMax = 0.35;
        /// <summary>Ticks (about 4 hours) of recovery after a crash streak ends: a short bottom, then a rebound.</summary>
        public const int CryptoRecoveryTicks = 46;
        /// <summary>The last this many recovery ticks are the rebound. The ticks before them are the bottom.</summary>
        public const int CryptoReboundTicks = 40;
        /// <summary>Largest log move per tick at the bottom, up or down.</summary>
        public const double CryptoBottomSwing = 0.04;
        /// <summary>Largest log move per tick while rebounding, up or down.</summary>
        public const double CryptoReboundSwing = 0.06;
        /// <summary>Fraction of the log gap to the base price closed per tick while rebounding. Strong enough to beat the noise.</summary>
        public const double CryptoReboundReversion = 0.06;
        /// <summary>Pull back to the base price on normal ticks, which bleeds off a moon over a few hours.</summary>
        public const double CryptoMeanReversionRate = 0.03;
        /// <summary>Option pricing volatility per tick for crypto, covering the bursts as well as the noise.</summary>
        public const double CryptoTickVolatility = 0.25;
        /// <summary>Shares a crypto is issued with. A thin market, so trades move it more.</summary>
        public const double CryptoSharesOutstanding = 20_000;
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
            { 5, 0.30 },
            { 6, 0.35 } //crypto: the biggest single-tick move, which only a burst reaches
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
        /// <summary>Ticks of recovery left after a crypto crash streak. 0 when not recovering.</summary>
        public int CrashRecoveryTicks { get; set; }
        /// <summary>Ticks left of a crypto burst: positive for a moon, negative for a crash. 0 when none.</summary>
        public int MomentumTicks { get; set; }
        /// <summary>Quoted price at each tick, oldest first.</summary>
        public List<double> History { get; set; } = new List<double>();

        public double PercentChange => PreviousPrice <= 0 ? 0 : (Price - PreviousPrice) / PreviousPrice * 100;

        /// <summary>% change of the quote from the launch price (<see cref="BasePrice"/>). Unlike the kept history, this never rolls off.</summary>
        public double AllTimeChange => BasePrice <= 0 ? 0 : (Price - BasePrice) / BasePrice * 100;

        public bool IsCrypto => RiskTier == CryptoTier;

        /// <summary>Normal, safe haven or bond. Old saved stocks load as Normal.</summary>
        public StockRole Role { get; set; } = StockRole.Normal;

        /// <summary>Marks a safe haven or bond in listings. Null for an ordinary stock.</summary>
        public string TypeEmoji => Role == StockRole.SafeHaven ? "🥇" : Role == StockRole.Bond ? "🏦" : null;

        /// <summary>What kind of listing this is, for headers: "safe haven", "bond", or the tier for ordinary stocks.</summary>
        public string KindLabel => Role == StockRole.SafeHaven ? "safe haven" : Role == StockRole.Bond ? "bond" : $"tier {RiskTier}";

        /// <summary>Standard deviation of one tick's move as a fraction of the price. Used to price options.</summary>
        public double TickVolatility => IsCrypto ? CryptoTickVolatility : MaxSwingByTier[RiskTier] / Math.Sqrt(3);

        /// <summary>
        /// Moves the fair price by a random amount within this stock's tier swing, pulls it back towards <see cref="BasePrice"/>,
        /// then lets the trading impact fade. <paramref name="random"/> returns a value in [0, 1).
        /// Crypto works in log space and has bursts: a moon or crash streak of several big steps, then after a crash a short
        /// bottom and a strong rebound. Random draws, in order: the noise, then for a streak tick the step size, or for an idle
        /// tick the event roll (and, if a streak starts, its length and then the first step size).
        /// </summary>
        public void ApplyTick(Func<double> random) => ApplyTick(random, MarketDrift.None);

        /// <summary>As <see cref="ApplyTick(Func{double})"/>, with a market-wide crisis drift on top (see <see cref="MarketCycle"/>).</summary>
        public void ApplyTick(Func<double> random, MarketDrift drift)
        {
            double noise = random() * 2 - 1; //uniform in [-1, +1)

            if (Role == StockRole.Bond)
            {
                //bonds don't round to the cent, or the small daily yield would be lost on every tick
                double bondMoved = FairPrice * Math.Exp(BondGrowthPerDay / TicksPerDay + noise * BondNoise);
                PreviousPrice = Price;
                FairPrice = Math.Max(MinPrice, bondMoved);
                Impact *= ImpactDecayPerTick;
                RefreshQuote();
                return;
            }

            double moved;
            double reversion = MeanReversionRate;

            if (!IsCrypto)
            {
                moved = FairPrice * (1 + noise * MaxSwingByTier[RiskTier] * drift.SwingScale);
            }
            else if (MomentumTicks != 0)
            {
                int direction = Math.Sign(MomentumTicks);
                moved = FairPrice * Math.Exp(direction * StreakStep(random));
                MomentumTicks -= direction;
                if (MomentumTicks == 0 && direction < 0)
                    CrashRecoveryTicks = CryptoRecoveryTicks;
                reversion = 0;
            }
            else if (CrashRecoveryTicks > 0)
            {
                bool bottom = CrashRecoveryTicks > CryptoReboundTicks;
                CrashRecoveryTicks--;
                moved = FairPrice * Math.Exp(noise * (bottom ? CryptoBottomSwing : CryptoReboundSwing));
                reversion = bottom ? 0 : CryptoReboundReversion;
            }
            else
            {
                double roll = random();
                bool crash = roll < CryptoCrashChance;
                bool moon = !crash && roll < CryptoCrashChance + CryptoMoonChance;
                if (crash || moon)
                {
                    int length = CryptoStreakMinTicks + (int)(random() * (CryptoStreakMaxTicks - CryptoStreakMinTicks + 1));
                    int direction = crash ? -1 : 1;
                    moved = FairPrice * Math.Exp(direction * StreakStep(random));
                    MomentumTicks = direction * (length - 1); //this tick was the first of the streak
                    reversion = 0;
                }
                else
                {
                    moved = FairPrice * Math.Exp(noise * CryptoSwing);
                    reversion = CryptoMeanReversionRate;
                }
            }

            moved *= Math.Exp(drift.LogPerTick);
            moved = Math.Max(MinPrice, moved);
            if (!drift.AllowReversion) reversion = 0; //during a crisis the price follows the drift, not the old base
            //a fixed percentage move up or down has a negative average log change, so the price would slowly decay without this
            if (BasePrice > 0 && reversion > 0)
                moved *= Math.Pow(BasePrice / moved, reversion);

            PreviousPrice = Price;
            //four decimals, so a slow crisis drift on a cheap stock isn't rounded away every tick (the quote is still shown to the cent)
            FairPrice = Math.Max(MinPrice, Math.Round(moved, 4));
            Impact *= ImpactDecayPerTick;
            RefreshQuote();
        }

        private static double StreakStep(Func<double> random) => CryptoStepMin + random() * (CryptoStepMax - CryptoStepMin);

        /// <summary>
        /// The fill price for a trade of <paramref name="shares"/> (negative to sell) and the impact it would leave.
        /// The mid fill is the midpoint of the quote before and after the move, so a big order pays for part of its own impact.
        /// The spread is then added on a buy or taken off on a sell, so a round trip always costs <see cref="Spread"/>.
        /// Does not change the stock.
        /// </summary>
        public (double Fill, double NewImpact) PreviewTrade(int shares) => PreviewTrade(shares, Spread);

        /// <summary>As <see cref="PreviewTrade(int)"/> with a different spread, e.g. none for an Insider Trader.</summary>
        public (double Fill, double NewImpact) PreviewTrade(int shares, double spread)
        {
            double move = ImpactCoefficient * Math.Sqrt(Math.Abs(shares) / SharesOutstanding) * Math.Sign(shares);
            double newImpact = Math.Max(-MaxImpact, Math.Min(MaxImpact, Impact + move));
            double after = PriceAt(FairPrice, newImpact);
            double mid = (Price + after) / 2;
            double fill = Math.Max(MinPrice, mid * (1 + Math.Sign(shares) * spread / 2));
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
        public int MaxAffordableShares(double cash) => MaxAffordableShares(cash, Spread);

        public int MaxAffordableShares(double cash, double spread)
        {
            if (cash < Price) return 0;
            int low = 0;
            int high = (int)Math.Min(int.MaxValue, Math.Floor(cash / Price));
            while (low < high)
            {
                int mid = low + (high - low + 1) / 2;
                double cost = Math.Round(mid * PreviewTrade(mid, spread).Fill, 2);
                if (cost <= cash) low = mid;
                else high = mid - 1;
            }
            return low;
        }

        /// <summary>
        /// The largest number of shares that can be shorted with <paramref name="cash"/>: the margin (the sale value) plus the
        /// sale tax must fit. Selling pushes the fill down, so this searches rather than dividing.
        /// </summary>
        public int MaxShortableShares(double cash)
        {
            int low = 0;
            int high = (int)Math.Min(int.MaxValue, Math.Floor(cash / MinPrice));
            while (low < high)
            {
                int mid = low + (high - low + 1) / 2;
                double gross = Math.Round(mid * PreviewTrade(-mid).Fill, 2);
                if (gross + TaxOn(gross) <= cash) low = mid;
                else high = mid - 1;
            }
            return low;
        }

        /// <summary>Shares in one option contract.</summary>
        public const int OptionContractSize = 100;
        /// <summary>The approximate value of an at-the-money option as a multiple of price × volatility × √time.</summary>
        public const double OptionValueFactor = 0.4;
        /// <summary>Extra the house charges on the fair premium.</summary>
        public const double OptionHouseEdge = 1.10;
        /// <summary>Fee on the premium, paid into the jackpot.</summary>
        public const double OptionFeeRate = 0.02;
        public const double MinOptionPremiumPerShare = 0.01;

        /// <summary>
        /// Premium per share of an at-the-money option lasting <paramref name="duration"/>, from this stock's tick volatility.
        /// Longer and wilder stocks cost more.
        /// </summary>
        public double OptionPremiumPerShare(TimeSpan duration)
        {
            double ticks = Math.Max(1, duration.TotalMinutes / 5);
            double fair = FairPrice * TickVolatility * Math.Sqrt(ticks) * OptionValueFactor;
            return Math.Max(MinOptionPremiumPerShare, Math.Round(fair * OptionHouseEdge, 2));
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
