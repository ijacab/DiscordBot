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
using DiscordBot.Games.Models.BattleArena;

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

        private async Task BuyStock(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 2) throw new BadSyntaxException();
            var (text, symbol) = await BuyStockText(message.Author.Id, message.Author.Username, message.Author.Mention, args[0], args[1]);
            await message.Channel.SendMessageAsync(text, components: StockTradeButtons(symbol));
        }

        private async Task SellStock(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
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

            string output = "";
            foreach (var holding in holdings.OrderBy(x => x.Key))
            {
                var stock = market.Find(holding.Key);
                double averageCost = market.GetAverageCost(account.UserId, holding.Key);
                string trend = averageCost <= 0 ? "➖" : stock.Price >= averageCost ? "📈" : "📉";
                output += $"{trend} **{holding.Key}** {holding.Value} sh · now ${Stock.FormatPrice(stock.Price)} = ${FormatHelper.GetCommaNumber(stock.Price * holding.Value)}\n";

                if (averageCost <= 0)
                {
                    output += "  ↳ buy price unknown\n";
                    continue;
                }
                double gain = (stock.Price - averageCost) * holding.Value;
                double gainPercent = (stock.Price - averageCost) / averageCost * 100;
                output += $"  ↳ bought @ ${averageCost:N2} · {SignedMoney(gain)} ({gainPercent:+0.00;-0.00;0.00}%)\n";
            }

            foreach (var held in shorts.OrderBy(x => x.Key))
            {
                var stock = market.Find(held.Key);
                var position = held.Value;
                double shortGain = position.EquityAt(stock.Price) - position.Margin;
                output += $"{(shortGain >= 0 ? "📈" : "📉")} **{held.Key}** short {position.Shares} sh · sold @ ${Stock.FormatPrice(position.EntryPrice)}, now ${Stock.FormatPrice(stock.Price)} · margin ${FormatHelper.GetCommaNumber(position.Margin)} · {SignedMoney(shortGain)}\n";
            }
            foreach (var option in options)
            {
                double fair = market.Find(option.Symbol)?.FairPrice ?? option.Strike;
                output += $"🎟️ {option.Contracts}x **{option.Symbol}** old {(option.IsCall ? "call" : "put")} strike ${Stock.FormatPrice(option.Strike)} · {FormatRemaining(option.ExpiresAt - DateTime.UtcNow)} left · worth ${FormatHelper.GetCommaNumber(option.PayoutAt(fair))}\n";
            }
            var bets = market.GetBets(account.UserId).OrderBy(b => b.ExpiresUtcTicks).ToList();
            foreach (var bet in bets)
                output += BetLine(bet, market.Find(bet.Symbol)?.FairPrice ?? bet.EntryPrice) + "\n";

            var (totalGain, totalCost) = market.GetUnrealisedGain(account.UserId);
            double stocksValue = _stockService.GetPortfolioValue(account.UserId);
            double propertyValue = _coinService.PropertyValue(account);
            double index = _coinService.GetAll().PropertyIndex;

            output += "\n**Everything you own**\n";
            output += $"💵 Cash ${FormatHelper.GetCommaNumber(account.NetWorth)}\n";
            if (holdings.Count > 0 || shorts.Count > 0 || options.Count > 0 || bets.Count > 0)
                output += $"📈 Stocks and positions ${FormatHelper.GetCommaNumber(stocksValue)}\n";
            //grouped by kind: every house is worth the same (one market index), so one line each with the average paid
            foreach (var group in account.Properties.GroupBy(p => p.Kind).OrderBy(g => g.Key))
            {
                int count = group.Count();
                string name = PropertyCatalog.DisplayName(group.Key);
                double worthEach = PropertyCatalog.SaleValue(group.Key, index);
                double averagePaid = group.Average(p => p.PurchaseCost);
                string rentEach = FormatHelper.GetCommaNumber(PropertyCatalog.NetRentPerHour(group.Key, index));
                string icon = group.Key == PropertyKind.House ? "🏠" : "🏢";
                output += count == 1
                    ? $"{icon} {name} (bought ${FormatHelper.GetCommaNumber(averagePaid)}) · worth ${FormatHelper.GetCommaNumber(worthEach)} after selling costs · rent ${rentEach} an hour\n"
                    : $"{icon} {count} {name.ToLower()}s (avg bought ${FormatHelper.GetCommaNumber(averagePaid)}) · worth ${FormatHelper.GetCommaNumber(worthEach)} each, ${FormatHelper.GetCommaNumber(worthEach * count)} in all, after selling costs · rent ${rentEach} an hour each\n";
            }
            if (account.Properties.Count > 0)
            {
                output += $"🏘️ Property value ${FormatHelper.GetCommaNumber(propertyValue)}\n";
                //rent from properties with squatters in goes to the squatter instead
                var squatted = account.Running(PowerUpIds.Squatted, DateTime.UtcNow).Select(s => PropertyService.SquattedPropertyId(account, s)).ToHashSet();
                double rentTotal = account.Properties.Where(p => !squatted.Contains(p.Id)).Sum(p => PropertyCatalog.NetRentPerHour(p.Kind, index));
                double squattersTake = account.Properties.Where(p => squatted.Contains(p.Id)).Sum(p => PropertyCatalog.NetRentPerHour(p.Kind, index));
                output += $"🏘️ Rent ${FormatHelper.GetCommaNumber(rentTotal)} an hour after the agency (about ${FormatHelper.GetCommaNumber(rentTotal * 24)} a day)" +
                    (squattersTake > 0 ? $" · squatters take ${FormatHelper.GetCommaNumber(squattersTake)} an hour" : "") + "\n";
            }
            if (account.Debt > 0)
                output += $"💳 Debt -${FormatHelper.GetCommaNumber(account.Debt)} (grows 10% every hour; paid off from any spare cash, and sales pay it off first; repayments go into the jackpot; auto-sell is {(account.AutoSellForDebt ? "on" : "off, `.autosell on`")})\n";
            output += $"🧾 Total ${FormatHelper.GetCommaNumber(account.NetWorth + stocksValue + propertyValue - account.Debt)}\n";
            if (holdings.Count > 0)
            {
                output += totalCost > 0
                    ? $"📊 Stock gain/loss {SignedMoney(totalGain)} ({totalGain / totalCost * 100:+0.00;-0.00;0.00}%) on known buy prices"
                    : "📊 Stock gain/loss n/a, no known buy prices";
            }

            //sell menus and the auto-sell switch only for your own portfolio, and they only work for you
            var components = account.UserId == message.Author.Id ? PortfolioComponents(account, holdings) : null;
            await SendPagedEmbeds(message.Channel, $"{account.Name}'s portfolio", output.Split('\n'), components);
        }

        private static string SignedMoney(double amount) =>
            $"{(amount < 0 ? "-" : "+")}${FormatHelper.GetCommaNumber(Math.Abs(amount))}";
    }
}
