using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    /// <summary>Shares borrowed and sold. The sale value is locked as margin; the position gains when the price falls.</summary>
    public class ShortPosition
    {
        public int Shares { get; set; }
        /// <summary>Average price the shares were sold at.</summary>
        public double EntryPrice { get; set; }
        /// <summary>Cash locked when opening. Returned (plus profit or minus loss) on cover.</summary>
        public double Margin { get; set; }

        /// <summary>What the position is worth at a price: the margin plus the gain. Never below zero.</summary>
        public double EquityAt(double price) => Math.Max(0, Margin + (EntryPrice - price) * Shares);

        /// <summary>At twice the entry price the loss is the whole margin, so the position is closed out.</summary>
        public bool IsLiquidatedAt(double price) => price >= EntryPrice * 2;
    }

    /// <summary>A cash-settled at-the-money option. One contract covers <see cref="Stock.OptionContractSize"/> shares.</summary>
    public class OptionContract
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 6);
        public ulong UserId { get; set; }
        public string Symbol { get; set; }
        public bool IsCall { get; set; }
        public double Strike { get; set; }
        public int Contracts { get; set; }
        /// <summary>Total premium paid, before the jackpot fee.</summary>
        public double Premium { get; set; }
        public long ExpiresAtUtcTicks { get; set; }

        public DateTime ExpiresAt => new DateTime(ExpiresAtUtcTicks, DateTimeKind.Utc);

        /// <summary>What the option pays at a fair price: how far in the money, per share.</summary>
        public double IntrinsicPerShare(double fairPrice) =>
            Math.Max(0, IsCall ? fairPrice - Strike : Strike - fairPrice);

        public double PayoutAt(double fairPrice) =>
            Math.Round(IntrinsicPerShare(fairPrice) * Stock.OptionContractSize * Contracts, 2);
    }

    public class StockMarket
    {
        /// <summary>How many settled option results are kept per user.</summary>
        public const int RecentSettledKept = 5;

        public List<Stock> Stocks { get; set; } = new List<Stock>();
        /// <summary>userId -> (symbol -> whole shares owned)</summary>
        public Dictionary<ulong, Dictionary<string, int>> Portfolios { get; set; } = new Dictionary<ulong, Dictionary<string, int>>();
        /// <summary>userId -> (symbol -> average price paid per share currently held)</summary>
        public Dictionary<ulong, Dictionary<string, double>> CostBasis { get; set; } = new Dictionary<ulong, Dictionary<string, double>>();
        /// <summary>userId -> (symbol -> short position)</summary>
        public Dictionary<ulong, Dictionary<string, ShortPosition>> Shorts { get; set; } = new Dictionary<ulong, Dictionary<string, ShortPosition>>();
        public List<OptionContract> Options { get; set; } = new List<OptionContract>();
        /// <summary>userId -> text of their last few settled options, newest first.</summary>
        public Dictionary<ulong, List<string>> RecentSettled { get; set; } = new Dictionary<ulong, List<string>>();
        /// <summary>Number of 5 minute periods since the unix epoch that prices were last updated for.</summary>
        public long LastTickSlot { get; set; }
        /// <summary>
        /// Layout version. 0 is the old layout, where tiers 1 and 2 also had an ordinary stock. Markets are created at
        /// <see cref="CurrentSchemaVersion"/>, and older ones are migrated by <see cref="StockService"/>.
        /// </summary>
        public int SchemaVersion { get; set; }
        public const int CurrentSchemaVersion = 2;

        public Stock Find(string symbol)
        {
            return Stocks.FirstOrDefault(s => string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        }

        public int GetShares(ulong userId, string symbol)
        {
            return Portfolios.TryGetValue(userId, out var holdings) && holdings.TryGetValue(symbol, out int shares) ? shares : 0;
        }

        public double GetAverageCost(ulong userId, string symbol)
        {
            return CostBasis.TryGetValue(userId, out var basis) && basis.TryGetValue(symbol, out double cost) ? cost : 0;
        }

        /// <summary>Holdings for a user, or an empty dictionary.</summary>
        public IReadOnlyDictionary<string, int> GetHoldings(ulong userId)
        {
            return Portfolios.TryGetValue(userId, out var holdings) ? holdings : new Dictionary<string, int>();
        }

        /// <summary>Adds bought shares and blends their price into the average cost of the holding.</summary>
        public void RecordPurchase(ulong userId, string symbol, int shares, double price)
        {
            int owned = GetShares(userId, symbol);
            double averageCost = GetAverageCost(userId, symbol);
            double newAverage = (owned * averageCost + shares * price) / (owned + shares);

            AddShares(userId, symbol, shares);
            if (!CostBasis.TryGetValue(userId, out var basis))
                CostBasis[userId] = basis = new Dictionary<string, double>();
            basis[symbol] = newAverage;
        }

        public void AddShares(ulong userId, string symbol, int shares)
        {
            if (!Portfolios.TryGetValue(userId, out var holdings))
                Portfolios[userId] = holdings = new Dictionary<string, int>();

            int newTotal = (holdings.TryGetValue(symbol, out int current) ? current : 0) + shares;
            if (newTotal < 0) throw new InvalidOperationException("Can't have a negative number of shares.");

            if (newTotal == 0)
            {
                holdings.Remove(symbol);
                if (CostBasis.TryGetValue(userId, out var basis)) basis.Remove(symbol);
            }
            else holdings[symbol] = newTotal;
        }

        public ShortPosition GetShort(ulong userId, string symbol) =>
            Shorts.TryGetValue(userId, out var positions) && positions.TryGetValue(symbol, out var position) ? position : null;

        public IReadOnlyDictionary<string, ShortPosition> GetShorts(ulong userId) =>
            Shorts.TryGetValue(userId, out var positions) ? positions : new Dictionary<string, ShortPosition>();

        /// <summary>Adds shorted shares, averaging the entry price and summing the margin.</summary>
        public void AddShort(ulong userId, string symbol, int shares, double fill, double margin)
        {
            if (!Shorts.TryGetValue(userId, out var positions))
                Shorts[userId] = positions = new Dictionary<string, ShortPosition>();

            if (!positions.TryGetValue(symbol, out var position))
            {
                positions[symbol] = new ShortPosition { Shares = shares, EntryPrice = fill, Margin = margin };
                return;
            }
            position.EntryPrice = (position.Shares * position.EntryPrice + shares * fill) / (position.Shares + shares);
            position.Shares += shares;
            position.Margin += margin;
        }

        /// <summary>
        /// Closes <paramref name="shares"/> of a short and returns the margin released with it. Removes the position when none is left.
        /// </summary>
        public double ReduceShort(ulong userId, string symbol, int shares)
        {
            var position = GetShort(userId, symbol) ?? throw new InvalidOperationException("No short position.");
            if (shares > position.Shares) throw new InvalidOperationException("Can't cover more than is shorted.");

            double released = shares == position.Shares ? position.Margin : Math.Round(position.Margin * shares / position.Shares, 2);
            position.Shares -= shares;
            position.Margin -= released;
            if (position.Shares == 0)
            {
                Shorts[userId].Remove(symbol);
                if (Shorts[userId].Count == 0) Shorts.Remove(userId);
            }
            return released;
        }

        public IEnumerable<OptionContract> GetOptions(ulong userId) => Options.Where(o => o.UserId == userId);

        /// <summary>True if the user holds any shares, short or open option.</summary>
        public bool HasPositions(ulong userId) =>
            GetHoldings(userId).Count > 0 || GetShorts(userId).Count > 0 || GetOptions(userId).Any();

        public void AddSettledResult(ulong userId, string text)
        {
            if (!RecentSettled.TryGetValue(userId, out var results))
                RecentSettled[userId] = results = new List<string>();
            results.Insert(0, text);
            if (results.Count > RecentSettledKept)
                results.RemoveRange(RecentSettledKept, results.Count - RecentSettledKept);
        }

        /// <summary>Shares at their quote, plus short positions at their equity and open options at what they would pay now.</summary>
        public double GetPortfolioValue(ulong userId)
        {
            double value = 0;
            if (Portfolios.TryGetValue(userId, out var holdings))
                value += holdings.Sum(h => (Find(h.Key)?.Price ?? 0) * h.Value);

            foreach (var position in GetShorts(userId))
                value += position.Value.EquityAt(Find(position.Key)?.Price ?? position.Value.EntryPrice);

            foreach (var option in GetOptions(userId))
                value += option.PayoutAt(Find(option.Symbol)?.FairPrice ?? option.Strike);

            return value;
        }

        /// <summary>Current value minus average buy price, over holdings with a known cost. Also returns what those holdings cost.</summary>
        public (double Gain, double Cost) GetUnrealisedGain(ulong userId)
        {
            double gain = 0, cost = 0;
            foreach (var holding in GetHoldings(userId))
            {
                double averageCost = GetAverageCost(userId, holding.Key);
                if (averageCost <= 0) continue;

                double price = Find(holding.Key)?.Price ?? 0;
                gain += (price - averageCost) * holding.Value;
                cost += averageCost * holding.Value;
            }
            return (gain, cost);
        }

        /// <summary>
        /// Every stock a season can pick from: (symbol, name, tier, starting price). One is chosen per tier each season.
        /// </summary>
        public static readonly IReadOnlyList<(string Symbol, string Name, int Tier, double Price)> Pool = new[]
        {
            //tiers 1 and 2 hold only safe havens (see SafeHavens), so the pool starts at tier 3
            //tier 3 - moderate
            ("SCORT", "Scort Industries", 3, 50.0),
            ("JCB", "JCB Group", 3, 80.0),
            ("DAVD", "Davd & Sons", 3, 65.0),
            //tier 4 - volatile
            ("SCORCH", "Scorch Rockets", 4, 20.0),
            ("BALLS", "Balls Inc", 4, 35.0),
            ("SAMOA", "Samoa Shipping", 4, 28.0),
            ("PUBE", "Pube Haircare", 4, 15.0),
            //tier 5 - degenerate
            ("GLGY", "GLGY Capital", 5, 10.0),
            ("ALX", "ALX Squared", 5, 5.0),
            ("COCK", "Cock Capital", 5, 8.0),
            //tier 6 - crypto
            ("BTCX", "Bitcoinx", 6, 50.0),
            ("MOONX", "MoonCoin", 6, 20.0),
            ("HODL", "HODL Token", 6, 12.0),
        };

        /// <summary>
        /// Safe havens, always listed (not drawn from the pool). Gold rallies in a crash and bonds pay a small steady yield.
        /// </summary>
        public static readonly IReadOnlyList<(string Symbol, string Name, int Tier, double Price, StockRole Role)> SafeHavens = new[]
        {
            ("GOLD", "Gold Bullion", 2, 150.0, StockRole.SafeHaven),
            ("BOND", "Treasury Bonds", 1, 100.0, StockRole.Bond),
        };

        /// <summary>A new market with one randomly chosen stock from the pool in each tier, plus the safe havens. Pass a seeded random for a repeatable pick.</summary>
        public static StockMarket CreateDefault(Random random = null)
        {
            random ??= new Random();
            var stocks = Pool
                .GroupBy(p => p.Tier)
                .OrderBy(g => g.Key)
                .Select(g => g.ElementAt(random.Next(g.Count())))
                .Select(p => NewStock(p.Symbol, p.Name, p.Tier, p.Price, StockRole.Normal))
                .ToList();

            var market = new StockMarket { Stocks = stocks, SchemaVersion = CurrentSchemaVersion };
            market.EnsureSafeHavens();
            return market;
        }

        /// <summary>
        /// Takes an ordinary stock off the market. Every holder is paid out at its current value, with no sell tax:
        /// shares at the quote, shorts at their equity (margin plus or minus the gain), and open options at their fair-price payout.
        /// Returns what each user is owed in cash. Caller credits it and saves.
        /// </summary>
        public Dictionary<ulong, double> Retire(Stock stock)
        {
            var owed = new Dictionary<ulong, double>();
            void Owe(ulong userId, double amount) =>
                owed[userId] = (owed.TryGetValue(userId, out double so_far) ? so_far : 0) + amount;

            string symbol = stock.Symbol;
            foreach (ulong userId in Portfolios.Keys.ToList())
            {
                int shares = GetShares(userId, symbol);
                if (shares <= 0) continue;
                AddShares(userId, symbol, -shares);
                Owe(userId, Math.Round(shares * stock.Price, 2));
                AddSettledResult(userId, $"{symbol} was taken off the market. Your {shares} shares were paid out at ${stock.Price:N2}.");
            }

            foreach (ulong userId in Shorts.Keys.ToList())
            {
                var position = GetShort(userId, symbol);
                if (position == null) continue;
                double equity = Math.Round(position.EquityAt(stock.Price), 2);
                ReduceShort(userId, symbol, position.Shares);
                Owe(userId, equity);
                AddSettledResult(userId, $"Short {symbol} closed when it was taken off the market. Returned ${equity:N2}.");
            }

            foreach (var option in Options.Where(o => o.Symbol == symbol).ToList())
            {
                double payout = option.PayoutAt(stock.FairPrice);
                Options.Remove(option);
                Owe(option.UserId, payout);
                AddSettledResult(option.UserId, $"{option.Contracts}x {symbol} {(option.IsCall ? "call" : "put")} settled when it was taken off the market: paid ${payout:N2}.");
            }

            Stocks.Remove(stock);
            return owed;
        }

        /// <summary>Adds any safe haven the market doesn't have yet, at its launch price. Older saved markets get them this way.</summary>
        public void EnsureSafeHavens()
        {
            foreach (var haven in SafeHavens)
            {
                if (Find(haven.Symbol) == null)
                    Stocks.Add(NewStock(haven.Symbol, haven.Name, haven.Tier, haven.Price, haven.Role));
            }
        }

        private static Stock NewStock(string symbol, string name, int tier, double price, StockRole role) => new Stock
        {
            Symbol = symbol,
            Name = name,
            RiskTier = tier,
            Role = role,
            Price = price,
            PreviousPrice = price,
            BasePrice = price,
            FairPrice = price,
            SharesOutstanding = tier == Stock.CryptoTier ? Stock.CryptoSharesOutstanding : Stock.DefaultSharesOutstanding,
            History = new List<double> { price }
        };
    }
}
