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
        /// <summary>
        /// Crypto can crash far further than other stocks (a mega crash is up to 100,000x down), so its floor is a cent. Prices under a
        /// dollar are kept to 4 decimals (<see cref="RoundPrice"/>).
        /// </summary>
        public const double CryptoMinPrice = 0.01;
        /// <summary>5 minute ticks in a day.</summary>
        public const int TicksPerDay = 24 * 60 / 5;
        /// <summary>Bond yield per hour, as a log move: 0.1% an hour, about 2.4% a day. Scaled to the game's hours (rent is 3.5% an hour), so a bond is a steady holding over a 2 to 4 week season.</summary>
        public const double BondGrowthPerHour = 0.001;
        /// <summary>Bond noise per tick, as a log move. Tiny, so the yield is what shows.</summary>
        public const double BondNoise = 0.0001;
        public const int MinTier = 1;
        public const int MaxTier = 6;
        /// <summary>The crypto tier: calm-ish noise, then sudden bursts of mooning or crashing, and a rebound after a crash.</summary>
        public const int CryptoTier = 6;
        /// <summary>Normal crypto noise per tick, as a log move (about ±12%). Symmetric in log space, so it has no downward drift.</summary>
        public const double CryptoSwing = 0.12;
        /// <summary>Chance per idle tick that a moon streak starts. Equal to <see cref="CryptoCrashChance"/>.</summary>
        public const double CryptoMoonChance = 0.0225;
        /// <summary>
        /// Chance per idle tick that a crash streak starts. Moons and crashes are equally likely and equally big (in log terms). Bigger
        /// or more frequent moons than crashes made the average crypto price several times its launch price, so holding it was a
        /// money machine and calls on it could never compete.
        /// </summary>
        public const double CryptoCrashChance = 0.0225;
        /// <summary>A streak lasts this many ticks (15 to 40 minutes).</summary>
        public const int CryptoStreakMinTicks = 3, CryptoStreakMaxTicks = 8;
        /// <summary>Each tick of a streak moves the price by this log amount (+5% to +16% up, or the same down in log terms).</summary>
        public const double CryptoStepMin = 0.05, CryptoStepMax = 0.15;
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
        /// <summary>
        /// Mega events: a crypto can boom or crash by 10x up to 100,000x. Each is checked per idle tick, after the small streaks above,
        /// and the two are equally likely. A coin is idle (not in a streak or a recovery) about 44% of the time, so a chance of 1 in 250
        /// per idle tick comes to about one boom and one crash every 2 days, or about 10 of each in a 3 week season.
        /// </summary>
        public const double CryptoMegaChance = 1.0 / 250;
        /// <summary>
        /// The size of a mega event is a log move of at least ln 10 plus an exponential extra, cut off at ln 100,000: every one is at
        /// least 10x, 1 in 10 reaches 100x, 1 in 100 reaches 1,000x, 1 in 1,000 reaches 10,000x, and none passes 100,000x.
        /// </summary>
        public const double CryptoMegaMinLog = 2.302585092994046, CryptoMegaMaxLog = 11.512925464970229;
        /// <summary>A mega move is spread over this many ticks, so the chart ramps. Afterwards it fades back at the normal pull.</summary>
        public const int CryptoMegaMinTicks = 2, CryptoMegaMaxTicks = 6;
        /// <summary>Selling can push a crypto's quote down at most this far (as a fraction of the fair price). Buying has no limit: the market is thin.</summary>
        public const double CryptoMaxSellImpact = 0.95;
        /// <summary>Pull back to the base price on normal ticks, which bleeds off a moon over a few hours.</summary>
        public const double CryptoMeanReversionRate = 0.03;
        /// <summary>A rough per-tick volatility for crypto, covering the bursts as well as the noise.</summary>
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
        /// <summary>Ticks left of a crypto mega boom (positive) or mega crash (negative). 0 when none.</summary>
        public int MegaTicks { get; set; }
        /// <summary>The log move each tick of the running mega event (always positive; the sign is on <see cref="MegaTicks"/>).</summary>
        public double MegaStep { get; set; }
        /// <summary>Pricing only (<see cref="OptionPricer"/>): never start a mega boom, so the rest of the process can be simulated and the boom priced exactly.</summary>
        [Newtonsoft.Json.JsonIgnore] public bool DisableMegaBoom { get; set; }
        /// <summary>Pricing only: how many ticks so far were idle ones (the only ticks a mega event can start on).</summary>
        [Newtonsoft.Json.JsonIgnore] public int PricingIdleTicks { get; set; }
        /// <summary>Pricing only: the pull towards the base applied so far, summed over ticks.</summary>
        [Newtonsoft.Json.JsonIgnore] public double PricingReversionSum { get; set; }
        /// <summary>Quoted price at each tick, oldest first.</summary>
        public List<double> History { get; set; } = new List<double>();

        public double PercentChange => PreviousPrice <= 0 ? 0 : (Price - PreviousPrice) / PreviousPrice * 100;

        /// <summary>% change of the quote from the launch price (<see cref="BasePrice"/>). Unlike the kept history, this never rolls off.</summary>
        public double AllTimeChange => BasePrice <= 0 ? 0 : (Price - BasePrice) / BasePrice * 100;

        public bool IsCrypto => RiskTier == CryptoTier;

        /// <summary>The lowest this stock's price can go: a dollar, or a cent for crypto.</summary>
        public double FloorPrice => IsCrypto ? CryptoMinPrice : MinPrice;

        /// <summary>Rounds a price to the cent, or to 4 decimals under a dollar, so a cheap crypto isn't bought at a rounded-down price.</summary>
        public static double RoundPrice(double price) => price < 1 ? Math.Round(price, 4) : Math.Round(price, 2);

        /// <summary>A price for text: cents normally, 4 decimals under a dollar (a crashed crypto).</summary>
        public static string FormatPrice(double price) => price < 1 ? price.ToString("N4") : price.ToString("N2");

        /// <summary>Normal, safe haven or bond. Old saved stocks load as Normal.</summary>
        public StockRole Role { get; set; } = StockRole.Normal;

        /// <summary>Marks a safe haven or bond in listings. Null for an ordinary stock.</summary>
        public string TypeEmoji => Role == StockRole.SafeHaven ? "🥇" : Role == StockRole.Bond ? "🏦" : null;

        /// <summary>What kind of listing this is, for headers: "safe haven", "bond", or the tier for ordinary stocks.</summary>
        public string KindLabel => Role == StockRole.SafeHaven ? "safe haven" : Role == StockRole.Bond ? "bond" : $"tier {RiskTier}";

        /// <summary>Standard deviation of one tick's move as a fraction of the price (options are now priced by simulation, see <see cref="OptionPricer"/>).</summary>
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
                double bondMoved = FairPrice * Math.Exp(BondGrowthPerHour * 24 / TicksPerDay + noise * BondNoise);
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
            else if (MegaTicks != 0)
            {
                //a mega boom or crash in progress: the same log step each tick, nothing else moves the price
                int direction = Math.Sign(MegaTicks);
                moved = FairPrice * Math.Exp(direction * MegaStep);
                MegaTicks -= direction;
                reversion = 0;
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
                double streakEnd = CryptoCrashChance + CryptoMoonChance;
                bool megaCrash = !crash && !moon && roll < streakEnd + CryptoMegaChance;
                bool megaBoom = !DisableMegaBoom && !crash && !moon && !megaCrash && roll < streakEnd + 2 * CryptoMegaChance;
                PricingIdleTicks++;
                if (megaCrash || megaBoom)
                {
                    //size, then length: a log move of at least ln 10, with an exponential tail, cut off at ln 100,000
                    double size = MegaSize(random());
                    int length = CryptoMegaMinTicks + (int)(random() * (CryptoMegaMaxTicks - CryptoMegaMinTicks + 1));
                    int direction = megaCrash ? -1 : 1;
                    MegaStep = size / length;
                    moved = FairPrice * Math.Exp(direction * MegaStep);
                    MegaTicks = direction * (length - 1); //this tick was the first of the move
                    reversion = 0;
                }
                else if (crash || moon)
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
            moved = Math.Max(FloorPrice, moved);
            if (!drift.AllowReversion) reversion = 0; //during a crisis the price follows the drift, not the old base
            if (BasePrice > 0) PricingReversionSum += reversion;
            //a fixed percentage move up or down has a negative average log change, so the price would slowly decay without this
            if (BasePrice > 0 && reversion > 0)
                moved *= Math.Pow(BasePrice / moved, reversion);

            PreviousPrice = Price;
            //four decimals, so a slow crisis drift on a cheap stock isn't rounded away every tick (the quote is still shown to the cent)
            FairPrice = Math.Max(FloorPrice, Math.Round(moved, 4));
            Impact *= ImpactDecayPerTick;
            RefreshQuote();
        }

        /// <summary>The log size of a mega event for a uniform draw <paramref name="u"/> in [0, 1): at least ln 10, an exponential tail, cut off at ln 100,000.</summary>
        public static double MegaSize(double u) => Math.Min(CryptoMegaMinLog - Math.Log(1 - u), CryptoMegaMaxLog);

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
            if (IsCrypto) return PreviewCryptoTrade(shares, spread);

            double move = ImpactCoefficient * Math.Sqrt(Math.Abs(shares) / SharesOutstanding) * Math.Sign(shares);
            double newImpact = Math.Max(-MaxImpact, Math.Min(MaxImpact, Impact + move));
            double after = PriceAt(FairPrice, newImpact);
            double mid = (Price + after) / 2;
            double fill = Math.Max(MinPrice, mid * (1 + Math.Sign(shares) * spread / 2));
            return (RoundPrice(fill), newImpact);
        }

        /// <summary>
        /// A crypto trade. The market is thin and a mega boom can multiply the price, so trading impact has no 50% cap: buying follows
        /// the square-root law all the way, and selling can push the quote down at most <see cref="CryptoMaxSellImpact"/>. The fill is
        /// the average price along the impact curve (not the midpoint), so a big order pays for its own impact.
        /// </summary>
        private (double Fill, double NewImpact) PreviewCryptoTrade(int shares, double spread)
        {
            double quantity = Math.Abs((double)shares);
            double scale = ImpactCoefficient * Math.Sqrt(quantity / SharesOutstanding);
            double newImpact, averageImpact;
            if (shares >= 0)
            {
                newImpact = Impact + scale;
                averageImpact = Impact + scale * 2.0 / 3.0;
            }
            else
            {
                newImpact = Math.Max(-CryptoMaxSellImpact, Impact - scale);
                //shares that can be sold before the quote reaches its lowest; beyond that the rest sell at that lowest price
                double headroom = Impact + CryptoMaxSellImpact;
                double untilFloor = headroom <= 0 ? 0 : SharesOutstanding * Math.Pow(headroom / ImpactCoefficient, 2);
                if (quantity <= untilFloor || quantity == 0)
                {
                    averageImpact = Impact - scale * 2.0 / 3.0;
                }
                else
                {
                    double areaAboveFloor = Impact * untilFloor - ImpactCoefficient * (2.0 / 3.0) * Math.Pow(untilFloor, 1.5) / Math.Sqrt(SharesOutstanding);
                    averageImpact = (areaAboveFloor - (quantity - untilFloor) * CryptoMaxSellImpact) / quantity;
                }
            }

            double mid = Math.Max(FloorPrice, FairPrice * (1 + averageImpact));
            double fill = Math.Max(FloorPrice, mid * (1 + Math.Sign(shares) * spread / 2));
            return (RoundPrice(fill), newImpact);
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
        /// The largest number of shares whose cost fits in <paramref name="cash"/>, and no more than <paramref name="maxShares"/>.
        /// Cost rises with size because of impact, so this searches rather than dividing. Buying pushes the fill up, so the
        /// price is an upper bound.
        /// </summary>
        public int MaxAffordableShares(double cash) => MaxAffordableShares(cash, Spread);

        public int MaxAffordableShares(double cash, double spread, int maxShares = int.MaxValue)
        {
            if (cash < Price || maxShares < 1) return 0;
            //long, so the midpoint can't overflow: with billions of cash the top of the range is int.MaxValue,
            //and an int search there wrapped negative and never finished, freezing the bot
            long low = 0;
            long high = (long)Math.Min(maxShares, Math.Floor(cash / Price));
            while (low < high)
            {
                long mid = low + (high - low + 1) / 2;
                double cost = Math.Round(mid * PreviewTrade((int)mid, spread).Fill, 2);
                if (cost <= cash) low = mid;
                else high = mid - 1;
            }
            return (int)low;
        }

        /// <summary>
        /// The largest number of shares that can be shorted with <paramref name="cash"/>: the margin (the sale value) plus the
        /// sale tax must fit. Selling pushes the fill down, so this searches rather than dividing.
        /// </summary>
        public int MaxShortableShares(double cash, int maxShares = int.MaxValue)
        {
            if (cash <= 0 || maxShares < 1) return 0;
            //long for the same reason as MaxAffordableShares
            long low = 0;
            long high = (long)Math.Min(maxShares, Math.Floor(cash / FloorPrice));
            while (low < high)
            {
                long mid = low + (high - low + 1) / 2;
                double gross = Math.Round(mid * PreviewTrade(-(int)mid).Fill, 2);
                if (gross + TaxOn(gross) <= cash) low = mid;
                else high = mid - 1;
            }
            return (int)low;
        }

        /// <summary>Shares in one option contract.</summary>
        public const int OptionContractSize = 100;
        /// <summary>Extra the house charges on the fair premium.</summary>
        public const double OptionHouseEdge = 1.10;
        /// <summary>Fee on the premium, paid into the jackpot.</summary>
        public const double OptionFeeRate = 0.02;

        /// <summary>Premium plus the jackpot fee for a number of contracts, to the cent. Same rounding as a purchase.</summary>
        public static double OptionCost(double premiumPerShare, int contracts)
        {
            double premium = Math.Round(premiumPerShare * OptionContractSize * contracts, 2);
            return premium + Math.Round(premium * OptionFeeRate, 2);
        }

        /// <summary>The most contracts that premium plus fee can buy with this cash. Zero when not even one is affordable.</summary>
        public static int MaxAffordableContracts(double cash, double premiumPerShare)
        {
            if (cash <= 0 || premiumPerShare <= 0) return 0;
            const int limit = 1_000_000_000;
            int contracts = (int)Math.Min(limit, Math.Floor(cash / OptionCost(premiumPerShare, 1)));
            while (contracts > 0 && OptionCost(premiumPerShare, contracts) > cash) contracts--;
            while (contracts < limit && OptionCost(premiumPerShare, contracts + 1) <= cash) contracts++;
            return contracts;
        }
        public const double MinOptionPremiumPerShare = 0.01;

        /// <summary>
        /// Premium per share of an at-the-money call or put lasting <paramref name="duration"/>: what it pays on average when the real
        /// price process is run forward from this stock's current state (<see cref="OptionPricer"/>), plus the house edge. Calls
        /// are dear and puts cheap when the stock is well below its base, and the reverse when it is above. <paramref name="drift"/>
        /// is the market-wide crisis drift now running, so puts aren't cheap in the middle of a crash.
        /// </summary>
        public double OptionPremiumPerShare(TimeSpan duration, bool isCall = true, MarketDrift? drift = null)
        {
            int ticks = (int)Math.Max(1, Math.Round(duration.TotalMinutes / 5));
            double fair = OptionPricer.FairValuePerShare(this, isCall, ticks, drift ?? MarketDrift.None);
            return Math.Max(MinOptionPremiumPerShare, Math.Round(fair * OptionHouseEdge, 2));
        }

        /// <summary>The quote for a fair price and impact, never below <paramref name="floor"/> (a dollar unless it's a crypto).</summary>
        public static double PriceAt(double fairPrice, double impact, double floor = MinPrice) =>
            Math.Max(floor, RoundPrice(fairPrice * (1 + impact)));

        public void RefreshQuote() => Price = PriceAt(FairPrice, Impact, FloorPrice);

        public void RecordHistory()
        {
            History.Add(Price);
            if (History.Count > MaxHistoryPoints)
                History.RemoveRange(0, History.Count - MaxHistoryPoints);
        }
    }
}
