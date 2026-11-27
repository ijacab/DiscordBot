using Microsoft.Extensions.Logging;
using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Models;
using Common.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;
using System.IO;
using Newtonsoft.Json;
using Common.Services;
using System.Reflection;
using DiscordBot.Models;
using DiscordBot.Services;
using DiscordBot.Text;
using DiscordBot.Games.Models.Cards;

namespace DiscordBot.Managers
{
    /// <summary>Stock market commands: `.stocks`, `.buy`, `.sell`, `.portfolio`, the live thread, old shorts.</summary>
    public partial class CommandManager
    {

        private async Task Stocks(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var market = _stockService.GetMarket();
            if (args.Count > 2) throw new BadSyntaxException();
            if (args.Count >= 1)
            {
                var chosen = market.Find(args[0]) ?? throw new BadInputException($"There's no stock called {args[0].ToUpper()}.");
                TimeSpan? window = null;
                if (args.Count == 2)
                {
                    if (!DurationParser.TryParse(args[1], out TimeSpan parsed)) throw new BadSyntaxException();
                    window = parsed;
                }
                await StockGraph(message, chosen, window);
                return;
            }

            //the chart goes first, so it still arrives before the text if that gets split into more than one message.
            //gold and bonds barely move, so their flat lines are left off (their prices are in the list and `.stocks GOLD` still graphs them)
            await SendMarketChart(message, market.Stocks.Where(s => s.Role == StockRole.Normal));

            string output = "";
            foreach (var tier in market.Stocks.Where(s => s.Role == StockRole.Normal).GroupBy(s => s.RiskTier).OrderBy(g => g.Key))
            {
                output += tier.Key == Stock.CryptoTier ? $"**Tier {tier.Key} (crypto)**\n" : $"**Tier {tier.Key}**\n";
                foreach (var stock in tier.OrderBy(s => s.Symbol))
                    output += StockRow(stock, stock.PercentChange > 0 ? "📈" : stock.PercentChange < 0 ? "📉" : "➖");
                output += "\n";
            }

            // safe havens and bonds are a different kind of listing, so they get their own section
            var havens = market.Stocks.Where(s => s.Role != StockRole.Normal).ToList();
            if (havens.Count > 0)
            {
                output += "**Safe havens** (not hit like normal stocks: gold rises in a crash, bonds pay a steady yield)\n";
                foreach (var stock in havens)
                    output += StockRow(stock, stock.TypeEmoji);
                output += "\n";
            }
            output += "*Prices change every 5 minutes. Tiers 3 to 6 run from moderate to crypto and insane. Use `.stocks SYMBOL 48h` for a graph, `.buy`/`.sell`, `.short`/`.cover`, `.call`/`.put`.*";

            await message.Channel.SendMessageAsync(embed: DiscordHelper.GetEmbedBuilder("Stock Market", output).Build(),
                components: StockPickMenu(StockTickerCard.Ordered(market.Stocks)));
        }

        private static string StockRow(Stock stock, string lead) =>
            $"{lead} **{stock.Symbol}** ${Stock.FormatPrice(stock.Price)} ({stock.PercentChange:+0.00;-0.00;0.00}% last tick | {stock.AllTimeChange:+0.00;-0.00;0.00}% all time)\n";

        /// <summary>
        /// Every stock's % change since its first kept tick, as one panel per stock in a single image (two per row), so
        /// prices of very different sizes are comparable. Each panel is labelled with its ticker.
        /// Stocks with fewer than two ticks have nothing to draw and are left out, and so are the safe havens (gold and bonds).
        /// </summary>
        private static async Task SendMarketChart(SocketMessage message, IEnumerable<Stock> stocks)
        {
            var symbols = new List<string>();
            var charts = new List<MemoryStream>();
            try
            {
                foreach (var stock in stocks)
                {
                    var history = stock.History.Count > 0 ? stock.History : new List<double> { stock.Price };
                    if (history.Count < 2) continue;

                    double first = history[0];
                    var series = new List<IReadOnlyList<double>>
                    {
                        history.Select(p => first <= 0 ? 0 : (p - first) / first * 100).ToList()
                    };
                    charts.Add(StockChart.RenderMultiPng(series, TimeSpan.FromMinutes(5), StockChart.FormatPercentLabel, StockChart.ColourForChange(stock.AllTimeChange)));
                    symbols.Add(stock.Symbol.ToUpperInvariant());
                }
                if (charts.Count == 0) return;

                using var grid = StockChart.RenderGridPng(charts, symbols, 2);
                var embed = DiscordHelper.GetEmbedBuilder("Stock prices",
                        "% change since each stock's first recorded tick, newest on the right. Green is above the launch price, red is below.")
                    .WithImageUrl("attachment://stocks.png")
                    .Build();
                await message.Channel.SendFileAsync(grid, "stocks.png", embed: embed);
            }
            finally
            {
                foreach (var chart in charts) chart.Dispose();
            }
        }

