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
                        stock.ApplyTick(_random.NextDouble, MarketCycle.DriftFor(stock, data.MarketPhase, data.CrisisSeverity));
                        stock.RecordHistory();
                    }
                    _market.LastTickSlot = slot;
                    changed = true;
                    LiquidateBrokenShorts();
                }

                changed |= SettleExpiredOptions(now);
                if (SettleBets(now))
                {
                    changed = true;
                    coinChanged = true;
                }
                if (changed) Save();
            }
            if (coinChanged) await _coinService.Update();
        }

        /// <summary>
        /// The share of all player wealth held in ordinary stocks and houses, 0 to 1. It sets the market crash chance
        /// (<see cref="MarketCycle.ChanceFor"/>). Gold and bonds count in the total but not as risky, so holding them lowers it.
        /// </summary>
        public double RiskyAssetShare()
        {
            lock (_lock)
            {
                double risky = 0, total = 0;
                foreach (var account in _coinService.GetAll().Accounts)
                {
                    double property = _coinService.PropertyValue(account);
                    risky += property;
                    total += property + Math.Max(0, account.NetWorth);

                    if (!_market.Portfolios.TryGetValue(account.UserId, out var holdings)) continue;
                    foreach (var (symbol, shares) in holdings)
                    {
                        var stock = _market.Find(symbol);
                        if (stock == null) continue;
                        double value = shares * stock.Price;
                        total += value;
                        if (stock.Role == StockRole.Normal) risky += value;
                    }
                }
                return MarketCycle.ExposureOf(risky, total);
            }
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
                //a holding can't pass int.MaxValue shares, so that's the most that can be added to it
                int room = int.MaxValue - _market.GetShares(account.UserId, stock.Symbol);
                if (room < 1) throw StockMarket.TooManyShares(stock.Symbol);
                bool isPercent = TryParsePercent(amount, out double share);
                int shares = isPercent ? stock.MaxAffordableShares(account.NetWorth * share, spread, room)
                    : amount.Equals("max", StringComparison.OrdinalIgnoreCase) ? stock.MaxAffordableShares(account.NetWorth, spread, room)
                    : ParseShares(amount);
                if (shares > room) throw StockMarket.TooManyShares(stock.Symbol);

                if (shares < 1)
                    throw new BadInputException(isPercent
                        ? $"{amount} of your cash (${account.NetWorth * share:N2}) can't buy even one share of {stock.Symbol} (${Stock.FormatPrice(stock.Price)})."
                        : $"You can't afford even one share of {stock.Symbol} (${Stock.FormatPrice(stock.Price)}).");

                double fill = stock.PreviewTrade(shares, spread).Fill;
                double cost = Math.Round(shares * fill, 2);
                if (cost > account.NetWorth)
                    throw new BadInputException($"{shares} shares of {stock.Symbol} cost ${cost:N2} but you only have ${account.NetWorth:N2}.");

                stock.ApplyTrade(shares);
                account.NetWorth -= cost;
                _market.RecordPurchase(account.UserId, stock.Symbol, shares, fill);
                account.Stats.StockBuys++;
                account.Stats.SharesBought += shares;
                account.Stats.MoneySpentOnStocks += cost;
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
                int shares = IsAllOrMax(amount) ? owned : SharesFromAmount(amount, owned);

                if (owned < 1)
                    throw new BadInputException($"You don't own any {stock.Symbol}.");
                if (shares < 1)
                    throw new BadInputException($"{amount} of your {owned} {stock.Symbol} is less than one share.");
                if (shares > owned)
                    throw new BadInputException($"You only own {owned} shares of {stock.Symbol}.");

                double fill = stock.PreviewTrade(-shares, SpreadFor(account)).Fill;
                double gross = Math.Round(shares * fill, 2);
                double tax = account.IsActive(PowerUpIds.TaxHaven, DateTime.UtcNow) ? 0 : Stock.TaxOn(gross);
                double costOfShares = _market.GetAverageCost(account.UserId, stock.Symbol) * shares;
                _coinService.AddToJackpot(tax);
                stock.ApplyTrade(-shares);
                account.NetWorth += _coinService.GetAll().PayDebtFrom(account, gross - tax); //sale money pays any debt first, into the jackpot
                _market.AddShares(account.UserId, stock.Symbol, -shares);
                double profit = gross - tax - costOfShares;
                var stats = account.Stats;
                stats.StockSells++;
                stats.SharesSold += shares;
                stats.StockSaleProceeds += gross;
                stats.SellTaxPaid += tax;
                stats.StockRealizedProfit += profit;
                stats.BestStockSale = Math.Max(stats.BestStockSale, profit);
                stats.WorstStockSale = Math.Min(stats.WorstStockSale, profit);
                Save();
                return PersistAccount(account, (stock, shares, gross, tax));
            }
        }

        /// <summary>The spread a player pays on buys and sells: none while they have Insider Trader running.</summary>
        private static double SpreadFor(CoinAccount account) =>
            account.IsActive(PowerUpIds.InsiderTrader, DateTime.UtcNow) ? 0 : Stock.Spread;

        /// <summary>Sells borrowed shares. The sale value is locked from cash as margin, and the sale tax goes to the jackpot.</summary>
        public (Stock Stock, int Shares, double Margin, double Tax) Short(CoinAccount account, string symbol, string amount)
        {
            lock (_lock)
            {
                var stock = _market.Find(symbol) ?? throw new BadInputException($"There's no stock called {symbol.ToUpper()}.");
                int room = int.MaxValue - (_market.GetShort(account.UserId, stock.Symbol)?.Shares ?? 0);
                if (room < 1) throw StockMarket.TooManyShares(stock.Symbol);
                bool isPercent = TryParsePercent(amount, out double share);
                int shares = isPercent ? stock.MaxShortableShares(account.NetWorth * share, room)
                    : amount.Equals("max", StringComparison.OrdinalIgnoreCase) ? stock.MaxShortableShares(account.NetWorth, room)
                    : ParseShares(amount);
                if (shares > room) throw StockMarket.TooManyShares(stock.Symbol);

                if (shares < 1)
                    throw new BadInputException($"You can't afford the margin for even one share of {stock.Symbol} (${Stock.FormatPrice(stock.Price)}){(isPercent ? $" with {amount} of your cash" : "")}.");

                double fill = stock.PreviewTrade(-shares).Fill;
                double margin = Math.Round(shares * fill, 2);
                double tax = Stock.TaxOn(margin);
                if (margin + tax > account.NetWorth)
                    throw new BadInputException($"Shorting {shares} shares of {stock.Symbol} needs ${margin + tax:N2} (margin plus tax) but you only have ${account.NetWorth:N2}.");

                _coinService.AddToJackpot(tax);
                stock.ApplyTrade(-shares);
                account.NetWorth -= margin + tax;
                _market.AddShort(account.UserId, stock.Symbol, shares, fill, margin);
                account.Stats.ShortsOpened++;
                account.Stats.SellTaxPaid += tax;
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
                int shares = IsAllOrMax(amount) ? position.Shares : SharesFromAmount(amount, position.Shares);
                if (shares < 1)
                    throw new BadInputException($"{amount} of your {position.Shares} shorted {stock.Symbol} is less than one share.");
                if (shares > position.Shares)
                    throw new BadInputException($"You're only short {position.Shares} shares of {stock.Symbol}.");

                double fill = stock.PreviewTrade(shares).Fill;
                double profit = Math.Round((position.EntryPrice - fill) * shares, 2);
                double released = _market.ReduceShort(account.UserId, stock.Symbol, shares);
                double returned = Math.Max(0, Math.Round(released + profit, 2));

                stock.ApplyTrade(shares);
                account.NetWorth += returned;
                account.Stats.ShortsCovered++;
                account.Stats.ShortProfit += returned - released;
                Save();
                return PersistAccount(account, (stock, shares, returned, returned - released));
            }
        }

        /// <summary>The market-wide crash drift on a stock right now, which options are priced with.</summary>
        private MarketDrift CurrentDrift(Stock stock)
        {
            var data = _coinService.GetAll();
            return MarketCycle.DriftFor(stock, data.MarketPhase, data.CrisisSeverity);
        }

        /// <summary>A call or put quote: what share of what's paid rides at the leverage, and what it's expected to pay back.</summary>
        public sealed class BetQuote
        {
            public Stock Stock { get; set; }
            public bool IsCall { get; set; }
            public int Leverage { get; set; }
            public TimeSpan Duration { get; set; }
            public double ExpectedMultiple { get; set; }
            public double RidingShare { get; set; }
            public bool TooDear => 1 - RidingShare > LeverageRules.MaxFeeShare;
        }

        /// <summary>Prices a call or put on <paramref name="symbol"/> from the stock's current state. Takes a moment the first time (it simulates).</summary>
        public BetQuote QuoteBet(string symbol, bool isCall, int leverage, TimeSpan duration)
        {
            if (!LeverageRules.Leverages.Contains(leverage))
                throw new BadInputException($"Leverage is {string.Join(", ", LeverageRules.Leverages.Select(l => l + "x"))}. Pick one.");
            if (duration < LeverageRules.MinDuration || duration > LeverageRules.MaxDuration)
                throw new BadInputException("Calls and puts run from 15m to 24h. Pick something in that range.");
            Stock copy;
            MarketDrift drift;
            lock (_lock)
            {
                var stock = _market.Find(symbol) ?? throw new BadInputException($"There's no stock called {symbol.ToUpper()}, dumbass. Check `.stocks`.");
                if (stock.Role != StockRole.Normal)
                    throw new BadInputException($"{stock.Symbol} is a safe haven. Nobody's gambling on bonds and gold, buy it with `.buy`.");
                copy = JsonConvert.DeserializeObject<Stock>(JsonConvert.SerializeObject(stock));
                drift = CurrentDrift(stock);
            }
            //simulated outside the lock so the market isn't held up
            double expected = LeveragePricer.ExpectedMultiple(copy, isCall, leverage, LeverageRules.TicksFor(duration), drift);
            return new BetQuote
            {
                Stock = copy, IsCall = isCall, Leverage = leverage, Duration = duration,
                ExpectedMultiple = expected, RidingShare = LeverageRules.RidingShare(expected),
            };
        }

        /// <summary>
        /// Opens a call or put for an amount of cash ("1000", "25%" of cash, "max"). Most of it rides at the leverage and the fee
        /// goes into the jackpot. Settles on the fair price when it closes (<see cref="SettleBets"/>).
        /// </summary>
        public (LeveragedBet Bet, BetQuote Quote) OpenBet(CoinAccount account, ulong channelId, string symbol, bool isCall, string amount, int leverage, TimeSpan duration)
        {
            var quote = QuoteBet(symbol, isCall, leverage, duration);
            if (quote.TooDear)
                throw new BadInputException($"A {leverage}x {quote.Stock.Symbol} {(isCall ? "call" : "put")} is too damn likely to pay right now, so the fee would eat over {LeverageRules.MaxFeeShare:P0} of it. Use less leverage or a shorter bet.");
            lock (_lock)
            {
                var stock = _market.Find(quote.Stock.Symbol) ?? throw new BadInputException($"{quote.Stock.Symbol} just got taken off the market.");
                double cash = Math.Max(0, account.NetWorth);
                double paid = TryParsePercent(amount, out double share) ? Math.Floor(cash * share * 100) / 100
                    : IsAllOrMax(amount) ? Math.Floor(cash * 100) / 100
                    : ParseMoney(amount);
                if (paid < 1) throw new BadInputException("Bet at least $1, you cheapskate.");
                if (paid > account.NetWorth)
                    throw new BadInputException($"You've got ${cash:N2} in cash. You can't bet ${paid:N2}.");

                double stake = Math.Round(paid * quote.RidingShare, 2);
                var bet = new LeveragedBet
                {
                    UserId = account.UserId,
                    ChannelId = channelId,
                    Symbol = stock.Symbol,
                    IsCall = isCall,
                    Leverage = leverage,
                    Paid = paid,
                    Stake = stake,
                    EntryPrice = stock.FairPrice,
                    OpenedUtcTicks = DateTime.UtcNow.Ticks,
                    ExpiresUtcTicks = (DateTime.UtcNow + duration).Ticks,
                };
                _market.Bets.Add(bet);
                account.NetWorth -= paid;
                _coinService.AddToJackpot(bet.Fee);
                account.Stats.OptionContractsBought++;
                account.Stats.OptionPremiumsPaid += paid;
                Save();
                return PersistAccount(account, (bet, quote));
            }
        }

        /// <summary>Whether a sell, cover or bet amount means everything: "all" or "max".</summary>
        private static bool IsAllOrMax(string amount) =>
            amount.Equals("all", StringComparison.OrdinalIgnoreCase) || amount.Equals("max", StringComparison.OrdinalIgnoreCase);

        /// <summary>A whole-dollar (or cents) amount like 1000, 1,000 or $1000.50. Anything else, NaN included, is a syntax error.</summary>
        private static double ParseMoney(string amount)
        {
            string text = (amount ?? "").Trim().Replace(",", "").Replace("$", "");
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value) || value <= 0)
                throw new BadSyntaxException();
            return Math.Floor(value * 100) / 100;
        }

        /// <summary>A call or put that closed: who, what it paid and where to say so.</summary>
        public sealed class SettledBet
        {
            public LeveragedBet Bet { get; set; }
            public double ClosePrice { get; set; }
            public double Payout { get; set; }
        }

        private readonly List<SettledBet> _settledBets = new List<SettledBet>();

        /// <summary>Calls and puts that closed since the last call, for announcing. Empties the list.</summary>
        public List<SettledBet> TakeSettledBets()
        {
            lock (_lock)
            {
                var list = _settledBets.ToList();
                _settledBets.Clear();
                return list;
            }
        }

        /// <summary>Pays out calls and puts whose time is up, on the fair price. Caller holds the lock. True when any closed.</summary>
        private bool SettleBets(long nowTicks)
        {
            var due = _market.Bets.Where(b => b.ExpiresUtcTicks <= nowTicks).ToList();
            foreach (var bet in due)
            {
                double close = _market.Find(bet.Symbol)?.FairPrice ?? bet.EntryPrice;
                double payout = bet.ValueAt(close);
                PayBet(bet, payout, close, "closed");
            }
            return due.Count > 0;
        }

        private void PayBet(LeveragedBet bet, double payout, double close, string how)
        {
            _market.Bets.Remove(bet);
            var data = _coinService.GetAll();
            var account = data.Accounts.FirstOrDefault(a => a.UserId == bet.UserId);
            if (account != null && payout > 0)
            {
                account.NetWorth += data.PayDebtFrom(account, payout); //winnings pay any debt first
                account.Stats.OptionPayouts += payout;
                if (payout > bet.Paid) account.Stats.OptionsExpiredInTheMoney++;
            }
            _market.AddSettledResult(bet.UserId,
                $"{bet.Leverage}x {bet.Symbol} {bet.Kind} {how} at ${Stock.FormatPrice(close)} (in at ${Stock.FormatPrice(bet.EntryPrice)}): paid ${payout:N2} for ${bet.Paid:N2}.");
            _settledBets.Add(new SettledBet { Bet = bet, ClosePrice = close, Payout = payout });
        }

        /// <summary>Closes a call or put early at what it would pay now. Only debt auto-sell does this; players ride them out.</summary>
        public (LeveragedBet Bet, double Paid) CloseBetForDebt(CoinAccount account, string betId)
        {
            lock (_lock)
            {
                var bet = _market.Bets.FirstOrDefault(b => b.Id == betId && b.UserId == account.UserId)
                    ?? throw new BadInputException("That bet has already closed.");
                double close = _market.Find(bet.Symbol)?.FairPrice ?? bet.EntryPrice;
                double payout = bet.ValueAt(close);
                if (payout <= 0) throw new BadInputException("That bet is worth nothing right now.");
                PayBet(bet, payout, close, "sold early to pay debt");
                _settledBets.RemoveAt(_settledBets.Count - 1); //auto-sell announces it itself
                Save();
                _coinService.Update().GetAwaiter().GetResult();
                return (bet, payout);
            }
        }

        /// <summary>
        /// Cashes an open option out early at what it would pay now (on the fair price, as `.portfolio` values it), less the
        /// option fee into the jackpot. Debt auto-sell uses it; an option worth nothing can't be cashed out.
        /// </summary>
        public (OptionContract Option, double Paid) CloseOption(CoinAccount account, string optionId)
        {
            lock (_lock)
            {
                var option = _market.Options.FirstOrDefault(o => o.Id == optionId && o.UserId == account.UserId)
                    ?? throw new BadInputException("That option has already expired or been closed.");
                double fair = _market.Find(option.Symbol)?.FairPrice ?? option.Strike;
                double payout = option.PayoutAt(fair);
                if (payout <= 0) throw new BadInputException("That option is worth nothing right now.");

                double fee = Math.Round(payout * Stock.OptionFeeRate, 2);
                double paid = Math.Round(payout - fee, 2);
                _market.Options.Remove(option);
                _coinService.AddToJackpot(fee);
                account.NetWorth += paid;
                account.Stats.OptionPayouts += paid;
                string kind = option.IsCall ? "call" : "put";
                _market.AddSettledResult(option.UserId,
                    $"{option.Contracts}x {option.Symbol} {kind} @ ${Stock.FormatPrice(option.Strike)} cashed out early at ${Stock.FormatPrice(fair)} to pay debt: ${paid:N2} after the fee, for ${option.Premium:N2}.");
                Save();
                return PersistAccount(account, (option, paid));
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
                    _market.AddSettledResult(user, $"Short {held.Key} liquidated at ${Stock.FormatPrice(stock.Price)}, margin lost.");
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
                    account.Stats.OptionPayouts += payout;
                    account.Stats.OptionsExpiredInTheMoney++;
                    _coinService.Update(account.UserId, account.NetWorth, account.Name).GetAwaiter().GetResult();
                }
                string kind = option.IsCall ? "call" : "put";
                _market.AddSettledResult(option.UserId,
                    $"{option.Contracts}x {option.Symbol} {kind} @ ${Stock.FormatPrice(option.Strike)} expired at ${Stock.FormatPrice(fair)}: paid ${payout:N2} for ${option.Premium:N2}.");
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

        /// <summary>
        /// A share of something written like "50%" (or "12.5%"): 0 to 100, as a fraction. False for anything else, and a "%" amount that isn't
        /// a number from above 0 to 100 is a syntax error.
        /// </summary>
        public static bool TryParsePercent(string amount, out double fraction)
        {
            fraction = 0;
            if (amount == null || !amount.EndsWith("%")) return false;
            if (!double.TryParse(amount.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent)
                || double.IsNaN(percent) || percent <= 0 || percent > 100)
                throw new BadSyntaxException();
            fraction = percent / 100;
            return true;
        }

        /// <summary>Shares for an amount of a holding: a whole number, or a percentage of it rounded down (50% of 7 is 3).</summary>
        private static int SharesFromAmount(string amount, int held) =>
            TryParsePercent(amount, out double share) ? (int)Math.Floor(held * share + 1e-9) : ParseShares(amount);

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
