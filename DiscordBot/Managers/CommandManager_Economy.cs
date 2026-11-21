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
    /// <summary>Leaderboard, net worth graphs, donations, the jackpot and `.stats`.</summary>
    public partial class CommandManager
    {

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
            double TotalWorth(CoinAccount a) => _worth.TotalWorth(a);
            var ordered = _worth.Ranked();
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

        //circles matching StockChart.SeriesColors, in the same order
        private static readonly string[] _seriesDots = { "🔴", "🟠", "🟡", "🟢", "🔵", "🟣", "⚪", "🟤" };

        private static readonly TimeSpan _netWorthSample = TimeSpan.FromHours(1);

        /// <summary>
        /// The newest points covering <paramref name="window"/> (all of them when null), as the stock graphs do. Sets
        /// <paramref name="note"/> when there isn't that much history yet.
        /// </summary>
        public static List<double> TakeWindow(List<double> points, TimeSpan? window, TimeSpan interval, out string note)
        {
            note = "";
            if (!window.HasValue) return points;
            int wanted = (int)Math.Max(2, window.Value.Ticks / interval.Ticks + 1);
            if (wanted < points.Count) return points.Skip(points.Count - wanted).ToList();
            if (wanted > points.Count) note = $" (only {StockChart.FormatSpan(points.Count, interval)} of history so far)";
            return points;
        }

        private async Task NetWorthGraph(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            //an optional time window like 48h or 7d goes last, as on .stocks
            TimeSpan? window = null;
            if (args.Count > 0 && DurationParser.TryParse(args[args.Count - 1], out TimeSpan parsed))
            {
                window = parsed;
                args = args.Take(args.Count - 1).ToList();
            }

            if (args.Count > 1) throw new BadSyntaxException();
            if (args.Count == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                await NetWorthGraphAll(message, window);
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
            TimeSpan interval = _netWorthSample;
            var points = TakeWindow(account.NetWorthHistory.Append(current).ToList(), window, interval, out string windowNote);

            double first = points[0];
            double change = first == 0 ? 0 : (current - first) / first * 100;
            string output = $"Net worth (cash + stocks + property): ${FormatHelper.GetCommaNumber(current)}\n" +
                $"{change:+0.00;-0.00;0.00}% over the last {StockChart.FormatSpan(points.Count, interval)}{windowNote} | low ${FormatHelper.GetCommaNumber(points.Min())} | high ${FormatHelper.GetCommaNumber(points.Max())}\n" +
                "*Sampled once an hour, newest on the right. Add a time like `48h` or `7d` to change the window.*";

            using var chart = StockChart.RenderPng(points, interval);
            var embed = DiscordHelper.GetEmbedBuilder($"{account.Name} net worth", output)
                .WithImageUrl("attachment://networth.png")
                .Build();
            await message.Channel.SendFileAsync(chart, "networth.png", embed: embed);
        }

        /// <summary>Every account's net worth on one chart, each in its own colour, with a legend ranked by current worth.</summary>
        private async Task NetWorthGraphAll(SocketMessage message, TimeSpan? window)
        {
            var ranked = _coinService.GetAll().Accounts
                .Select(a => (Account: a, Current: _worth.TotalWorth(a)))
                .OrderByDescending(x => x.Current)
                .ToList();
            if (ranked.Count == 0 || ranked.All(x => x.Account.NetWorthHistory.Count == 0))
            {
                await message.SendRichEmbedMessage("Everyone's net worth", "Not enough history yet. The graph is sampled once an hour, so check back later.");
                return;
            }

            //each line ends at the current value, like the single account graph
            var series = ranked.Select(x => TakeWindow(x.Account.NetWorthHistory.Append(x.Current).ToList(), window, _netWorthSample, out _)).ToList();

            var lines = new List<string>();
            for (int i = 0; i < ranked.Count; i++)
            {
                var (account, current) = ranked[i];
                double first = series[i][0];
                double change = first == 0 ? 0 : (current - first) / first * 100;
                string dot = _seriesDots[i % _seriesDots.Length];
                lines.Add($"{dot} **{account.Name}** ${FormatHelper.GetCommaNumber(current)} ({change:+0.00;-0.00;0.00}%)");
            }

            string span = window.HasValue
                ? $"the last {StockChart.FormatSpan(series.Max(s => s.Count), _netWorthSample)} (or each person's own history if shorter)"
                : "each person's own history";
            string output = $"Net worth (cash + stocks + property), newest on the right. % is change over {span}. Add a time like `48h` to change the window.\n" +
                string.Join("\n", lines);
            if (ranked.Count > _seriesDots.Length)
                output += $"\n*Colours repeat after {_seriesDots.Length} people.*";

            using var chart = StockChart.RenderMultiPng(series, _netWorthSample);
            var embed = DiscordHelper.GetEmbedBuilder("Everyone's net worth", output)
                .WithImageUrl("attachment://networth.png")
                .Build();
            await message.Channel.SendFileAsync(chart, "networth.png", embed: embed);
        }

        /// <summary>What the jackpot takes off every donation.</summary>
        public const double DonationTax = 0.20;
        /// <summary>INTENTIONAL, don't "fix": 1 donation in this many pays the donor back <see cref="DonationBonusMultiple"/> times what they gave.</summary>
        public const int DonationBonusOdds = 12;
        public const int DonationBonusMultiple = 6;

        private async Task Donate(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count != 2 || !DiscordHelper.TryGetUserId(args[0], out ulong userIdToDonateTo))
                throw new BadSyntaxException();

            ulong userId = message.Author.Id;
            if (userId == userIdToDonateTo)
                throw new BadInputException("You can't **FUCKING** donate to yourself you stupid PIECE OF SHIT");

            var receiverUser = client.GetUser(userIdToDonateTo) ?? message.MentionedUsers.FirstOrDefault(u => u.Id == userIdToDonateTo);
            if (receiverUser?.IsBot == true || userIdToDonateTo == _botId)
                throw new BadInputException("You STUPID *CUNT*! YOU CAN'T DONATE TO A FUCKING BOT!");
            var existing = _coinService.GetAll().Accounts.FirstOrDefault(a => a.UserId == userIdToDonateTo);
            if (existing == null && receiverUser == null)
                throw new BadInputException("Never heard of them. @mention someone who actually plays.");
            EnsureNotBetting(userId, "donating");

            CoinAccount account = await _coinService.Get(userId, message.Author.Username);
            if (!Money.TryParse(args[1], account.NetWorth, out double donationAmount))
                throw new BadInputException("That's not an amount, DUMBASS. Try `.donate @person 1000` or `.donate @person 10%`.");
            CoinAccount receiver = existing ?? await _coinService.Get(userIdToDonateTo, receiverUser.Username);

            if (!_appSettings.PrestigeDisabled && receiver.PrestigeLevel > account.PrestigeLevel)
                throw new BadInputException($"Can't donate to someone with a higher prestige level than you. You are level {account.PrestigeLevel} and they are {receiver.PrestigeLevel}.");

            Money.Debit(account, donationAmount, $"You've got {Money.Format(Math.Max(0, account.NetWorth))}. Can't donate more than you have... SCHYUPID IDIOT");
            double tax = Math.Round(donationAmount * DonationTax, 2);
            double received = donationAmount - tax;
            Money.Credit(_coinService.GetAll(), receiver, received); //pays off their debt first, like any money coming in
            _coinService.AddToJackpot(tax);

            var given = account.Stats;
            given.TotalMoneyDonated += donationAmount;
            given.MaxMoneyDonatedAtOnce = Math.Max(given.MaxMoneyDonatedAtOnce, donationAmount);
            given.DonationAmountsToDict[userIdToDonateTo] = given.DonationAmountsToDict.GetValueOrDefault(userIdToDonateTo) + donationAmount;
            var got = receiver.Stats;
            got.TotalMoneyReceivedFromDonations += received;
            got.MaxMoneyReceivedFromDonationAtOnce = Math.Max(got.MaxMoneyReceivedFromDonationAtOnce, received);
            got.DonationAmountsFromDict[userId] = got.DonationAmountsFromDict.GetValueOrDefault(userId) + received;

            string bonusLine = "";
            if (Random.Shared.Next(DonationBonusOdds) == 0)
            {
                double back = donationAmount * DonationBonusMultiple;
                Money.Credit(_coinService.GetAll(), account, back);
                given.DonationBonusesEncountered += 1;
                bonusLine = $"\n🎉 *Rare bonus for being generous: you get back {Money.Format(back)}.*";
            }

            await _coinService.Update();
            string taxLine = $"The jackpot skimmed {Money.Format(tax)} ({DonationTax:P0} tax).";
            await message.Channel.SendMessageAsync($"💸 {message.Author.Mention} gave **{receiver.Name}** {Money.Format(received)}. {taxLine}{bonusLine}\n"
                + $"{account.Name}: {Money.Format(account.NetWorth)} · {receiver.Name}: {Money.Format(receiver.NetWorth)}", allowedMentions: AllowedMentions.None);
        }

        private Task Jackpot(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            return message.Channel.SendMessageAsync($"The jackpot is ${FormatHelper.GetCommaNumber(_coinService.JackpotPool)}. 75% of every lost bet, every sell tax, donation tax and debt repayment goes in. " +
                $"Bet all your cash (`max`) up to ${BetManager.JackpotSmallBetLimit:N0}, or {BetManager.JackpotBigBetShare:P0} of your cash or more on a bigger bet, with no stocks or property held, and you have a {BetManager.JackpotWinChance:P0} chance to win it all, with {BetManager.JackpotRollsPerHour} rolls per hour each. " +
                "Three 💎 on the `.slots` machine win it too.");
        }

        private async Task Stats(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            //`.stats`, `.stats me`, `.stats @someone`, each with an optional page: `.stats me stocks`
            var accounts = _coinService.GetAll().Accounts;
            string who = args.Count > 0 ? args[0] : "me";
            CoinAccount account;
            if (who.StartsWith("me"))
                account = accounts.FirstOrDefault(a => a.UserId == message.Author.Id);
            else if (DiscordHelper.TryGetUserId(who, out ulong userId))
                account = accounts.FirstOrDefault(a => a.UserId == userId);
            else
                throw new BadSyntaxException();

            if (account == null)
                throw new BadInputException($"Account not found.");

            StatsReport.Section section = StatsReport.Sections[0];
            if (args.Count > 1)
                section = StatsReport.Find(args[1]) ?? throw new BadInputException($"There's no stats page called {args[1]}. Pages: {string.Join(", ", StatsReport.Sections.Select(s => s.Key))}.");

            await message.Channel.SendMessageAsync(embed: StatsEmbed(account, section), components: StatsMenu(account.UserId, section.Key));
        }

        /// <summary>One stats page as an embed.</summary>
        private Embed StatsEmbed(CoinAccount account, StatsReport.Section section)
        {
            var accounts = _coinService.GetAll().Accounts;
            double TotalWorth(CoinAccount a) => _worth.TotalWorth(a);
            var ordered = _worth.Ranked(accounts);

            var market = _stockService.GetMarket();
            double index = _coinService.GetAll().PropertyIndex;
            var context = new StatsContext
            {
                StocksValue = _stockService.GetPortfolioValue(account.UserId),
                PropertyValue = _coinService.PropertyValue(account),
                HourlyRent = account.Properties.Sum(p => PropertyCatalog.NetRentPerHour(p.Kind, index)),
                Rank = ordered.IndexOf(account) + 1,
                Players = ordered.Count,
                Holdings = market.GetHoldings(account.UserId).Select(h => (h.Key, h.Value, market.Find(h.Key)?.Price ?? 0)).ToList(),
                OpenShorts = market.GetShorts(account.UserId).Count,
                OpenOptions = market.Options.Count(o => o.UserId == account.UserId),
                JackpotPool = _coinService.JackpotPool,
                EnteredDraws = RaffleService.EnteredDrawCount(account, DateTime.UtcNow),
                PrestigeDisabled = _appSettings.PrestigeDisabled,
                NameOf = id => accounts.FirstOrDefault(a => a.UserId == id)?.Name ?? id.ToString(),
            };
            return DiscordHelper.GetEmbedBuilder($"{account.Name} · {section.Emoji} {section.Label}", StatsReport.Build(section.Key, account, context)).Build();
        }

        /// <summary>The page picker under a stats message. Anyone can use it: stats are public.</summary>
        private static MessageComponent StatsMenu(ulong accountId, string current)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId(ComponentId.Build(MoneyComponents.Stats, "pick", accountId.ToString()))
                .WithPlaceholder("More stats…");
            foreach (var section in StatsReport.Sections)
                menu.AddOption(section.Label, section.Key, emote: new Emoji(section.Emoji), isDefault: section.Key == current);
            return new ComponentBuilder().WithSelectMenu(menu).Build();
        }

        private async Task StatsComponent(SocketMessageComponent component, string action, string[] args)
        {
            if (action != "pick" || !ButtonOwner.TryRead(args, 0, out ulong accountId) || component.Data.Values.Count == 0)
            {
                await ReplyPrivately(component, "That menu doesn't do anything any more.");
                return;
            }
            CoinAccount account = _coinService.GetAll().Accounts.FirstOrDefault(a => a.UserId == accountId);
            if (account == null)
            {
                await ReplyPrivately(component, "Account not found.");
                return;
            }

            StatsReport.Section section = StatsReport.Find(component.Data.Values.First()) ?? StatsReport.Sections[0];
            await component.DeferAsync();
            Embed embed = StatsEmbed(account, section);
            MessageComponent menu = StatsMenu(accountId, section.Key);
            await component.Message.ModifyAsync(m =>
            {
                m.Embed = embed;
                m.Components = menu;
            });
        }
    }
}