        /// <summary>
        /// Graph of one stock. <paramref name="window"/> limits it to the most recent stretch of history; a window longer than
        /// the history kept shows all of it. The line colour is always the all-time change, not the window's.
        /// </summary>
        private static async Task StockGraph(SocketMessage message, Stock stock, TimeSpan? window = null)
        {
            var card = StockTickerCard.Build(stock, window);
            if (card.Png == null)
            {
                await message.SendRichEmbedMessage(stock.Name, "Not enough price history yet. Check back after a few ticks.");
                return;
            }

            using var chart = new MemoryStream(card.Png);
            var embed = DiscordHelper.GetEmbedBuilder(card.Title, card.Text)
                .WithImageUrl("attachment://stock.png")
                .Build();
            await message.Channel.SendFileAsync(chart, "stock.png", embed: embed, components: StockTradeButtons(stock.Symbol));
        }

        private async Task StocksThread(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 1 || args.Count == 1 && !args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
                throw new BadSyntaxException();
            if (message.Channel is IThreadChannel || message.Channel is not ITextChannel channel)
                throw new BadInputException("Run `.stocksthread` in a normal server text channel, not a thread or DM.");

            if (args.Count == 1)
            {
                bool stopped = await _stockThreadService.StopAsync(client, channel.Guild);
                await message.Channel.SendMessageAsync(stopped ? "Live stock tickers stopped." : "There's no live stock thread in this server.");
                return;
            }

            IThreadChannel thread;
            try
            {
                thread = await _stockThreadService.StartAsync(client, channel);
            }
            catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new BadInputException("I need the Create Public Threads and Send Messages in Threads permissions here.");
            }
            await message.Channel.SendMessageAsync($"Live tickers are in <#{thread.Id}>. They update every 5 minutes.");
        }

        /// <summary>`.buy property` and `.sell property`: "property", "house" and "business" mean property, not a stock.</summary>
        private static bool IsPropertyWord(string word) =>
            new[] { "property", "properties", "house", "houses", "business", "businesses" }.Contains(word.ToLowerInvariant());

        private static bool IsBusinessWord(string word) => word.StartsWith("business", StringComparison.OrdinalIgnoreCase);

