using Common.Services;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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
                FillMissingFields();
                _market.EnsureSafeHavens(); //gold and bonds were added after some markets were saved
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

        /// <summary>
        /// Copies of every stock in <c>.stocks</c> order, with the tick they show, taken under the lock so drawing charts
        /// never races a tick or a trade.
        /// </summary>
        public (List<Stock> Stocks, long Slot) SnapshotForCharts()
        {
            lock (_lock)
            {
                var copies = StockTickerCard.Ordered(_market.Stocks)
                    .Select(s => JsonConvert.DeserializeObject<Stock>(JsonConvert.SerializeObject(s)))
                    .ToList();
                return (copies, _market.LastTickSlot);
            }
        }

        public double GetPortfolioValue(ulong userId)
        {
            lock (_lock) return _market.GetPortfolioValue(userId);
        }

        /// <summary>
        /// Updates all prices if we've moved into a new 5 minute period since the last update, closes shorts that have lost
        /// their margin, and settles options that have expired. Safe to call every minute.
        /// </summary>
        public async Task TickIfDue()
        {
            long now = DateTimeOffset.UtcNow.UtcTicks;
            long slot = now / _tickLengthTicks;
            bool coinChanged = false;
            lock (_lock)
            {
                bool changed = false;
                var data = _coinService.GetAll();

                if (_market.SchemaVersion < StockMarket.CurrentSchemaVersion)
                {
                    //tiers 1 and 2 are safe havens only now: take their ordinary stocks off, paying out every holder
                    foreach (var stock in _market.Stocks.Where(s => s.Role == StockRole.Normal && s.RiskTier < 3).ToList())
                    {
                        foreach (var (userId, amount) in _market.Retire(stock))
                        {
                            var account = data.Accounts.FirstOrDefault(a => a.UserId == userId);
                            if (account != null) account.NetWorth += data.PayDebtFrom(account, amount);
                        }
                    }
                    _market.SchemaVersion = StockMarket.CurrentSchemaVersion;
                    changed = true;
                    coinChanged = true;
                }

                if (slot != _market.LastTickSlot)
                {
                    if (data.CrisisResetPending)
                    {
                        //the crash has ended: stocks keep their new level instead of drifting back to the old one
                        MarketCycle.ResetBasePrices(_market.Stocks);
                        data.CrisisResetPending = false;
                        coinChanged = true;
                    }

                    //one tick only, even if the bot was down for several periods
                    foreach (var stock in _market.Stocks)
                    {
                        stock.ApplyTick(_random.NextDouble, MarketCycle.DriftFor(stock, data.MarketPhase));
                        stock.RecordHistory();
                    }
                    _market.LastTickSlot = slot;
                    changed = true;
                    LiquidateBrokenShorts();
                }

                changed |= SettleExpiredOptions(now);
                if (changed) Save();
            }
            if (coinChanged) await _coinService.Update();
        }

        public bool HasPositions(ulong userId)
        {
            lock (_lock) return _market.HasPositions(userId);
        }

        public (Stock Stock, int Shares, double Cost) Buy(CoinAccount account, string symbol, string amount)
        {
            lock (_lock)
            {
                var stock = _market.Find(symbol) ?? throw new BadInputException($"There's no stock called {symbol.ToUpper()}.");
                double spread = SpreadFor(account);
                int shares = amount.Equals("max", StringComparison.OrdinalIgnoreCase)
                    ? stock.MaxAffordableShares(account.NetWorth, spread)
                    : ParseShares(amount);

                if (shares < 1)
                    throw new BadInputException($"You can't afford even one share of {stock.Symbol} (${stock.Price:N2}).");

                double fill = stock.PreviewTrade(shares, spread).Fill;
                double cost = Math.Round(shares * fill, 2);
                if (cost > account.NetWorth)
                    throw new BadInputException($"{shares} shares of {stock.Symbol} cost ${cost:N2} but you only have ${account.NetWorth:N2}.");

                stock.ApplyTrade(shares);
                account.NetWorth -= cost;
                _market.RecordPurchase(account.UserId, stock.Symbol, shares, fill);
                Save();
                return PersistAccount(account, (stock, shares, cost));
            }
        }

        public (Stock Stock, int Shares, double Gross, double Tax) Sell(CoinAccount account, string symbol, string amount)
        {
            lock (_lock)
            {
                var stock = _market.Find(symbol) ?? throw new BadInputException($"There's no stock called {symbol.ToUpper()}.");
                int owned = _market.GetShares(account.UserId, stock.Symbol);
                int shares = IsAllOrMax(amount) ? owned : ParseShares(amount);

                if (shares < 1 || owned < 1)
                    throw new BadInputException($"You don't own any {stock.Symbol}.");
                if (shares > owned)
                    throw new BadInputException($"You only own {owned} shares of {stock.Symbol}.");

                double fill = stock.PreviewTrade(-shares, SpreadFor(account)).Fill;
                double gross = Math.Round(shares * fill, 2);
                double tax = account.IsActive(PowerUpIds.TaxHaven, DateTime.UtcNow) ? 0 : Stock.TaxOn(gross);
                _coinService.AddToJackpot(tax);
                stock.ApplyTrade(-shares);
                account.NetWorth += _coinService.GetAll().PayDebtFrom(account, gross - tax); //sale money pays any debt first, into the jackpot
                _market.AddShares(account.UserId, stock.Symbol, -shares);
                Save();
                return PersistAccount(account, (stock, shares, gross, tax));
            }
        }

        /// <summary>The spread a player pays on buys and sells: none while they have Insider Trader running.</summary>
        private static double SpreadFor(CoinAccount account) =>
            account.IsActive(PowerUpIds.InsiderTrader, DateTime.UtcNow) ? 0 : Stock.Spread;

        public static readonly TimeSpan MinOptionDuration = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan MaxOptionDuration = TimeSpan.FromDays(3);

        /// <summary>Sells borrowed shares. The sale value is locked from cash as margin, and the sale tax goes to the jackpot.</summary>
        public (Stock Stock, int Shares, double Margin, double Tax) Short(CoinAccount account, string symbol, string amount)
        {
            lock (_lock)
            {
                var stock = _market.Find(symbol) ?? throw new BadInputException($"There's no stock called {symbol.ToUpper()}.");
                int shares = amount.Equals("max", StringComparison.OrdinalIgnoreCase)
                    ? stock.MaxShortableShares(account.NetWorth)
                    : ParseShares(amount);

                if (shares < 1)
                    throw new BadInputException($"You can't afford the margin for even one share of {stock.Symbol} (${stock.Price:N2}).");

                double fill = stock.PreviewTrade(-shares).Fill;
                double margin = Math.Round(shares * fill, 2);
                double tax = Stock.TaxOn(margin);
                if (margin + tax > account.NetWorth)
                    throw new BadInputException($"Shorting {shares} shares of {stock.Symbol} needs ${margin + tax:N2} (margin plus tax) but you only have ${account.NetWorth:N2}.");

                _coinService.AddToJackpot(tax);
                stock.ApplyTrade(-shares);
                account.NetWorth -= margin + tax;
                _market.AddShort(account.UserId, stock.Symbol, shares, fill, margin);
                Save();
                return PersistAccount(account, (stock, shares, margin, tax));
            }
        }

        /// <summary>Buys back shorted shares. The margin comes back plus the gain, or minus the loss, and never below zero.</summary>
        public (Stock Stock, int Shares, double Returned, double Profit) Cover(CoinAccount account, string symbol, string amount)
        {
            lock (_lock)
            {
                var stock = _market.Find(symbol) ?? throw new BadInputException($"There's no stock called {symbol.ToUpper()}.");
                var position = _market.GetShort(account.UserId, stock.Symbol) ?? throw new BadInputException($"You aren't short any {stock.Symbol}.");
                int shares = IsAllOrMax(amount) ? position.Shares : ParseShares(amount);
                if (shares > position.Shares)
                    throw new BadInputException($"You're only short {position.Shares} shares of {stock.Symbol}.");

                double fill = stock.PreviewTrade(shares).Fill;
                double profit = Math.Round((position.EntryPrice - fill) * shares, 2);
                double released = _market.ReduceShort(account.UserId, stock.Symbol, shares);
                double returned = Math.Max(0, Math.Round(released + profit, 2));

                stock.ApplyTrade(shares);
                account.NetWorth += returned;
                Save();
                return PersistAccount(account, (stock, shares, returned, returned - released));
            }
        }

        /// <summary>The most option contracts the account can pay for (premium plus fee) at this duration.</summary>
        public int MaxAffordableContracts(CoinAccount account, string symbol, TimeSpan duration)
        {
            lock (_lock)
            {
                var stock = _market.Find(symbol) ?? throw new BadInputException($"There's no stock called {symbol.ToUpper()}.");
                if (duration < MinOptionDuration || duration > MaxOptionDuration)
                    throw new BadInputException("Options last between 15m and 3d.");

                double perShare = stock.OptionPremiumPerShare(duration);
                int contracts = Stock.MaxAffordableContracts(account.NetWorth, perShare);
                if (contracts < 1)
                    throw new BadInputException($"One contract of {stock.Symbol} costs ${Stock.OptionCost(perShare, 1):N2} (premium plus fee) and you only have ${account.NetWorth:N2}.");
                return contracts;
            }
        }

        /// <summary>Whether a sell, cover or option amount means everything: "all" or "max".</summary>
        private static bool IsAllOrMax(string amount) =>
            amount.Equals("all", StringComparison.OrdinalIgnoreCase) || amount.Equals("max", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Buys at-the-money options that expire after <paramref name="duration"/>. The strike is the fair price (not the quote,
        /// so trading can't be used to set it), and the premium plus a jackpot fee is paid from cash.
        /// </summary>
        public (OptionContract Option, double Premium, double Fee) BuyOption(CoinAccount account, string symbol, bool isCall, int contracts, TimeSpan duration)
        {
            lock (_lock)
            {
                var stock = _market.Find(symbol) ?? throw new BadInputException($"There's no stock called {symbol.ToUpper()}.");
                if (contracts < 1) throw new BadSyntaxException();
                if (duration < MinOptionDuration || duration > MaxOptionDuration)
                    throw new BadInputException("Options last between 15m and 3d.");

                double premium = Math.Round(stock.OptionPremiumPerShare(duration) * Stock.OptionContractSize * contracts, 2);
                double fee = Math.Round(premium * Stock.OptionFeeRate, 2);
                if (premium + fee > account.NetWorth)
                    throw new BadInputException($"That option costs ${premium + fee:N2} (premium plus fee) but you only have ${account.NetWorth:N2}.");

                var option = new OptionContract
                {
                    UserId = account.UserId,
                    Symbol = stock.Symbol,
                    IsCall = isCall,
                    Strike = stock.FairPrice,
                    Contracts = contracts,
                    Premium = premium,
                    ExpiresAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks + duration.Ticks
                };
                _market.Options.Add(option);
                _coinService.AddToJackpot(fee);
                account.NetWorth -= premium + fee;
                Save();
                return PersistAccount(account, (option, premium, fee));
            }
        }

        /// <summary>Closes shorts whose price has doubled, since their loss is the whole margin. Caller holds the lock.</summary>
        private void LiquidateBrokenShorts()
        {
            foreach (var user in _market.Shorts.Keys.ToList())
                foreach (var held in _market.GetShorts(user).ToList())
                {
                    var stock = _market.Find(held.Key);
                    if (stock == null || !held.Value.IsLiquidatedAt(stock.Price)) continue;

                    _market.ReduceShort(user, held.Key, held.Value.Shares);
                    _market.AddSettledResult(user, $"Short {held.Key} liquidated at ${stock.Price:N2}, margin lost.");
                }
        }

        /// <summary>Pays and removes expired options, settled on the fair price. Returns whether any expired. Caller holds the lock.</summary>
        private bool SettleExpiredOptions(long nowTicks)
        {
            var expired = _market.Options.Where(o => o.ExpiresAtUtcTicks <= nowTicks).ToList();
            foreach (var option in expired)
            {
                double fair = _market.Find(option.Symbol)?.FairPrice ?? option.Strike;
                double payout = option.PayoutAt(fair);
                _market.Options.Remove(option);

                var account = _coinService.GetAll().Accounts.FirstOrDefault(a => a.UserId == option.UserId);
                if (account != null && payout > 0)
                {
                    account.NetWorth += payout;
                    _coinService.Update(account.UserId, account.NetWorth, account.Name).GetAwaiter().GetResult();
                }
                string kind = option.IsCall ? "call" : "put";
                _market.AddSettledResult(option.UserId,
                    $"{option.Contracts}x {option.Symbol} {kind} @ ${option.Strike:N2} expired at ${fair:N2}: paid ${payout:N2} for ${option.Premium:N2}.");
            }
            return expired.Count > 0;
        }

        /// <summary>
        /// Older markets lack base prices (given the default for their symbol) and fair prices. Their quote is the fair price,
        /// because they never had trading impact.
        /// </summary>
        private void FillMissingFields()
        {
            foreach (var stock in _market.Stocks)
            {
                if (stock.BasePrice <= 0)
                    stock.BasePrice = StockMarket.Pool.Select(p => (p.Symbol, p.Price))
                        .Concat(StockMarket.SafeHavens.Select(h => (h.Symbol, h.Price)))
                        .Where(p => p.Symbol == stock.Symbol).Select(p => p.Price).DefaultIfEmpty(stock.Price).First();
                if (stock.FairPrice <= 0)
                    stock.FairPrice = stock.Price;
            }
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
