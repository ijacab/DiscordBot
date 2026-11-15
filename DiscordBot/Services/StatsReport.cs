using Common.Helpers;
using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Services
{
    /// <summary>What a stats page needs to know that isn't on the account itself. The command fills it in.</summary>
    public sealed class StatsContext
    {
        public double StocksValue { get; init; }
        public double PropertyValue { get; init; }
        /// <summary>Net rent per hour from all houses, after the agency fee.</summary>
        public double HourlyRent { get; init; }
        public int Rank { get; init; }
        public int Players { get; init; }
        public IReadOnlyList<(string Symbol, int Shares, double Price)> Holdings { get; init; } = Array.Empty<(string, int, double)>();
        public int OpenShorts { get; init; }
        public int OpenOptions { get; init; }
        public double JackpotPool { get; init; }
        public int EnteredDraws { get; init; }
        public bool PrestigeDisabled { get; init; } = true;
        public DateTime Now { get; init; } = DateTime.UtcNow;
        public Func<ulong, string> NameOf { get; init; } = id => id.ToString();
    }

    /// <summary>
    /// The pages of `.stats`: one text per section, built from the account's tracked numbers. Pure, so it's tested without Discord.
    /// Counters that were added later start from zero for older accounts.
    /// </summary>
    public static class StatsReport
    {
        public sealed class Section
        {
            public string Key { get; init; }
            public string Label { get; init; }
            public string Emoji { get; init; }
        }

        public static readonly IReadOnlyList<Section> Sections = new List<Section>
        {
            new Section { Key = "overview", Label = "Overview", Emoji = "📊" },
            new Section { Key = "betting", Label = "Betting", Emoji = "🎲" },
            new Section { Key = "games", Label = "Games", Emoji = "🎮" },
            new Section { Key = "stocks", Label = "Stocks", Emoji = "📈" },
            new Section { Key = "property", Label = "Property & debt", Emoji = "🏠" },
            new Section { Key = "jackpot", Label = "Jackpot & income", Emoji = "💰" },
            new Section { Key = "powerups", Label = "Power-ups", Emoji = "⚡" },
            new Section { Key = "dungeon", Label = "Dungeon", Emoji = "🗡️" },
            new Section { Key = "donations", Label = "Donations", Emoji = "🎁" },
        };

        /// <summary>The section a typed word means ("stock", "bets", "pu"...), or null.</summary>
        public static Section Find(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return null;
            word = word.Trim().ToLowerInvariant();
            var aliases = new Dictionary<string, string>
            {
                ["stock"] = "stocks", ["bets"] = "betting", ["bet"] = "betting", ["game"] = "games", ["houses"] = "property", ["debt"] = "property",
                ["jackpots"] = "jackpot", ["income"] = "jackpot", ["pu"] = "powerups", ["powerup"] = "powerups", ["power-ups"] = "powerups",
                ["raffle"] = "powerups", ["dng"] = "dungeon", ["donation"] = "donations", ["donate"] = "donations", ["all"] = "overview",
            };
            if (aliases.TryGetValue(word, out string key)) word = key;
            return Sections.FirstOrDefault(s => s.Key == word);
        }

        public static string Build(string key, CoinAccount account, StatsContext context)
        {
            switch (key)
            {
                case "betting": return Betting(account);
                case "games": return Games(account);
                case "stocks": return StocksPage(account, context);
                case "property": return PropertyPage(account, context);
                case "jackpot": return JackpotPage(account, context);
                case "powerups": return PowerUps(account, context);
                case "dungeon": return Dungeon(account);
                case "donations": return Donations(account, context);
                default: return Overview(account, context);
            }
        }

        // ---- formatting

        private static string M(double amount) => (amount < 0 ? "-$" : "$") + FormatHelper.GetCommaNumber(Math.Abs(amount));
        private static string Signed(double amount) => (amount >= 0 ? "+$" : "-$") + FormatHelper.GetCommaNumber(Math.Abs(amount));
        private static string N(long count) => count.ToString("N0");
        private static string Pct(double part, double whole) => whole <= 0 ? "n/a" : $"{part / whole * 100:0.#}%";
        private static string Row(string label, string value) => $"**{label}:** {value}\n";

        private sealed class Page
        {
            private readonly StringBuilder _text = new StringBuilder();
            public Page Row(string label, string value) { _text.Append(StatsReport.Row(label, value)); return this; }
            public Page Heading(string text) { if (_text.Length > 0) _text.Append('\n'); _text.Append($"__{text}__\n"); return this; }
            public Page Line(string text) { _text.AppendLine(text); return this; }
            public override string ToString() => _text.ToString().TrimEnd();
        }

        // ---- sections

        private static string Overview(CoinAccount a, StatsContext c)
        {
            var s = a.Stats;
            double total = a.NetWorth + c.StocksValue + c.PropertyValue - a.Debt;
            var page = new Page()
                .Row("Total worth", M(total) + (c.Rank > 0 ? $" · #{c.Rank} of {c.Players}" : ""))
                .Row("Cash", M(a.NetWorth))
                .Row("Stocks", M(c.StocksValue))
                .Row("Property", M(c.PropertyValue))
                .Row("Debt", a.Debt > 0 ? "-" + M(a.Debt) : "none");
            if (!c.PrestigeDisabled)
                page.Row("Prestige", a.PrestigeLevel.ToString());

            var history = a.NetWorthHistory;
            if (history.Count > 0)
            {
                page.Heading("Net worth history");
                page.Row("Peak", M(history.Max())).Row("Lowest", M(history.Min()));
                if (history.Count > 24) page.Row("Last 24 hours", Signed(history[^1] - history[^25]));
                if (history.Count > 168) page.Row("Last 7 days", Signed(history[^1] - history[^169]));
            }

            string today = c.Now.ToString("yyyyMMdd");
            page.Heading("Today")
                .Row("Winnings today", Signed(a.NetWinningsToday))
                .Row("Hourly bonus", a.MostRecentDateBonusMet == today ? $"active ({Constants.InterestPercentage}% an hour)" : "not earned yet")
                .Row("Raffle draws entered", c.EnteredDraws.ToString());

            page.Heading("Lifetime")
                .Row("Games played", N(s.GamesPlayed))
                .Row("Win rate", Pct(s.BetsWon, s.BetsWon + s.BetsLost))
                .Row("Net from betting", Signed(s.TotalMoneyWon - s.TotalMoneyLost))
                .Row("Earned from interest and daily income", M(s.InterestEarned + s.DailyIncomeEarned));
            page.Line("\n*Pick another page from the menu below.*");
            return page.ToString();
        }

        private static string Betting(CoinAccount a)
        {
            var s = a.Stats;
            long decided = s.BetsWon + s.BetsLost;
            return new Page()
                .Row("Games played", N(s.GamesPlayed))
                .Row("Bets won", $"{N(s.BetsWon)} ({Pct(s.BetsWon, decided)})")
                .Row("Bets lost", $"{N(s.BetsLost)} ({Pct(s.BetsLost, decided)})")
                .Heading("Money")
                .Row("Total bet", M(s.TotalMoneyBet))
                .Row("Average bet", s.GamesPlayed > 0 ? M(s.TotalMoneyBet / s.GamesPlayed) : "n/a")
                .Row("Total won", M(s.TotalMoneyWon))
                .Row("Total lost", M(s.TotalMoneyLost))
                .Row("Net", Signed(s.TotalMoneyWon - s.TotalMoneyLost))
                .Row("Return on bets", s.TotalMoneyBet > 0 ? $"{(s.TotalMoneyWon - s.TotalMoneyLost) / s.TotalMoneyBet * 100:+0.#;-0.#;0}%" : "n/a")
                .Row("Bonus winnings", M(s.TotalBonusWinnings))
                .Heading("Records")
                .Row("Biggest bet", M(s.MaxMoneyBetAtOnce))
                .Row("Biggest win", M(s.MaxMoneyWonAtOnce))
                .Row("Biggest loss", M(s.MaxMoneyLostAtOnce))
                .Heading("Streaks")
                .Row("Current", s.CurrentWinStreak > 0 ? $"{N(s.CurrentWinStreak)} wins" : s.CurrentLossStreak > 0 ? $"{N(s.CurrentLossStreak)} losses" : "none")
                .Row("Longest win streak", N(s.MaxWinStreak))
                .Row("Longest loss streak", N(s.MaxLossStreak))
                .ToString();
        }

        private static string Games(CoinAccount a)
        {
            var games = a.Stats.Games.Where(g => g.Value.Played > 0).OrderByDescending(g => g.Value.Played).ToList();
            if (games.Count == 0)
                return "No games recorded yet. Per-game results start counting from the games you play now.";

            var page = new Page();
            foreach (var (name, r) in games)
            {
                page.Line($"**{name}** · {N(r.Played)} played · {N(r.Won)}W {N(r.Lost)}L{(r.Pushed > 0 ? $" {N(r.Pushed)}P" : "")} ({Pct(r.Won, r.Played)})");
                page.Line($"   bet {M(r.MoneyBet)} · net {Signed(r.Net)} · best {Signed(r.BestWin)} · worst {Signed(-r.WorstLoss)}");
            }
            var best = games.Where(g => g.Value.Played >= 3).OrderByDescending(g => g.Value.Net).FirstOrDefault();
            var worst = games.Where(g => g.Value.Played >= 3).OrderBy(g => g.Value.Net).FirstOrDefault();
            if (best.Key != null && worst.Key != null && best.Key != worst.Key)
                page.Line($"\nMost profitable: **{best.Key}** ({Signed(best.Value.Net)}) · Least: **{worst.Key}** ({Signed(worst.Value.Net)})");
            return page.ToString();
        }

        private static string StocksPage(CoinAccount a, StatsContext c)
        {
            var s = a.Stats;
            var page = new Page().Row("Portfolio value", M(c.StocksValue));
            if (c.Holdings.Count > 0)
                page.Row("Holdings", string.Join(", ", c.Holdings.OrderByDescending(h => h.Shares * h.Price).Take(12).Select(h => $"{h.Symbol} x{N(h.Shares)}")));
            page.Row("Open shorts", c.OpenShorts.ToString()).Row("Open options", c.OpenOptions.ToString());
            page.Heading("Trading")
                .Row("Buys", N(s.StockBuys)).Row("Sells", N(s.StockSells))
                .Row("Shares bought / sold", $"{s.SharesBought:N0} / {s.SharesSold:N0}")
                .Row("Spent on stocks", M(s.MoneySpentOnStocks))
                .Row("Sale proceeds", M(s.StockSaleProceeds))
                .Row("Realised profit", Signed(s.StockRealizedProfit))
                .Row("Best sale", Signed(s.BestStockSale)).Row("Worst sale", Signed(s.WorstStockSale))
                .Row("Tax paid (into the jackpot)", M(s.SellTaxPaid));
            page.Heading("Shorts and options")
                .Row("Shorts opened / covered", $"{N(s.ShortsOpened)} / {N(s.ShortsCovered)}")
                .Row("Short profit", Signed(s.ShortProfit))
                .Row("Option contracts bought", N(s.OptionContractsBought))
                .Row("Premiums and fees paid", M(s.OptionPremiumsPaid))
                .Row("Option payouts", M(s.OptionPayouts))
                .Row("Expired in the money", N(s.OptionsExpiredInTheMoney))
                .Row("Options net", Signed(s.OptionPayouts - s.OptionPremiumsPaid));
            return page.ToString();
        }

        private static string PropertyPage(CoinAccount a, StatsContext c)
        {
            var s = a.Stats;
            return new Page()
                .Row("Houses owned", a.Properties.Count.ToString())
                .Row("Worth if sold", M(c.PropertyValue))
                .Row("Rent per hour", M(c.HourlyRent))
                .Heading("History")
                .Row("Bought / sold", $"{N(s.PropertiesBought)} / {N(s.PropertiesSold)}")
                .Row("Spent", M(s.PropertySpent))
                .Row("Sale proceeds", M(s.PropertySaleProceeds))
                .Row("Rent earned", M(s.RentEarned))
                .Row("Holding costs paid", M(s.HoldingCostsPaid))
                .Row("Property net", Signed(s.RentEarned + s.PropertySaleProceeds - s.PropertySpent - s.HoldingCostsPaid))
                .Heading("Debt")
                .Row("Owed now", a.Debt > 0 ? M(a.Debt) : "none")
                .Row("Highest debt", M(s.PeakDebt))
                .Row("Repaid so far", M(s.DebtRepaid))
                .Row("Auto-sell for debt", a.AutoSellForDebt ? "on" : "off")
                .ToString();
        }

        private static string JackpotPage(CoinAccount a, StatsContext c)
        {
            var s = a.Stats;
            long hour = c.Now.Ticks / TimeSpan.TicksPerHour;
            int usedThisHour = a.JackpotRollHour == hour ? a.JackpotRollsThisHour : 0;
            return new Page()
                .Row("Jackpot right now", M(c.JackpotPool))
                .Row("Your rolls used this hour", $"{usedThisHour} of 2")
                .Heading("Your jackpots")
                .Row("All-in rolls made", N(s.JackpotRolls))
                .Row("Jackpots won", N(s.JackpotsWon))
                .Row("Won from the pool", M(s.JackpotMoneyWon))
                .Row("Slot jackpots (💎💎💎)", N(s.SlotJackpotsHit))
                .Row("Your lost bets that fed the pool", M(s.MoneyFedToJackpot))
                .Row("Dungeon buy-ins lost", M(s.DungeonBuyInsLost))
                .Heading("First gamble of the day")
                .Row("Daily rolls", N(s.DailyRolls))
                .Row("Steals won", $"{N(s.DailyStealsWon)} ({M(s.MoneyStolenByDailyRoll)})")
                .Row("Times robbed", $"{N(s.TimesRobbedByDailyRoll)} ({M(s.MoneyLostToDailyRoll)})")
                .Heading("Income")
                .Row("Daily $1,000s", M(s.DailyIncomeEarned))
                .Row("Hourly interest", M(s.InterestEarned))
                .Row("Bonus winnings from betting", M(s.TotalBonusWinnings))
                .ToString();
        }

        private static string PowerUps(CoinAccount a, StatsContext c)
        {
            var s = a.Stats;
            long now = c.Now.Ticks;
            string Name(string id) => PowerUpCatalog.Find(id)?.Name ?? id;
            var held = a.HeldPowerUps.Where(h => h.ExpiresAtTicks == 0 || h.ExpiresAtTicks > now).Select(h => Name(h.Id)).ToList();
            var active = a.ActivePowerUps.Where(p => p.ExpiresAtTicks > now && p.ChargesLeft != 0).Select(p => Name(p.Id)).ToList();
            return new Page()
                .Row("Kept to use", held.Count == 0 ? "none" : string.Join(", ", held.Take(15)))
                .Row("Running now", active.Count == 0 ? "none" : string.Join(", ", active.Take(15)))
                .Row("In the next draws", $"{c.EnteredDraws} of 6")
                .Heading("Raffle")
                .Row("Draws entered", N(s.RafflesEntered))
                .Row("Draws won", $"{N(s.RafflesWon)} ({Pct(s.RafflesWon, s.RafflesEntered)})")
                .Row("Power-ups used", N(s.PowerUpsUsed))
                .Heading("Attacks")
                .Row("Landed on others", N(s.AttacksLanded))
                .Row("Suffered", N(s.AttacksSuffered))
                .Row("Taken from others", M(s.MoneyTakenByPowerUps))
                .Row("Lost to others", M(s.MoneyLostToPowerUps))
                .Row("Net", Signed(s.MoneyTakenByPowerUps - s.MoneyLostToPowerUps))
                .ToString();
        }

        private static string Dungeon(CoinAccount a)
        {
            var s = a.Stats;
            var faceOff = a.Stats.Games.TryGetValue("Face Off", out var record) ? record : null;
            var page = new Page()
                .Row("Dungeon buy-ins", N(s.DungeonRuns))
                .Row("Paid out (cash-outs and refunds)", M(s.DungeonPayouts))
                .Row("Buy-ins lost", M(s.DungeonBuyInsLost));
            page.Heading("Face Off");
            if (faceOff == null || faceOff.Played == 0)
                page.Line("No Face Off fights recorded yet.");
            else
                page.Row("Fights", N(faceOff.Played)).Row("Won / lost", $"{N(faceOff.Won)} / {N(faceOff.Lost)} ({Pct(faceOff.Won, faceOff.Played)})")
                    .Row("Net", Signed(faceOff.Net)).Row("Best win", Signed(faceOff.BestWin));
            page.Row("Battle card", a.BattlePerson?.Name ?? "none (`.cardpull`)");
            return page.ToString();
        }

        private static string Donations(CoinAccount a, StatsContext c)
        {
            var s = a.Stats;
            var page = new Page()
                .Row("Donated", M(s.TotalMoneyDonated))
                .Row("Biggest donation", M(s.MaxMoneyDonatedAtOnce))
                .Row("Received", M(s.TotalMoneyReceivedFromDonations))
                .Row("Biggest received", M(s.MaxMoneyReceivedFromDonationAtOnce))
                .Row("Donation bonuses", s.DonationBonusesEncountered.ToString("N0"));

            void List(string heading, Dictionary<ulong, double> amounts)
            {
                if (amounts == null || amounts.Count == 0) return;
                page.Heading(heading);
                foreach (var kvp in amounts.OrderByDescending(k => k.Value).Take(10))
                    page.Line($"{c.NameOf(kvp.Key)}: {M(kvp.Value)}");
            }
            List("Donated to", s.DonationAmountsToDict);
            List("Received from", s.DonationAmountsFromDict);
            return page.ToString();
        }
    }
}
