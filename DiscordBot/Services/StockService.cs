using Common.Services;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Services
{
    public class StockService
    {
        private const string _fileName = "stockmarket.json";
        private static readonly long _tickLengthTicks = TimeSpan.FromMinutes(5).Ticks;
        private static readonly Random _random = new Random();

        private readonly LocalFileService _fileService;
        private readonly CoinService _coinService;
        private readonly object _lock = new object();
        private StockMarket _market;

        public StockService(LocalFileService fileService, CoinService coinService)
        {
            _fileService = fileService;
            _coinService = coinService;
            _market = _fileService.GetContent<StockMarket>(_fileName).Result;
            if (_market == null || _market.Stocks.Count == 0)
            {
                _market = StockMarket.CreateDefault();
                Save();
            }
            else
            {
                FillMissingBasePrices();
            }
        }

        public StockMarket GetMarket() => _market;

        /// <summary>Resets the market to its default prices, with no holdings or history.</summary>
        public Task ClearAll()
        {
            lock (_lock)
            {
                _market = StockMarket.CreateDefault();
                Save();
            }
            return Task.CompletedTask;
        }

        public double GetPortfolioValue(ulong userId)
        {
            lock (_lock) return _market.GetPortfolioValue(userId);
        }

        /// <summary>
        /// Updates all prices if we've moved into a new 5 minute period since the last update. Safe to call every minute.
        /// </summary>
        public Task TickIfDue()
        {
            long slot = DateTimeOffset.UtcNow.UtcTicks / _tickLengthTicks;
            lock (_lock)
            {
                if (slot == _market.LastTickSlot) return Task.CompletedTask;

                //one tick only, even if the bot was down for several periods
                foreach (var stock in _market.Stocks)
                {
                    stock.ApplyTick(_random.NextDouble);
                    stock.RecordHistory();
                }
                _market.LastTickSlot = slot;
                Save();
            }
            return Task.CompletedTask;
        }

        public (Stock Stock, int Shares, double Cost) Buy(CoinAccount account, string symbol, string amount)
        {
            lock (_lock)
            {
                var stock = _market.Find(symbol) ?? throw new BadInputException($"There's no stock called {symbol.ToUpper()}.");
                int shares = amount.Equals("max", StringComparison.OrdinalIgnoreCase)
                    ? (int)Math.Floor(account.NetWorth / stock.Price)
                    : ParseShares(amount);

                if (shares < 1)
                    throw new BadInputException($"You can't afford even one share of {stock.Symbol} (${stock.Price:N2}).");

                double cost = Math.Round(shares * stock.Price, 2);
                if (cost > account.NetWorth)
                    throw new BadInputException($"{shares} shares of {stock.Symbol} cost ${cost:N2} but you only have ${account.NetWorth:N2}.");

                account.NetWorth -= cost;
                _market.RecordPurchase(account.UserId, stock.Symbol, shares, stock.Price);
                Save();
                return PersistAccount(account, (stock, shares, cost));
            }
        }

        public (Stock Stock, int Shares, double Proceeds) Sell(CoinAccount account, string symbol, string amount)
        {
            lock (_lock)
            {
                var stock = _market.Find(symbol) ?? throw new BadInputException($"There's no stock called {symbol.ToUpper()}.");
                int owned = _market.GetShares(account.UserId, stock.Symbol);
                int shares = amount.Equals("all", StringComparison.OrdinalIgnoreCase) ? owned : ParseShares(amount);

                if (shares < 1 || owned < 1)
                    throw new BadInputException($"You don't own any {stock.Symbol}.");
                if (shares > owned)
                    throw new BadInputException($"You only own {owned} shares of {stock.Symbol}.");

                double proceeds = Math.Round(shares * stock.Price, 2);
                account.NetWorth += proceeds;
                _market.AddShares(account.UserId, stock.Symbol, -shares);
                Save();
                return PersistAccount(account, (stock, shares, proceeds));
            }
        }

        /// <summary>Markets saved before base prices existed get the default base price for their symbol.</summary>
        private void FillMissingBasePrices()
        {
            var defaults = StockMarket.CreateDefault();
            foreach (var stock in _market.Stocks.Where(s => s.BasePrice <= 0))
                stock.BasePrice = defaults.Find(stock.Symbol)?.BasePrice ?? stock.Price;
        }

        private static int ParseShares(string amount)
        {
            if (!int.TryParse(amount, out int shares) || shares < 1)
                throw new BadSyntaxException();
            return shares;
        }

        private T PersistAccount<T>(CoinAccount account, T result)
        {
            _coinService.Update(account.UserId, account.NetWorth, account.Name).GetAwaiter().GetResult();
            return result;
        }

        private void Save()
        {
            _fileService.UpdateContent(_fileName, JsonConvert.SerializeObject(_market)).GetAwaiter().GetResult();
        }
    }
}