        private async Task BuyStock(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count >= 1 && IsPropertyWord(args[0]))
            {
                //`.buy house` is a house; `.buy property mansion` and `.buy business cafe` name the kind
                var rest = args.Skip(1).ToList();
                if (rest.Count == 0 && args[0].StartsWith("house", StringComparison.OrdinalIgnoreCase)) rest.Add("house");
                await BuyProperty(client, message, rest, business: IsBusinessWord(args[0]));
                return;
            }
            if (args.Count != 2) throw new BadSyntaxException();
            var (text, symbol) = await BuyStockText(message.Author.Id, message.Author.Username, message.Author.Mention, args[0], args[1]);
            await message.Channel.SendMessageAsync(text, components: StockTradeButtons(symbol));
        }

        private async Task SellStock(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count >= 1 && IsPropertyWord(args[0]))
            {
                await SellProperty(client, message, args.Skip(1).ToList());
                return;
            }
            if (args.Count != 2) throw new BadSyntaxException();
            var (text, symbol) = await SellStockText(message.Author.Id, message.Author.Username, message.Author.Mention, args[0], args[1]);
            await message.Channel.SendMessageAsync(text, components: StockTradeButtons(symbol));
        }

        /// <summary>Shorting is gone: a put does the same job with the downside capped at what you put in.</summary>
        private Task ShortStock(DiscordSocketClient client, SocketMessage message, List<string> args) =>
            message.Channel.SendMessageAsync($"{message.Author.Mention} shorting is dead. Bet on a stock tanking with a put instead: `.put SYMBOL 1000 2x` is a short that can't lose more than you put in. Old shorts still close with `.cover`.");

        private async Task CoverStock(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 2) throw new BadSyntaxException();
            EnsureNotBetting(message);

            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            var (stock, shares, returned, profit) = _stockService.Cover(account, args[0], args[1]);

            await message.Channel.SendMessageAsync($"{message.Author.Mention} covered {shares} share(s) of {stock.Symbol} and got back ${FormatHelper.GetCommaNumber(returned)} ({SignedMoney(profit)}). {stock.Symbol} is now ${Stock.FormatPrice(stock.Price)}. Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.");
        }

        private static string FormatRemaining(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{(int)Math.Ceiling(span.TotalMinutes)}m";
        }

        private async Task Portfolio(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 1) throw new BadSyntaxException();

            CoinAccount account;
            if (args.Count == 0)
            {
                account = await _coinService.Get(message.Author.Id, message.Author.Username);
            }
            else if (DiscordHelper.TryGetUserId(args[0], out ulong userId))
            {
                account = _coinService.GetAll().Accounts.FirstOrDefault(a => a.UserId == userId)
                    ?? throw new BadInputException("Account not found.");
            }
            else
            {
                throw new BadSyntaxException();
            }

            var market = _stockService.GetMarket();
            var holdings = market.GetHoldings(account.UserId);
            var shorts = market.GetShorts(account.UserId);
            var options = market.GetOptions(account.UserId).OrderBy(o => o.ExpiresAtUtcTicks).ToList();
            var bets = market.GetBets(account.UserId).OrderBy(b => b.ExpiresUtcTicks).ToList();
            double stocksValue = _stockService.GetPortfolioValue(account.UserId);
            double propertyValue = _coinService.PropertyValue(account);
            double index = _coinService.GetAll().PropertyIndex;
            double total = _worth.TotalWorth(account);
            var lines = new List<string> { $"**Worth {Money.Format(total)}**", $"💵 Cash {Money.Format(account.NetWorth)}" };

            //stocks: two short lines each, like it used to be. The first is what you hold and what it's worth, the second is what you paid and how you're doing
            if (holdings.Count > 0 || shorts.Count > 0)
            {
                var (totalGain, totalCost) = market.GetUnrealisedGain(account.UserId);
                double sharesValue = holdings.Sum(h => (market.Find(h.Key)?.Price ?? 0) * h.Value);
                lines.Add($"\n📈 **Stocks {Money.Format(sharesValue)}**");
                foreach (var holding in holdings.OrderByDescending(h => (market.Find(h.Key)?.Price ?? 0) * h.Value))
                {
                    var stock = market.Find(holding.Key);
                    double cost = market.GetAverageCost(account.UserId, holding.Key);
                    string trend = cost <= 0 ? "➖" : stock.Price >= cost ? "📈" : "📉";
                    lines.Add($"{trend} **{holding.Key}** · {holding.Value:N0} shares · now ${Stock.FormatPrice(stock.Price)} each = {Money.Format(stock.Price * holding.Value)}");
                    if (cost <= 0)
                        lines.Add("  ↳ buy price unknown");
                    else
                        lines.Add($"  ↳ bought at ${Stock.FormatPrice(cost)} · {Money.Signed((stock.Price - cost) * holding.Value)} ({(stock.Price - cost) / cost * 100:+0.##;-0.##;0}%)");
                }
                foreach (var held in shorts.OrderBy(x => x.Key))
                {
                    var stock = market.Find(held.Key);
                    double gain = held.Value.EquityAt(stock.Price) - held.Value.Margin;
                    lines.Add($"{(gain >= 0 ? "📈" : "📉")} **{held.Key}** · short {held.Value.Shares:N0} shares · now ${Stock.FormatPrice(stock.Price)} each");
                    lines.Add($"  ↳ sold at ${Stock.FormatPrice(held.Value.EntryPrice)} · {Money.Signed(gain)} · `.cover {held.Key} all` to close");
                }
                if (holdings.Count > 0)
                    lines.Add(totalCost > 0
                        ? $"📊 All stocks: {Money.Signed(totalGain)} ({totalGain / totalCost * 100:+0.##;-0.##;0}%) on what you paid"
                        : "📊 All stocks: no known buy prices");
            }

            //calls and puts: what each pays if it closed now
            if (bets.Count > 0 || options.Count > 0)
            {
                lines.Add($"\n🎲 **Calls and puts {Money.Format(bets.Sum(b => b.ValueAt(market.Find(b.Symbol)?.FairPrice ?? b.EntryPrice)) + options.Sum(o => o.PayoutAt(market.Find(o.Symbol)?.FairPrice ?? o.Strike)))}**");
                foreach (var bet in bets)
                {
                    double value = bet.ValueAt(market.Find(bet.Symbol)?.FairPrice ?? bet.EntryPrice);
                    lines.Add($"{(value >= bet.Paid ? "🟢" : "🔴")} {bet.Leverage}x {bet.Symbol} {bet.Kind} · {Money.Format(value)} of {Money.Format(bet.Paid)} · {FormatRemaining(bet.ExpiresAt - DateTime.UtcNow)} left");
                }
                foreach (var option in options)
                    lines.Add($"🎟️ old {option.Symbol} {(option.IsCall ? "call" : "put")} · {Money.Format(option.PayoutAt(market.Find(option.Symbol)?.FairPrice ?? option.Strike))} · {FormatRemaining(option.ExpiresAt - DateTime.UtcNow)} left");
            }

            //property: a total, then a line per type owned (houses first, then businesses, in catalog order)
            if (account.Properties.Count > 0)
                lines.AddRange(PropertyLines(account, index, propertyValue));

            if (account.Debt > 0)
                lines.Add($"\n💳 **Debt {Money.Format(-account.Debt)}**, growing {CoinAccount.HourlyDebtInterest:P0} an hour. Auto-sell is {(account.AutoSellForDebt ? "on" : "off (`.autosell on`)")}.");

            //sell menus and the auto-sell switch only for your own portfolio, and they only work for you
            var components = account.UserId == message.Author.Id ? PortfolioComponents(account, holdings) : null;
            await SendPagedEmbeds(message.Channel, $"{account.Name}'s portfolio", lines, components);
        }

        /// <summary>The property part of `.portfolio`: the total and what it earns an hour, then each type owned with its count, value, gain if sold and earnings.</summary>
        public static List<string> PropertyLines(CoinAccount account, double index, double propertyValue)
        {
            var lines = new List<string>();
            var squatted = account.Running(PowerUpIds.Squatted, DateTime.UtcNow).Select(sq => PropertyService.SquattedPropertyId(account, sq)).ToHashSet();
            double Earning(IEnumerable<PropertyHolding> places) =>
                places.Where(p => !squatted.Contains(p.Id)).Sum(p => PropertyCatalog.NetRentPerHour(p.Kind, index)) - places.Sum(p => PropertyCatalog.HourlyHoldingCost(p.Kind, index));

            double earning = Earning(account.Properties);
            string squat = squatted.Count > 0 ? $" · 🏚️ squatters in {squatted.Count}" : "";
            lines.Add($"\n🏘️ **Property {Money.Format(propertyValue)}** · {(earning >= 0 ? "🟢" : "🔴")} earning {Money.Signed(earning)} an hour after rent, fees and upkeep{squat}");
            foreach (var type in PropertyCatalog.Types)
            {
                var places = account.Properties.Where(p => p.Kind == type.Kind).ToList();
                if (places.Count == 0) continue;
                double value = places.Sum(p => PropertyCatalog.SaleValue(p.Kind, index));
                double paid = places.Sum(p => p.PurchaseCost);
                double perHour = Earning(places);
                string name = places.Count == 1 ? type.Name : PluralName(type.Name);
                lines.Add($"{type.Emoji} **{places.Count} {name} {Money.Format(value)}** (paid {Money.Format(paid)}, {Money.Signed(value - paid)} if sold now) · {Money.Signed(perHour)}/h{(type.IsBusiness ? $" · swings ±{PropertyCatalog.BusinessIncomeSwing:0%}" : "")}");
            }
            return lines;
        }

        /// <summary>"Shack" to "Shacks", "Luxury home" to "Luxury homes", "Café" to "Cafés": every kind's plural is just an s.</summary>
        private static string PluralName(string name) => name + "s";

        private static string SignedMoney(double amount) =>
            $"{(amount < 0 ? "-" : "+")}${FormatHelper.GetCommaNumber(Math.Abs(amount))}";
    }
}
