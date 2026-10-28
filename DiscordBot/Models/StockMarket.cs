using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    public class StockMarket
    {
        public const string ScortSymbol = "SCORT";

        public List<Stock> Stocks { get; set; } = new List<Stock>();
        /// <summary>userId -> (symbol -> whole shares owned)</summary>
        public Dictionary<ulong, Dictionary<string, int>> Portfolios { get; set; } = new Dictionary<ulong, Dictionary<string, int>>();
        /// <summary>Number of 5 minute periods since the unix epoch that prices were last updated for.</summary>
        public long LastTickSlot { get; set; }

        public Stock Find(string symbol)
        {
            return Stocks.FirstOrDefault(s => string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        }

        public int GetShares(ulong userId, string symbol)
        {
            return Portfolios.TryGetValue(userId, out var holdings) && holdings.TryGetValue(symbol, out int shares) ? shares : 0;
        }

        public void AddShares(ulong userId, string symbol, int shares)
        {
            if (!Portfolios.TryGetValue(userId, out var holdings))
                Portfolios[userId] = holdings = new Dictionary<string, int>();

            int newTotal = (holdings.TryGetValue(symbol, out int current) ? current : 0) + shares;
            if (newTotal < 0) throw new InvalidOperationException("Can't have a negative number of shares.");

            if (newTotal == 0) holdings.Remove(symbol);
            else holdings[symbol] = newTotal;
        }

        public double GetPortfolioValue(ulong userId)
        {
            if (!Portfolios.TryGetValue(userId, out var holdings)) return 0;
            return holdings.Sum(h => (Find(h.Key)?.Price ?? 0) * h.Value);
        }

        public static StockMarket CreateDefault()
        {
            Stock S(string symbol, string name, int tier, double price) =>
                new Stock { Symbol = symbol, Name = name, RiskTier = tier, Price = price, PreviousPrice = price };

            return new StockMarket
            {
                Stocks = new List<Stock>
                {
                    //tier 1 - very stable
                    S("BNDX", "Boring Bond Index", 1, 100),
                    S("UTIL", "Utility Co", 1, 60),
                    //tier 2 - stable
                    S("GROC", "Grocery Giant", 2, 45),
                    S("WANG", "Wang Holdings", 2, 25),
                    //tier 3 - moderate
                    S(ScortSymbol, "Scort Industries", 3, 50),
                    S("JCB", "JCB Group", 3, 80),
                    //tier 4 - volatile
                    S("SCORCH", "Scorch Rockets", 4, 20),
                    S("BALLS", "Balls Inc", 4, 35),
                    //tier 5 - degenerate
                    S("GLGY", "GLGY Capital", 5, 10),
                    S("ALX", "ALX Squared", 5, 5)
                }
            };
        }
    }
}
