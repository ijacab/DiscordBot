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
    public partial class CommandManager
    {
        private async Task Add(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count() != 2)
                throw new BadSyntaxException();

            if (args[0].StartsWith('.'))
                throw new BadSyntaxException("you can't FUCKING create a custom command that starts with a '.'. STOP TRYING BREAK ME MY BRIAN TOO BIG FOR YOU");

            if (args[0].Length <= 1)
                throw new BadSyntaxException("key must be length of 2 or greater.");

            await _mappingService.Add(args[0], args[1]);
            await message.Channel.SendMessageAsync($"Added mapping {args[0]} : {args[1]}");
            _customMappings = await _mappingService.GetAll();
        }

        private async Task Remove(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count() != 1)
            {
                throw new BadSyntaxException();
            }
            else
            {
                await _mappingService.Remove(args[0]);
                await message.Channel.SendMessageAsync($"Removed mapping {args[0]}");
            }
        }

        private async Task Clear(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args[0].StartsWith("mapping"))
            {
                await _mappingService.ClearAll();
                _customMappings = await _mappingService.GetAll();
            }
            else if (args[0].StartsWith("remind"))
            {
                await _reminderService.ClearAll();
            }
            else if (args[0].StartsWith("coin"))
            {
                await _coinService.ClearAll();
                await _stockService.ClearAll();
            }
            else
            {
                return;
            }

            await message.Channel.SendMessageAsync($"Cleared all custom {args[0]}");

        }

        private async Task Age(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            string info = $@"Your account was created at {message.Author.CreatedAt.DateTime.ToString("yyyy-MM-dd hh:mm:ss")}";
            var age = DateTimeOffset.Now - message.Author.CreatedAt.DateTime;
            info += $"\n\nYou are {Convert.ToInt32(age.TotalDays)} days old.";
            await message.Channel.SendMessageAsync(info);
        }

        private async Task AddReminder(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args[0] == "show")
            {
                string msg = "`Reminders`";
                var reminders = await _reminderService.GetAll();
                var remindersOrdered = reminders.OrderBy(r => r.TimeToRemind);

                foreach (var reminder in remindersOrdered)
                {
                    msg += $"{reminder.TimeToRemind.ToString("yyyy-MM-dd hh:mm:ss")}: {reminder.Message}";
                }
                await message.Channel.SendMessageAsync($"{msg}");
                return;
            }


            if (args.Count != 3)
                throw new BadSyntaxException();
            else
            {
                string reminderMessage = args[0].Trim('"');
                int numberOfTime = Convert.ToInt32(args[1]);
                numberOfTime = numberOfTime < 0 ? 0 : numberOfTime;
                string timeType = args[2].ToLower();
                var timeToRemind = timeType.StartsWith("second") ? DateTimeOffset.Now.AddSeconds(numberOfTime)
                    : timeType.StartsWith("minute") ? DateTimeOffset.Now.AddMinutes(numberOfTime)
                    : timeType.StartsWith("hour") ? DateTimeOffset.Now.AddHours(numberOfTime)
                    : timeType.StartsWith("day") ? DateTimeOffset.Now.AddDays(numberOfTime)
                    : DateTimeOffset.Now;

                await _reminderService.Add(message.Author.Mention, reminderMessage, timeToRemind, message.Channel.Id);
                await message.Channel.SendMessageAsync($@"Added reminder for UTC time: {timeToRemind.UtcDateTime}");
            }
        }

        private async Task Help(DiscordSocketClient client, SocketMessage message, List<string> args)
        {

            var rand = new Random().Next(15);
            if (rand == 0)
            {
                await message.Channel.SendMessageAsync("FUCK you I'm not helping you");
                await Task.Delay(TimeSpan.FromSeconds(3));
                await message.Channel.SendMessageAsync("just kidding");
            }
            var helpLines = new List<string>();
            foreach (var command in _commands.Where(c => c.Hidden == false))
            {
                string desc = string.IsNullOrEmpty(command.Description) ? string.Empty : $" Desc: {command.Description}";
                string syntax = string.IsNullOrEmpty(command.Description) ? string.Empty : $" Syntax: {command.Syntax}";
                helpLines.Add($"**.{command.Name}** \t{desc}{syntax}");
            }

            await SendPagedEmbeds(message.Channel, "List of commands available", helpLines);

            var customCommands = await _mappingService.GetAll();
            var customCommandNames = customCommands.Keys.ToList();
            if (customCommandNames.Count == 0)
                await message.SendRichEmbedMessage("Custom commands", "None added yet. Add one with `.add \"key\" \"value\"`");
            else
                await SendPagedEmbeds(message.Channel, "Custom commands", new[] { string.Join(", ", customCommandNames) });
        }

        private async Task ShowPatchNotes(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            bool showAll = args.Count == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase);
            if (args.Count > 1 || (args.Count == 1 && !showAll)) throw new BadSyntaxException();

            var notes = showAll ? PatchNotes.All : PatchNotes.All.Take(10).ToList();
            await SendPagedEmbeds(message.Channel, showAll ? "Patch notes" : "Recent patch notes", notes.Select(n => $"**{n.Date}** · {n.Text}"));
        }

        // Discord caps an embed at 6000 characters, so long lists go out as several embeds (one message each)
        private static async Task SendPagedEmbeds(IMessageChannel channel, string title, IEnumerable<string> lines)
        {
            var pages = TextPaginator.Paginate(lines);
            for (int i = 0; i < pages.Count; i++)
            {
                var embed = new EmbedBuilder().WithColor(Color.DarkPurple);
                for (int f = 0; f < pages[i].Count; f++)
                {
                    string fieldName = (i == 0 && f == 0) ? title : "⠀";
                    embed.AddField(fieldName, pages[i][f]);
                }
                await channel.SendMessageAsync(embed: embed.Build());
            }
        }

        private async Task Roll(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            int rand;
            if (args.Count == 0)
            {
                rand = new Random().Next(0, 101);
                await message.Channel.SendMessageAsync($"You rolled {rand}");
                return;
            }

            if (args.Count != 2)
                throw new BadSyntaxException();

            if (int.TryParse(args[0], out int num1) == false)
                throw new BadSyntaxException();
            if (int.TryParse(args[1], out int num2) == false)
                throw new BadSyntaxException();
            if (num1 > num2)
                throw new BadSyntaxException();

            rand = new Random().Next(num1, num2 + 1);

            await message.SendRichEmbedMessage($"You rolled {rand}");
        }

        private async Task LeaderboardWithHints(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            await Leaderboard(client, message, args, showHints: true);
        }


        private async Task LeaderboardWithoutHints(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            await Leaderboard(client, message, args, showHints: false);
        }

        private async Task Leaderboard(DiscordSocketClient client, SocketMessage message, List<string> args, bool showHints = true)
        {
            var accounts = _coinService.GetAll();
            string output = "";
            int? prestige = null;
            bool prestigeDisabled = _appSettings.PrestigeDisabled;
            double TotalWorth(CoinAccount a) => a.NetWorth + _stockService.GetPortfolioValue(a.UserId) + _coinService.PropertyValue(a) - a.Debt; //cash + stocks + property - debt
            var ordered = prestigeDisabled
                ? accounts.Accounts.OrderByDescending(TotalWorth)
                : accounts.Accounts.OrderByDescending(a => a.PrestigeLevel).ThenByDescending(TotalWorth);
            foreach (var account in ordered)
            {
                if (!prestigeDisabled && prestige != null && account.PrestigeLevel < prestige)
                    output += "\n"; //line between each prestige lvl

                prestige = account.PrestigeLevel;

                string prestigeDisplay = prestigeDisabled || prestige == 0 ? "" : $"**[P{prestige}]** ";
                output += $"**{account.Name}**: {prestigeDisplay}${FormatHelper.GetCommaNumber(TotalWorth(account))}";

                if (account.MostRecentDateBonusMet == DateTime.UtcNow.ToString("yyyyMMdd"))
                    output += "   \\*";
                output += "\n";

                //what the total is made of: cash, stocks held and property
                var parts = new List<string> { $"💵 ${FormatHelper.GetCommaNumber(account.NetWorth)}" };
                double stocksValue = _stockService.GetPortfolioValue(account.UserId);
                if (stocksValue > 0)
                    parts.Add($"📈 ${FormatHelper.GetCommaNumber(stocksValue)}");
                double propertyValue = _coinService.PropertyValue(account);
                if (propertyValue > 0)
                    parts.Add($"🏠 ${FormatHelper.GetCommaNumber(propertyValue)}");
                if (account.Debt > 0)
                    parts.Add($"💳 -${FormatHelper.GetCommaNumber(account.Debt)}");
                output += $"  ↳ {string.Join(" · ", parts)}\n";
            }
            if (showHints && prestigeDisabled)
            {
                output += $"\n*Each day you will get $1000. If you bet over 50% in a single bet that day you will also get an hourly bonus of {Constants.InterestPercentage}% net worth each hour for the rest of the day (UTC). Your leaderboard entry will show \\* symbol if you are currently receiving the bonus.*\n";
                output += "\n*The more money you win from playing in a day, the more money you make from further wins (via a bonus), up to a maxmium of 3x winnings bonus.*";
            }
            else if (showHints)
            {
                output += $"\n*Each day you will get $1000 x P level. If you bet over 50% in a single bet that day, and bet a minimum of P level bets you will also get an hourly bonus $1000 x P level + (up to) {Constants.InterestPercentage}% net worth each hour for the rest of the day (UTC). Your leaderboard entry will show \\* symbol if you are currently receiving the bonus.*\n";
                output += "\n*The more money you win from playing in a day, the more money you make from further wins (via a bonus), up to a maxmium of 3x winnings bonus.*\n";
                output += $"\n*Type .prestige to level up your account if you have enough money (it will reset your money to ${FormatHelper.GetCommaNumber(_startingAmount)}). People who are lower prestige than you cannot donate to you.*";
            }

            await message.SendRichEmbedMessage("Leaderboard", output);
        }

        private async Task ArchiveLeaderboard(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            string fileNamePrefix = "leaderboard_season", fileNameExtension = ".json";
            string fileNamePattern = "leaderboard_season*.json";
            var fileNames = Directory.GetFiles(Directory.GetCurrentDirectory(), fileNamePattern);
            int i = 1;
            if (fileNames.Any())
            {
                string latestFileNameFullPath = fileNames.OrderByDescending(fn => fn).First();
                string latestFileName = Path.GetFileName(latestFileNameFullPath);
                i = 1 + int.Parse(latestFileName.Replace(fileNamePrefix, "").Replace(fileNameExtension, ""));
            }
            string fileName = fileNamePattern.Replace("*", i.ToString());
            string path = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            var accounts = _coinService.GetAll();
            File.WriteAllText(path, JsonConvert.SerializeObject(accounts));

            await message.Channel.SendMessageAsync($"Season {i} archived.");
        }

        private async Task Prestige(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (_appSettings.PrestigeDisabled)
                throw new BadInputException("Prestige is disabled.");

            if (_betManager.InitiatedBetUserIds.Contains(message.Author.Id))
                throw new BadInputException("Hey **FUCK HEAD**! DON'T TRY TO CHEAT THE SYSTEM! You can't FUCKING prestige while you still have a bet in progress!");

            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            double amountForNextLevel = account.GetAmountRequiredForNextLevel();

            if (account.NetWorth < amountForNextLevel)
                throw new BadInputException($"You need at least ${FormatHelper.GetCommaNumber(amountForNextLevel)} to prestige to level {account.PrestigeLevel + 1}. You have ${FormatHelper.GetCommaNumber(account.NetWorth)}.");

            account.PrestigeLevel += 1;
            account.NetWorth = _startingAmount;
            account.NetWinningsToday = 0;
            await _coinService.Update(account.UserId, account.NetWorth, account.Name);

            await message.Channel.SendMessageAsync($"{message.Author.Mention} you have prestiged to level {account.PrestigeLevel} and your networth has been reset to {_startingAmount}.");
        }

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

            //the chart goes first, so it still arrives before the text if that gets split into more than one message
            await SendMarketChart(message, market.Stocks);

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

            await message.SendRichEmbedMessage("Stock Market", output);
        }

        private static string StockRow(Stock stock, string lead) =>
            $"{lead} **{stock.Symbol}** ${stock.Price:N2} ({stock.PercentChange:+0.00;-0.00;0.00}% last tick | {stock.AllTimeChange:+0.00;-0.00;0.00}% all time)\n";

        /// <summary>
        /// Every stock's % change since its first kept tick, as one panel per stock in a single image (two per row), so
        /// prices of very different sizes are comparable. Each panel is labelled with its ticker.
        /// Stocks with fewer than two ticks have nothing to draw and are left out.
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
                    charts.Add(StockChart.RenderMultiPng(series, TimeSpan.FromMinutes(5), v => $"{v:+0;-0;0}%", StockChart.ColourForChange(stock.AllTimeChange)));
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

        private static readonly TimeSpan _stockTick = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Graph of one stock. <paramref name="window"/> limits it to the most recent stretch of history; a window longer than
        /// the history kept shows all of it. The line colour is always the all-time change, not the window's.
        /// </summary>
        private static async Task StockGraph(SocketMessage message, Stock stock, TimeSpan? window = null)
        {
            IReadOnlyList<double> history = stock.History;
            if (history.Count < 2)
            {
                await message.SendRichEmbedMessage(stock.Name, "Not enough price history yet. Check back after a few ticks.");
                return;
            }

            string windowNote = "";
            if (window.HasValue)
            {
                int points = (int)Math.Max(2, window.Value.Ticks / _stockTick.Ticks + 1);
                if (points < history.Count)
                    history = history.Skip(history.Count - points).ToList();
                else if (points > history.Count)
                    windowNote = $" (only {StockChart.FormatSpan(history.Count, _stockTick)} of history so far)";
            }

            double first = history[0];
            double last = history[history.Count - 1];
            double windowChange = first <= 0 ? 0 : (last - first) / first * 100;

            string title = stock.TypeEmoji == null ? stock.Name : $"{stock.TypeEmoji} {stock.Name}";
            string output = $"{title} ({stock.KindLabel}): ${stock.Price:N2}\n" +
                $"{windowChange:+0.00;-0.00;0.00}% over the last {StockChart.FormatSpan(history.Count, _stockTick)}{windowNote} | low ${history.Min():N2} | high ${history.Max():N2}\n" +
                $"All time {stock.AllTimeChange:+0.00;-0.00;0.00}% (launched at ${stock.BasePrice:N2}), line is {(stock.AllTimeChange >= 0 ? "green" : "red")} for that.\n" +
                "*Each point is one 5 minute price tick, newest on the right. Add a time like `48h` to change the window.*";

            using var chart = StockChart.RenderPng(history, _stockTick, StockChart.ColourForChange(stock.AllTimeChange));
            var embed = DiscordHelper.GetEmbedBuilder($"{stock.Symbol} price history", output)
                .WithImageUrl("attachment://stock.png")
                .Build();
            await message.Channel.SendFileAsync(chart, "stock.png", embed: embed);
        }

        private async Task BuyStock(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 2) throw new BadSyntaxException();
            if (_betManager.InitiatedBetUserIds.Contains(message.Author.Id))
                throw new BadInputException("Finish your bet before trading stocks.");

            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            var (stock, shares, cost) = _stockService.Buy(account, args[0], args[1]);

            await message.Channel.SendMessageAsync($"{message.Author.Mention} bought {shares} share(s) of {stock.Symbol} at an average ${cost / shares:N2} for ${FormatHelper.GetCommaNumber(cost)}. {stock.Symbol} is now ${stock.Price:N2}. Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.");
        }

        private async Task SellStock(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 2) throw new BadSyntaxException();
            if (_betManager.InitiatedBetUserIds.Contains(message.Author.Id))
                throw new BadInputException("Finish your bet before trading stocks.");

            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            var (stock, shares, gross, tax) = _stockService.Sell(account, args[0], args[1]);

            await message.Channel.SendMessageAsync($"{message.Author.Mention} sold {shares} share(s) of {stock.Symbol} at an average ${gross / shares:N2} for ${FormatHelper.GetCommaNumber(gross)}, less ${FormatHelper.GetCommaNumber(tax)} sell tax (goes to the jackpot). {stock.Symbol} is now ${stock.Price:N2}. Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.");
        }

        private async Task Property(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var data = _coinService.GetAll();
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            double index = data.PropertyIndex;

            string output = $"The market is {(data.PropertyTrendDirection > 0 ? "rising" : "falling")}. Prices move about 0.5% a day at most, and the market runs in booms and busts.\n\n";
            foreach (var kind in new[] { PropertyKind.House, PropertyKind.Apartment })
            {
                output += $"🏠 **{PropertyCatalog.DisplayName(kind)}**: ${FormatHelper.GetCommaNumber(PropertyCatalog.Price(kind, index))} " +
                    $"(${FormatHelper.GetCommaNumber(PropertyCatalog.BuyCost(kind, index))} to buy with stamp duty), " +
                    $"worth ${FormatHelper.GetCommaNumber(PropertyCatalog.SaleValue(kind, index))} to sell, rent {PropertyCatalog.RentPerHour(kind, index):N2} an hour\n";
            }

            output += "\n**Your property**\n";
            if (account.Properties.Count == 0)
                output += "None yet. Buy one with `.buyproperty house` or `.buyproperty apartment`.\n";
            for (int i = 0; i < account.Properties.Count; i++)
            {
                var property = account.Properties[i];
                double netRentPerHour = PropertyCatalog.GrossRentPerHour(property.Kind, index) * (1 - PropertyCatalog.AgencyFeeRate);
                output += $"{i + 1}. {PropertyCatalog.DisplayName(property.Kind)} · worth ${FormatHelper.GetCommaNumber(PropertyCatalog.SaleValue(property.Kind, index))} · " +
                    $"rented out, about {netRentPerHour:N2} an hour after the agency\n";
            }

            output += "\nRent goes to your cash each hour after the agency's 8% and a 4% vacancy allowance. Rates and maintenance (1.5% of value a year) come out daily. " +
                "Buying costs 4.5% stamp duty and conveyancing, and selling costs 3%.\n" +
                "Every property is rented out automatically. `.sellproperty N` sells.";
            await message.SendRichEmbedMessage("Property", output);
        }

        private async Task BuyProperty(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 1 || !TryParsePropertyKind(args[0], out PropertyKind kind)) throw new BadSyntaxException();
            EnsureNotBettingForProperty(message);

            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            double price = PropertyCatalog.Price(kind, _coinService.GetAll().PropertyIndex);
            var (_, cost) = await _propertyService.Buy(account, kind);

            await message.Channel.SendMessageAsync($"{message.Author.Mention} bought a {PropertyCatalog.DisplayName(kind).ToLower()} for ${FormatHelper.GetCommaNumber(price)}, " +
                $"${FormatHelper.GetCommaNumber(cost - price)} stamp duty and conveyancing included in ${FormatHelper.GetCommaNumber(cost)} total. " +
                $"Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}. It's rented out straight away, and rent comes in every hour.");
        }

        private async Task SellProperty(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 1 || !int.TryParse(args[0], out int number)) throw new BadSyntaxException();
            EnsureNotBettingForProperty(message);

            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            var (holding, proceeds) = await _propertyService.Sell(account, number);

            await message.Channel.SendMessageAsync($"{message.Author.Mention} sold your {PropertyCatalog.DisplayName(holding.Kind).ToLower()} for ${FormatHelper.GetCommaNumber(proceeds)} after agent commission and marketing. " +
                $"Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.");
        }

        /// <summary>Old .letout / .unlet: every property is rented out automatically now.</summary>
        private async Task RentingIsAutomatic(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            await message.Channel.SendMessageAsync("Every property is rented out automatically now, so there's nothing to switch. See `.property` for your rent.");
        }

        private static bool TryParsePropertyKind(string text, out PropertyKind kind)
        {
            switch (text.ToLowerInvariant())
            {
                case "house": kind = PropertyKind.House; return true;
                case "apartment": case "apt": kind = PropertyKind.Apartment; return true;
                default: kind = default; return false;
            }
        }

        private void EnsureNotBettingForProperty(SocketMessage message)
        {
            if (_betManager.InitiatedBetUserIds.Contains(message.Author.Id))
                throw new BadInputException("Finish your bet before buying or selling property.");
        }

        private void EnsureNotBetting(SocketMessage message)
        {
            if (_betManager.InitiatedBetUserIds.Contains(message.Author.Id))
                throw new BadInputException("Finish your bet before trading stocks.");
        }

        private async Task ShortStock(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 2) throw new BadSyntaxException();
            EnsureNotBetting(message);

            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            var (stock, shares, margin, tax) = _stockService.Short(account, args[0], args[1]);

            await message.Channel.SendMessageAsync($"{message.Author.Mention} shorted {shares} share(s) of {stock.Symbol}, locking ${FormatHelper.GetCommaNumber(margin)} margin and ${FormatHelper.GetCommaNumber(tax)} sale tax (goes to the jackpot). You profit if it falls and lose margin as it rises, and at 2x the entry price you're wiped out. {stock.Symbol} is now ${stock.Price:N2}. Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.");
        }

        private async Task CoverStock(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 2) throw new BadSyntaxException();
            EnsureNotBetting(message);

            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            var (stock, shares, returned, profit) = _stockService.Cover(account, args[0], args[1]);

            await message.Channel.SendMessageAsync($"{message.Author.Mention} covered {shares} share(s) of {stock.Symbol} and got back ${FormatHelper.GetCommaNumber(returned)} ({SignedMoney(profit)}). {stock.Symbol} is now ${stock.Price:N2}. Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.");
        }

        private Task CallOption(DiscordSocketClient client, SocketMessage message, List<string> args) => BuyOptionCommand(message, args, isCall: true);

        private Task PutOption(DiscordSocketClient client, SocketMessage message, List<string> args) => BuyOptionCommand(message, args, isCall: false);

        private async Task BuyOptionCommand(SocketMessage message, List<string> args, bool isCall)
        {
            if (args.Count != 3 || !DurationParser.TryParse(args[2], out TimeSpan duration))
                throw new BadSyntaxException();
            bool useMax = args[1].Equals("max", StringComparison.OrdinalIgnoreCase);
            if (!useMax && (!int.TryParse(args[1], out int typed) || typed < 1))
                throw new BadSyntaxException();
            EnsureNotBetting(message);

            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            int contracts = useMax ? _stockService.MaxAffordableContracts(account, args[0], duration) : int.Parse(args[1]);
            var (option, premium, fee) = _stockService.BuyOption(account, args[0], isCall, contracts, duration);

            string kind = isCall ? "call" : "put";
            string wins = isCall ? "rises above" : "falls below";
            await message.Channel.SendMessageAsync($"{message.Author.Mention} bought {contracts} {option.Symbol} {kind} option(s) with strike ${option.Strike:N2} for ${FormatHelper.GetCommaNumber(premium)} plus ${FormatHelper.GetCommaNumber(fee)} fee (goes to the jackpot). Each is 100 shares and pays out only if {option.Symbol} {wins} the strike by expiry in {FormatRemaining(duration)}. Settled on the fair price, not on trades. Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.");
        }

        private async Task Options(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 0) throw new BadSyntaxException();

            var market = _stockService.GetMarket();
            var open = market.GetOptions(message.Author.Id).OrderBy(o => o.ExpiresAtUtcTicks).ToList();
            market.RecentSettled.TryGetValue(message.Author.Id, out var recent);

            if (open.Count == 0 && (recent == null || recent.Count == 0))
            {
                await message.Channel.SendMessageAsync($"{message.Author.Mention} you have no options. Try `.call SYMBOL 1 6h` or `.put SYMBOL 1 6h`.");
                return;
            }

            string output = "";
            if (open.Count > 0)
            {
                output += "**Open**\n";
                foreach (var option in open)
                {
                    double fair = market.Find(option.Symbol)?.FairPrice ?? option.Strike;
                    output += $"{(option.IsCall ? "📈 call" : "📉 put")} {option.Contracts}x **{option.Symbol}** strike ${option.Strike:N2} · expires in {FormatRemaining(option.ExpiresAt - DateTime.UtcNow)} · worth ${FormatHelper.GetCommaNumber(option.PayoutAt(fair))} now (paid ${FormatHelper.GetCommaNumber(option.Premium)})\n";
                }
            }
            if (recent != null && recent.Count > 0)
            {
                output += "\n**Recently settled**\n" + string.Join("\n", recent);
            }

            await message.SendRichEmbedMessage($"{message.Author.Username}'s options", output);
        }

        private static string FormatRemaining(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{(int)Math.Ceiling(span.TotalMinutes)}m";
        }

        //circles matching StockChart.SeriesColors, in the same order
        private static readonly string[] _seriesDots = { "🔴", "🟠", "🟡", "🟢", "🔵", "🟣", "⚪", "🟤" };

        private async Task NetWorthGraph(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 1) throw new BadSyntaxException();
            if (args.Count == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                await NetWorthGraphAll(message);
                return;
            }

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

            if (account.NetWorthHistory.Count == 0)
            {
                await message.SendRichEmbedMessage($"{account.Name} net worth", "Not enough history yet. The graph is sampled once an hour, so check back later.");
                return;
            }

            //history is sampled hourly, so end the line at the current value
            double current = account.NetWorth + _stockService.GetPortfolioValue(account.UserId) + _coinService.PropertyValue(account) - account.Debt;
            var points = account.NetWorthHistory.Append(current).ToList();
            TimeSpan interval = TimeSpan.FromHours(1);

            double first = points[0];
            double change = first == 0 ? 0 : (current - first) / first * 100;
            string output = $"Net worth (cash + stocks + property): ${FormatHelper.GetCommaNumber(current)}\n" +
                $"{change:+0.00;-0.00;0.00}% over the last {StockChart.FormatSpan(points.Count, interval)} | low ${FormatHelper.GetCommaNumber(points.Min())} | high ${FormatHelper.GetCommaNumber(points.Max())}";

            using var chart = StockChart.RenderPng(points, interval);
            var embed = DiscordHelper.GetEmbedBuilder($"{account.Name} net worth", output)
                .WithImageUrl("attachment://networth.png")
                .Build();
            await message.Channel.SendFileAsync(chart, "networth.png", embed: embed);
        }

        /// <summary>Every account's net worth on one chart, each in its own colour, with a legend ranked by current worth.</summary>
        private async Task NetWorthGraphAll(SocketMessage message)
        {
            var ranked = _coinService.GetAll().Accounts
                .Select(a => (Account: a, Current: a.NetWorth + _stockService.GetPortfolioValue(a.UserId) + _coinService.PropertyValue(a) - a.Debt))
                .OrderByDescending(x => x.Current)
                .ToList();
            if (ranked.Count == 0 || ranked.All(x => x.Account.NetWorthHistory.Count == 0))
            {
                await message.SendRichEmbedMessage("Everyone's net worth", "Not enough history yet. The graph is sampled once an hour, so check back later.");
                return;
            }

            //each line ends at the current value, like the single account graph
            var series = ranked.Select(x => x.Account.NetWorthHistory.Append(x.Current).ToList()).ToList();

            var lines = new List<string>();
            for (int i = 0; i < ranked.Count; i++)
            {
                var (account, current) = ranked[i];
                double first = series[i][0];
                double change = first == 0 ? 0 : (current - first) / first * 100;
                string dot = _seriesDots[i % _seriesDots.Length];
                lines.Add($"{dot} **{account.Name}** ${FormatHelper.GetCommaNumber(current)} ({change:+0.00;-0.00;0.00}%)");
            }

            string output = $"Net worth (cash + stocks), newest on the right. % is change over each person's own history.\n" +
                string.Join("\n", lines);
            if (ranked.Count > _seriesDots.Length)
                output += $"\n*Colours repeat after {_seriesDots.Length} people.*";

            using var chart = StockChart.RenderMultiPng(series, TimeSpan.FromHours(1));
            var embed = DiscordHelper.GetEmbedBuilder("Everyone's net worth", output)
                .WithImageUrl("attachment://networth.png")
                .Build();
            await message.Channel.SendFileAsync(chart, "networth.png", embed: embed);
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
                output += $"{trend} **{holding.Key}** {holding.Value} sh · now ${stock.Price:N2} = ${FormatHelper.GetCommaNumber(stock.Price * holding.Value)}\n";

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
                output += $"{(shortGain >= 0 ? "📈" : "📉")} **{held.Key}** short {position.Shares} sh · sold @ ${position.EntryPrice:N2}, now ${stock.Price:N2} · margin ${FormatHelper.GetCommaNumber(position.Margin)} · {SignedMoney(shortGain)}\n";
            }
            foreach (var option in options)
            {
                double fair = market.Find(option.Symbol)?.FairPrice ?? option.Strike;
                output += $"🎟️ {option.Contracts}x **{option.Symbol}** {(option.IsCall ? "call" : "put")} strike ${option.Strike:N2} · {FormatRemaining(option.ExpiresAt - DateTime.UtcNow)} left · worth ${FormatHelper.GetCommaNumber(option.PayoutAt(fair))}\n";
            }

            var (totalGain, totalCost) = market.GetUnrealisedGain(account.UserId);
            double stocksValue = _stockService.GetPortfolioValue(account.UserId);
            double propertyValue = _coinService.PropertyValue(account);
            double index = _coinService.GetAll().PropertyIndex;

            output += "\n**Everything you own**\n";
            output += $"💵 Cash ${FormatHelper.GetCommaNumber(account.NetWorth)}\n";
            if (holdings.Count > 0 || shorts.Count > 0 || options.Count > 0)
                output += $"📈 Stocks and positions ${FormatHelper.GetCommaNumber(stocksValue)}\n";
            foreach (var property in account.Properties)
                output += $"🏠 {PropertyCatalog.DisplayName(property.Kind)} (bought ${FormatHelper.GetCommaNumber(property.PurchaseCost)}) · worth ${FormatHelper.GetCommaNumber(PropertyCatalog.SaleValue(property.Kind, index))} after selling costs · rented out\n";
            if (account.Properties.Count > 0)
                output += $"🏘️ Property value ${FormatHelper.GetCommaNumber(propertyValue)}\n";
            if (account.Debt > 0)
                output += $"💳 Debt -${FormatHelper.GetCommaNumber(account.Debt)} (grows 1% a day; paid off from any spare cash, and sales pay it off first)\n";
            output += $"🧾 Total ${FormatHelper.GetCommaNumber(account.NetWorth + stocksValue + propertyValue - account.Debt)}\n";
            if (holdings.Count > 0)
            {
                output += totalCost > 0
                    ? $"📊 Stock gain/loss {SignedMoney(totalGain)} ({totalGain / totalCost * 100:+0.00;-0.00;0.00}%) on known buy prices"
                    : "📊 Stock gain/loss n/a, no known buy prices";
            }

            await SendPagedEmbeds(message.Channel, $"{account.Name}'s portfolio", output.Split('\n'));
        }

        private static string SignedMoney(double amount) =>
            $"{(amount < 0 ? "-" : "+")}${FormatHelper.GetCommaNumber(Math.Abs(amount))}";

        private async Task Donate(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 2 || !DiscordHelper.TryGetUserId(args[0], out ulong userIdToDonateTo))
                throw new BadSyntaxException();

            ulong userId = message.Author.Id;

            if (userId == userIdToDonateTo)
                throw new BadInputException("You can't **FUCKING** donate to yourself you stupid PIECE OF SHIT");

            if (client.GetUser(userIdToDonateTo).IsBot)
                throw new BadInputException("You STUPID *CUNT*! YOU CAN'T DONATE TO A FUCKING BOT!");


            if (!double.TryParse(args[1], out double donationAmount))
                throw new BadSyntaxException();

            if (donationAmount <= 0)
                throw new BadInputException("Can't donate 0 or less than 0 DUMBASS");

            CoinAccount account = await _coinService.Get(userId, message.Author.Username);
            CoinAccount donationReceiverAccount = await _coinService.Get(userIdToDonateTo, client.GetUser(userIdToDonateTo).Username);

            if (!_appSettings.PrestigeDisabled && donationReceiverAccount.PrestigeLevel > account.PrestigeLevel)
                throw new BadInputException($"Can't donate to someone with a higher prestige level than you. You are level {account.PrestigeLevel} and they are {donationReceiverAccount.PrestigeLevel}.");

            if (donationAmount > account.NetWorth)
                throw new BadInputException($"Can't donate more than you have ({FormatHelper.GetCommaNumber(account.NetWorth)})... SCHYUPID IDIOT");

            account.NetWorth -= donationAmount;
            donationReceiverAccount.NetWorth += donationAmount * 0.8;
            _coinService.AddToJackpot(donationAmount * 0.2); //the 20% tax goes to the jackpot

            //statsfor donater
            account.Stats.TotalMoneyDonated += donationAmount;
            if (donationAmount > account.Stats.MaxMoneyDonatedAtOnce)
                account.Stats.MaxMoneyDonatedAtOnce = donationAmount;

            if (account.Stats.DonationAmountsToDict.ContainsKey(userIdToDonateTo))
                account.Stats.DonationAmountsToDict[userIdToDonateTo] += donationAmount;
            else
                account.Stats.DonationAmountsToDict.Add(userIdToDonateTo, donationAmount);

            //stats for donatee
            donationReceiverAccount.Stats.TotalMoneyReceivedFromDonations += donationAmount;
            if (donationAmount > donationReceiverAccount.Stats.MaxMoneyReceivedFromDonationAtOnce)
                donationReceiverAccount.Stats.MaxMoneyReceivedFromDonationAtOnce = donationAmount;

            if (donationReceiverAccount.Stats.DonationAmountsFromDict.ContainsKey(userId))
                donationReceiverAccount.Stats.DonationAmountsFromDict[userId] += donationAmount;
            else
                donationReceiverAccount.Stats.DonationAmountsFromDict.Add(userId, donationAmount);

            string output = "";
            int chance = 12, multiplier = 6;
            int rand = new Random().Next(0, chance);
            if (rand == 0)
            {
                double returnAmount = multiplier * donationAmount;
                output += $"*You encountered a rare bonus reward for donating. You get back ${FormatHelper.GetCommaNumber(returnAmount)}.*\n";
                account.NetWorth += returnAmount;

                //stats for donation bonus
                account.Stats.DonationBonusesEncountered += 1;
            }

            output += $"{message.Author.Mention} you donated {FormatHelper.GetCommaNumber(donationAmount)} to {args[0]} (minus 20% tax, which goes to the jackpot)." +
            $"\n`Net worth:`" +
            $"\n{account.Name}: ${FormatHelper.GetCommaNumber(account.NetWorth)}" +
            $"\n{donationReceiverAccount.Name}: ${FormatHelper.GetCommaNumber(donationReceiverAccount.NetWorth)}";

            await _coinService.Update(account.UserId, account.NetWorth, message.Author.Username);
            await _coinService.Update(donationReceiverAccount.UserId, donationReceiverAccount.NetWorth, donationReceiverAccount.Name);

            await message.Channel.SendMessageAsync(output);
        }

        private async Task PowerUp(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var now = DateTime.UtcNow;
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            account.Prune(now);

            if (args.Count >= 1 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                string catalog = string.Join("\n", PowerUpCatalog.All.Select(p => $"**{p.Name}**: {p.Description}"));
                await message.Channel.SendMessageAsync($"Power-ups the raffle can give:\n{catalog}");
                return;
            }

            if (args.Count >= 1 && args[0].Equals("use", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count != 2 || !int.TryParse(args[1], out int number))
                    throw new BadSyntaxException();
                if (_betManager.InitiatedBetUserIds.Contains(message.Author.Id))
                    throw new BadInputException("Finish your bet before using a power-up.");

                string result = await _raffleService.Use(account, number, now, new Random().NextDouble);
                await message.Channel.SendMessageAsync($"{message.Author.Mention} {result}");
                return;
            }

            if (args.Count != 0) throw new BadSyntaxException();

            string output = "";
            string untilDraw = FormatRemaining(RaffleService.NextRaffleUtc(now) - now);
            output += RaffleService.IsEnteredInNextDraw(account, now)
                ? $"🎟️ **You're entered** in the raffle in {untilDraw}.\n"
                : $"🎟️ **You're not entered** in the raffle in {untilDraw}. Do something with money (bets, trades, donations) to be entered.\n";
            if (account.HeldPowerUps.Count == 0)
                output += "You have no power-ups.\n";
            else
            {
                output += "**Your power-ups** (use one with `.pu use <number>`):\n";
                for (int i = 0; i < account.HeldPowerUps.Count; i++)
                {
                    var held = account.HeldPowerUps[i];
                    var power = PowerUpCatalog.Find(held.Id);
                    output += $"{i + 1}. **{power?.Name ?? held.Id}**: {power?.Description} (expires in {FormatRemaining(new DateTime(held.ExpiresAtTicks) - now)})\n";
                }
            }

            if (account.ActivePowerUps.Count > 0)
            {
                output += "**Running now:**\n";
                foreach (var active in account.ActivePowerUps)
                {
                    var power = PowerUpCatalog.Find(active.Id);
                    string left = active.ChargesLeft == ActivePowerUp.Unlimited
                        ? FormatRemaining(new DateTime(active.ExpiresAtTicks) - now) + " left"
                        : $"{active.ChargesLeft} use(s) left";
                    output += $"- {power?.Name ?? active.Id} ({left})\n";
                }
            }

            output += $"Next raffle in {untilDraw}.";
            await message.Channel.SendMessageAsync(output);
        }

        private Task Jackpot(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            return message.Channel.SendMessageAsync($"The jackpot is ${FormatHelper.GetCommaNumber(_coinService.JackpotPool)}. Every sell tax and donation tax goes in. " +
                $"Bet `max` on a game with all your money in cash (no stocks) and you have a {BetManager.JackpotWinChance:P0} chance to win it all.");
        }

        private async Task Start(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 0 && args[0].StartsWith("im"))
            {
                _imageSearchStopped = false;
                await message.Channel.SendMessageAsync("Image search enabled.");
                return;
            }

            await message.Channel.SendMessageAsync("I WAKE");
            _stopped = false;
        }

        private async Task Stop(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 0 && args[0].StartsWith("im"))
            {
                _imageSearchStopped = true;
                await message.Channel.SendMessageAsync("Image search disabled.");
                return;
            }

            await message.Channel.SendMessageAsync("I SLEEP");
            _stopped = true;
        }

        private async Task Time(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args[0] == "pst")
                await message.Channel.SendMessageAsync($"PST: {DateTime.UtcNow.AddHours(-7).ToString("yyyy-MM-dd HH:mm:ss")}");
            else if (args[0] == "aest")
                await message.Channel.SendMessageAsync($"AEST: {DateTime.UtcNow.AddHours(10).ToString("yyyy-MM-dd HH:mm:ss")}");
            else
                await message.Channel.SendMessageAsync($"UTC: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")}");
        }



        public async Task ImageSearch(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            await ImageSearch(message, args);
        }

        public async Task ImageSearchDbz(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            await ImageSearch(message, new List<string> { "dragon", "ball", "z" }, isAlwaysOn: true);
        }

        private async Task ImageSearch(SocketMessage message, List<string> args, bool isAlwaysOn = false)
        {
            if (_imageSearchStopped && !isAlwaysOn)
                return;

            ThrowIfBlackListed(message);

            var searchQuery = string.Join(' ', args);

            if (string.IsNullOrWhiteSpace(searchQuery))
                throw new BadInputException("You didn't provide an argument");

            try
            {
                var images = await _duckDuckGoService.GetImages(searchQuery);
                string output = images.Count > 0 ? images[new Random().Next(0, images.Count)]?.Image : "No image found";

                var msg = await message.Channel.SendMessageAsync(output);

                if (searchQuery.Contains("safeoff", StringComparison.OrdinalIgnoreCase))
                {
                    //delete nsfw messages after delay
                    _ = msg.DeleteAfterDelay();

                }
            }
            catch (Exception ex)
            {
                await message.Channel.SendMessageAsync(ex.Message);
            }
        }

        public void ThrowIfBlackListed(SocketMessage message)
        {
            if (_appSettings.BlackListedIds.Contains(message.Author.Id))
                throw new BadInputException("No");

            _appSettings.BlackListedWords.ForEach(bw =>
            {
                if (message.Content.Contains(bw))
                {
                    _appSettings.BlackListedIds.Add(message.Author.Id);
                    string json = File.ReadAllText("appsettings.json");
                    dynamic jsonObj = JsonConvert.DeserializeObject(json);
                    jsonObj["AppSettings"]["BlackListedIds"] = JsonConvert.SerializeObject(_appSettings.BlackListedIds);
                    string output = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
                    File.WriteAllText("appsettings.json", output);
                    throw new BadInputException("No");
                }
            });
        }

        public async Task FaceGenerate(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            using var stream = await _faceService.Run();
            await message.Channel.SendFileAsync(stream: stream, "image.jpg");
        }

        private const string CardDirectory = "card_images";

        public async Task CardPull(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 1 && args[0].StartsWith("keep"))
            {
                string keepPrefix = args[1];
                if (!ulong.TryParse(keepPrefix.Split('_')[0], out ulong userId)
                    || userId != message.Author.Id)
                {
                    await message.SendRichEmbedMessage($"You are not the owner of this card. FUCK OFF");
                    return;
                }

                CoinAccount ownerAccount = await _coinService.Get(message.Author.Id, message.Author.Username);
                await SaveCard(keepPrefix, ownerAccount, message);
                return;
            }

            CoinAccount coinAccount = await _coinService.Get(message.Author.Id, message.Author.Username);
            string today = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            bool isFreePull = coinAccount.LastFreePullDate != today;

            if (!isFreePull && coinAccount.NetWorth < BattlePerson.PullCost)
            {
                await message.SendRichEmbedMessage("Not enough money", $"A card pull costs ${FormatHelper.GetCommaNumber(BattlePerson.PullCost)}. Your first pull each day is free.");
                return;
            }

            //get the face before charging, so nobody pays when the image service is down
            byte[] faceBytes;
            using (var faceStream = await _faceService.Run())
            using (var buffer = new MemoryStream())
            {
                await faceStream.CopyToAsync(buffer);
                faceBytes = buffer.ToArray();
            }

            var battlePerson = new BattlePerson(NameGenerator.GetGeneratedName(2));
            if (battlePerson.Foil)
                faceBytes = FoilEffect.Apply(faceBytes); //foil cards get the holographic image, saved with the card
            string prefix = $"{message.Author.Id}_{battlePerson.Name.Replace(" ", "")}_{DateTimeOffset.Now.Ticks}";

            //the card is saved next to the image so 'keep' saves the card the player actually saw
            Directory.CreateDirectory(CardDirectory);
            string unsavedBase = Path.Combine(CardDirectory, $"{prefix}_unsaved");
            await File.WriteAllBytesAsync($"{unsavedBase}.jpg", faceBytes);
            await File.WriteAllTextAsync($"{unsavedBase}.json", JsonConvert.SerializeObject(battlePerson));

            if (isFreePull)
                coinAccount.LastFreePullDate = today;
            else
                coinAccount.NetWorth -= BattlePerson.PullCost;
            await _coinService.Update();

            _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(t => DeleteUnsavedCard(prefix));

            var embed = DiscordHelper.GetEmbedBuilder(battlePerson.Name, battlePerson.GetSummary()).Build();
            using (var image = new MemoryStream(faceBytes))
            {
                await message.Channel.SendFileAsync(stream: image, $"{prefix}_unsaved.jpg", embed: embed);
            }

            if (coinAccount.BattlePerson == null)
            {
                await SaveCard(prefix, coinAccount, message); //auto save the card if they don't have any card
            }
            else
            {
                string paidLine = isFreePull ? "Free daily pull used." : $"${FormatHelper.GetCommaNumber(BattlePerson.PullCost)} paid.";
                await message.Channel.SendMessageAsync($"{paidLine} Type the following if you want to replace your current card with the new one (you have 5 minutes before it is gone):");
                await message.Channel.SendMessageAsync($".cardpull keep {prefix}");
            }
        }

        public async Task CardView(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount coinAccount = await _coinService.Get(message.Author.Id, message.Author.Username);
            BattlePerson card = coinAccount.BattlePerson;
            if (card == null)
            {
                await message.SendRichEmbedMessage("No card", "You don't have a Battle Person yet. Type `.cardpull` to get one.");
                return;
            }

            var embed = DiscordHelper.GetEmbedBuilder(card.Name, card.GetSummary()).Build();
            if (!string.IsNullOrEmpty(card.FilePath) && File.Exists(card.FilePath))
            {
                using var image = File.OpenRead(card.FilePath);
                await message.Channel.SendFileAsync(stream: image, Path.GetFileName(card.FilePath), embed: embed);
            }
            else
            {
                await message.Channel.SendMessageAsync(embed: embed);
            }
        }

        private async Task SaveCard(string prefix, CoinAccount coinAccount, SocketMessage message)
        {
            //the prefix comes from the player's message, so it must be a plain file name
            if (string.IsNullOrWhiteSpace(prefix) || prefix != Path.GetFileName(prefix))
            {
                await message.SendRichEmbedMessage($"This Battle Person ID does not exist. It may have been deleted.");
                return;
            }

            string unsavedBase = Path.Combine(CardDirectory, $"{prefix}_unsaved");
            string unsavedJsonPath = $"{unsavedBase}.json";
            string unsavedImagePath = $"{unsavedBase}.jpg";
            if (!File.Exists(unsavedJsonPath) || !File.Exists(unsavedImagePath))
            {
                await message.SendRichEmbedMessage($"This Battle Person ID does not exist. It may have been deleted.");
                return;
            }

            BattlePerson battlePerson = JsonConvert.DeserializeObject<BattlePerson>(await File.ReadAllTextAsync(unsavedJsonPath));

            if (coinAccount.BattlePerson?.FilePath != null && File.Exists(coinAccount.BattlePerson.FilePath))
                File.Delete(coinAccount.BattlePerson.FilePath); //replacing the old card removes its image
            FaceOffRenderer.ForgetArt(coinAccount.BattlePerson?.FilePath);

            string savedImagePath = Path.Combine(CardDirectory, $"{prefix}.jpg");
            FaceOffRenderer.ForgetArt(savedImagePath);
            File.Move(unsavedImagePath, savedImagePath);
            File.Delete(unsavedJsonPath);

            battlePerson.FilePath = savedImagePath;
            coinAccount.BattlePerson = battlePerson;
            await _coinService.Update();
            await message.SendRichEmbedMessage($"New Battle Person saved: {battlePerson.Name}");
        }

        private static void DeleteUnsavedCard(string prefix)
        {
            string unsavedBase = Path.Combine(CardDirectory, $"{prefix}_unsaved");
            foreach (string path in new[] { $"{unsavedBase}.jpg", $"{unsavedBase}.json" })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private async Task Stats(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count() == 0)
                throw new BadSyntaxException();

            var accounts = _coinService.GetAll().Accounts;
            CoinAccount account;
            if (args[0].StartsWith("me"))
            {
                account = accounts.FirstOrDefault(a => a.UserId == message.Author.Id);
            }
            else if (DiscordHelper.TryGetUserId(args[0], out ulong userId))
            {
                account = accounts.FirstOrDefault(a => a.UserId == userId);
            }
            else
            {
                throw new BadSyntaxException();
            }

            if (account == null)
                throw new BadInputException($"Account not found.");

            var stats = account.Stats;
            string lastPropertyName = string.Empty;

            string title = $"{account.Name} stats";

            Type type = stats.GetType();
            PropertyInfo[] properties = type.GetProperties();
            string output = "";
            foreach (PropertyInfo property in properties)
            {
                if (property.PropertyType == typeof(Dictionary<ulong, double>))
                    continue;

                var value = property.GetValue(stats, index: null);
                var strValue = property.PropertyType == typeof(double) ? FormatHelper.GetCommaNumber((double)value) : value.ToString();

                string spacingToAdd = "";
                if (lastPropertyName == "Streak" && !property.Name.EndsWith("Streak"))
                {
                    spacingToAdd = "\n";
                }
                else if (lastPropertyName == "Max" && !property.Name.StartsWith("Max"))
                {
                    spacingToAdd = "\n";
                }
                else if (lastPropertyName == "Total" && !property.Name.StartsWith("Total"))
                {
                    spacingToAdd = "\n";
                }

                output += $"{spacingToAdd}**{property.Name.SplitCamelCaseWithSpace()}:**\t {strValue}\n";

                lastPropertyName = property.Name.StartsWith("Total") ? "Total"
                    : property.Name.StartsWith("Max") ? "Max"
                    : property.Name.EndsWith("Streak") ? "Streak"
                    : string.Empty;
            }

            await message.SendRichEmbedMessage(title, output);

            string dictOutput = "";
            foreach (PropertyInfo dictProperty in properties
                .Where(p => p.PropertyType == typeof(Dictionary<ulong, double>)))
            {
                var dict = (Dictionary<ulong, double>)dictProperty.GetValue(stats, index: null);
                var orderedDict = dict.OrderByDescending(kvp => kvp.Value).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                dictOutput += $"**{dictProperty.Name.SplitCamelCaseWithSpace()}:**\n";
                foreach (var kvp in orderedDict)
                {
                    string userName = client.GetUser(kvp.Key).Username;
                    dictOutput += $"{userName}: {FormatHelper.GetCommaNumber(kvp.Value)}\n";
                }
                dictOutput += "\n";
            }

            await message.SendRichEmbedMessage($"{account.Name} donation stats", dictOutput);
        }

        private async Task GetFreeSpace(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            string path = AppContext.BaseDirectory;
            var drive = new DriveInfo(path);
            double freeGB = drive.AvailableFreeSpace / 1024 / 1024 / 1024;
            await message.Channel.SendMessageAsync($"Free space: {freeGB} GB");
        }

        //private async Task Convo(DiscordSocketClient client, SocketMessage message, List<string> args)
        //{
        //    string? context = null;

        //    if (args.Count > 0)
        //        context = $"{message.Author.Username}:{args[0]}";

        //    string convo = await _gptService.GetConvo(context);
        //    await message.Channel.SendMessageAsync(convo);
        //}

        private async Task Convo(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var channel = message.Channel as IMessageChannel;
            if (channel == null)
                return;
            
            // Fetch the last 11 messages (to have 10 + the triggering one)
            var messages = await channel.GetMessagesAsync(limit: 30)
                .FlattenAsync();

            // Exclude the triggering message
            var filtered = messages
                .Where(m => m.Id != message.Id && !string.IsNullOrWhiteSpace(m.Content))
                .Where(f => !f.Author.IsBot);

            // Order oldest → newest
            var ordered = filtered.OrderBy(m => m.Timestamp);

            // Take the last 10 (in case there were more than 11 returned)
            var lastTen = ordered.TakeLast(10);

            // Format as "username: message"
            List<string> formattedMessages = lastTen
                .Select(m => $"{m.Author.Username}: {m.Content}")
                .ToList();

            // Call your convo service
            string convo = await _convoService.ProduceConvo(formattedMessages);

            await message.Channel.SendMessageAsync(convo);
        }

        private async Task Lang(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count == 0)
                throw new BadSyntaxException();

            TranslationResult result = await _translationService.TranslateThroughLanguages(string.Join(" ", args));

            string translated = result.Text;
            if (translated.Length > _messageCharLimit)
                translated = translated.Substring(0, _messageCharLimit);

            await message.Channel.SendMessageAsync(translated, allowedMentions: AllowedMentions.None);
        }

        private async Task Ban(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var target = message.MentionedUsers.FirstOrDefault();
            if (target == null)
                throw new BadSyntaxException("you have to mention someone to ban them");

            if (_adminIds.Contains(target.Id))
                throw new BadSyntaxException("you can't ban an admin");

            if (_bannedUserIds.Contains(target.Id))
            {
                await message.Channel.SendMessageAsync($"{target.Mention} is already banned");
                return;
            }

            await _banService.Save(_bannedUserIds.Concat(new[] { target.Id }));
            _bannedUserIds.Add(target.Id);
            await message.Channel.SendMessageAsync($"Banned {target.Mention}");
        }

        private async Task Unban(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var target = message.MentionedUsers.FirstOrDefault();
            if (target == null)
                throw new BadSyntaxException("you have to mention someone to unban them");

            if (!_bannedUserIds.Contains(target.Id))
            {
                await message.Channel.SendMessageAsync($"{target.Mention} is not banned");
                return;
            }

            await _banService.Save(_bannedUserIds.Where(id => id != target.Id));
            _bannedUserIds.Remove(target.Id);
            await message.Channel.SendMessageAsync($"Unbanned {target.Mention}");
        }
    }
}
