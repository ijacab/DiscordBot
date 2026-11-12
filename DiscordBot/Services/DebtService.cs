using Common.Helpers;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Services
{
    /// <summary>
    /// Sells assets to pay off debt for players who switched on <see cref="CoinAccount.AutoSellForDebt"/> (`.autosell on`).
    /// Stocks go first, then property. Shorts and options are left alone, since their margin is locked.
    /// </summary>
    public class DebtService
    {
        /// <summary>Stock sales aim this much over the debt, since selling pushes the price down as it goes.</summary>
        public const double StockSaleBuffer = 1.02;

        private readonly CoinService _coinService;
        private readonly StockService _stockService;
        private readonly PropertyService _propertyService;

        public DebtService(CoinService coinService, StockService stockService, PropertyService propertyService)
        {
            _coinService = coinService;
            _stockService = stockService;
            _propertyService = propertyService;
        }

        /// <summary>A stock the player holds, with what one share brings in after the sell tax and spread.</summary>
        public class StockLot
        {
            public string Symbol { get; set; }
            public int Shares { get; set; }
            public double NetPerShare { get; set; }
        }

        public class SalePlan
        {
            public List<(string Symbol, int Shares)> Stocks { get; } = new List<(string, int)>();
            public List<PropertyHolding> Properties { get; } = new List<PropertyHolding>();
            public bool IsEmpty => Stocks.Count == 0 && Properties.Count == 0;
        }

        /// <summary>
        /// What to sell to cover <paramref name="debt"/>. Stocks first, the biggest holding first, only as many shares as needed.
        /// Then property: the cheapest one that covers what's left, or the most valuable one if none does, until it's covered.
        /// </summary>
        public static SalePlan Plan(double debt, IEnumerable<StockLot> stocks, IEnumerable<(PropertyHolding Holding, double SaleValue)> properties)
        {
            var plan = new SalePlan();
            double remaining = debt;

            foreach (var lot in stocks.Where(l => l.Shares > 0 && l.NetPerShare > 0).OrderByDescending(l => l.Shares * l.NetPerShare))
            {
                if (remaining <= 0) break;
                int shares = (int)Math.Min(lot.Shares, Math.Ceiling(remaining * StockSaleBuffer / lot.NetPerShare));
                plan.Stocks.Add((lot.Symbol, shares));
                remaining -= shares * lot.NetPerShare;
            }

            var left = properties.Where(p => p.SaleValue > 0).ToList();
            while (remaining > 0 && left.Count > 0)
            {
                var covering = left.Where(p => p.SaleValue >= remaining).OrderBy(p => p.SaleValue).ToList();
                var pick = covering.Count > 0 ? covering[0] : left.OrderByDescending(p => p.SaleValue).First();
                plan.Properties.Add(pick.Holding);
                remaining -= pick.SaleValue;
                left.Remove(pick);
            }
            return plan;
        }

        /// <summary>
        /// Called every minute, after spare cash has gone to debt. Sells for every player with auto-sell on and debt left,
        /// except those with a bet open. Returns one line per player that something was sold for.
        /// </summary>
        public async Task<List<string>> AutoSellForDebts(IEnumerable<ulong> skipUserIds)
        {
            var skip = new HashSet<ulong>(skipUserIds);
            var lines = new List<string>();
            var market = _stockService.GetMarket();
            double index = _coinService.GetAll().PropertyIndex;

            foreach (var account in _coinService.GetAll().Accounts.Where(a => a.AutoSellForDebt && a.Debt > 0 && !skip.Contains(a.UserId)).ToList())
            {
                var lots = market.GetHoldings(account.UserId)
                    .Select(h => new StockLot
                    {
                        Symbol = h.Key,
                        Shares = h.Value,
                        NetPerShare = (market.Find(h.Key)?.Price ?? 0) * (1 - Stock.SellTaxRate - Stock.Spread),
                    }).ToList();
                var plan = Plan(account.Debt, lots, account.Properties.Select(p => (p, PropertyCatalog.SaleValue(p.Kind, index))));
                if (plan.IsEmpty) continue;

                double debtBefore = account.Debt;
                var sold = new List<string>();
                foreach (var (symbol, shares) in plan.Stocks)
                {
                    try
                    {
                        var result = _stockService.Sell(account, symbol, shares.ToString());
                        sold.Add($"{result.Shares} {result.Stock.Symbol}");
                    }
                    catch (BadInputException) { } //the holding changed since the plan; the next minute tries again
                }
                foreach (var holding in plan.Properties)
                {
                    if (account.Debt <= 0) break;
                    int number = account.Properties.IndexOf(holding) + 1;
                    if (number < 1) continue;
                    await _propertyService.Sell(account, number);
                    sold.Add($"a {PropertyCatalog.DisplayName(holding.Kind).ToLower()}");
                }

                if (sold.Count == 0) continue;
                double paid = Math.Round(debtBefore - account.Debt, 2);
                string left = account.Debt > 0 ? $" ${FormatHelper.GetCommaNumber(account.Debt)} still owed." : " Debt cleared.";
                lines.Add($"💳 Auto-sell for {account.Name}: sold {string.Join(", ", sold)} and paid ${FormatHelper.GetCommaNumber(paid)} of debt.{left}");
            }

            if (lines.Count > 0) await _coinService.Update();
            return lines;
        }
    }
}
