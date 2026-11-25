using Common.Helpers;
using Discord;
using DiscordBot.Games.Models;
using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Games
{
    /// <summary>One bet on the table: what it's on ("red", "17", "00", "thirddozen") and how much.</summary>
    public sealed class RouletteChoice
    {
        public string Choice { get; init; }
        public double Amount { get; init; }
    }

    /// <summary>One line of a settled spin: a bet, whether it won and what it paid back (stake included).</summary>
    public sealed class RouletteLine
    {
        public string Label { get; init; }
        public double Amount { get; init; }
        public bool Won { get; init; }
        public double Back { get; init; }
    }

    /// <summary>What the table message shows at one moment.</summary>
    public sealed class RouletteScreen
    {
        public string Name { get; init; }
        /// <summary>The pocket that came up ("23", "0", "00"), or null before the first spin.</summary>
        public string Pocket { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
        public IReadOnlyList<RouletteLine> Lines { get; init; } = Array.Empty<RouletteLine>();
        public string Status { get; init; }
        public Color Colour { get; init; } = Color.DarkGrey;
        public double Cash { get; init; }
        public int Spins { get; init; }
        public double SessionNet { get; init; }
        public string[] Notes { get; init; } = Array.Empty<string>();
    }

    /// <summary>The roulette table's embed and buttons. Pure text and ids, so it can be tested without Discord.</summary>
    public static class RouletteView
    {
        public const string Title = "🎡 Roulette";
        public const string HowToBet = "Type your bets like `red 1000` or `17 50 odd 25%`: each is a spot then an amount. Spots: a number (`0`-`36`, `00`), `red`, `black`, `odd`, `even`, `firstdozen`, `seconddozen`, `thirddozen`, `firstcolumn`, `secondcolumn`, `thirdcolumn`.";

        private static readonly int[] Reds = { 32, 19, 21, 25, 34, 27, 36, 30, 23, 5, 16, 1, 14, 9, 18, 7, 12, 3 };

        /// <summary>"-1" is the double zero.</summary>
        public static string PocketName(string value) => value == "-1" ? "00" : value;

        /// <summary>🔴, ⚫ or 🟢 for a pocket.</summary>
        public static string Dot(string pocket)
        {
            if (!int.TryParse(pocket == "00" ? "-1" : pocket, out int n) || n <= 0) return "🟢";
            return Reds.Contains(n) ? "🔴" : "⚫";
        }

        /// <summary>"FirstDozen" as "1st dozen", "Red" as "Red", a number as "#17".</summary>
        public static string Label(RouletteBet bet)
        {
            if (bet.RoulleteBetType == BetType.Number)
                return bet.BetNumberChoice == -1 ? "#00" : $"#{bet.BetNumberChoice}";
            return Pretty(bet.RoulleteBetType.ToString());
        }

        public static string Pretty(string betType) => betType
            .Replace("First", "1st ").Replace("Second", "2nd ").Replace("Third", "3rd ")
            .Replace("Dozen", "dozen").Replace("Column", "column").Trim();

        public static Embed Build(RouletteScreen screen)
        {
            var text = new StringBuilder();
            if (screen.Pocket != null)
            {
                text.AppendLine($"{Dot(screen.Pocket)} **{screen.Pocket}**" + (screen.Tags.Length > 0 ? $"  ·  {string.Join(", ", screen.Tags)}" : ""));
                text.AppendLine();
            }
            foreach (var line in screen.Lines)
            {
                string result = screen.Pocket == null ? "" : line.Won ? $"  ✅ → ${FormatHelper.GetCommaNumber(line.Back)}" : "  ❌";
                text.AppendLine($"• {line.Label} · ${FormatHelper.GetCommaNumber(line.Amount)}{result}");
            }
            if (screen.Lines.Count == 0)
                text.AppendLine(HowToBet);
            if (!string.IsNullOrEmpty(screen.Status))
                text.AppendLine().Append(screen.Status);
            foreach (string note in screen.Notes.Where(n => !string.IsNullOrEmpty(n)))
                text.Append("\n").Append(note);

            string session = screen.SessionNet == 0 ? "$0" : $"{(screen.SessionNet > 0 ? "+" : "-")}${FormatHelper.GetCommaNumber(Math.Abs(screen.SessionNet))}";
            return new EmbedBuilder()
                .WithAuthor(screen.Name == null ? Title : $"{Title} · {screen.Name}")
                .WithDescription(text.ToString())
                .AddField("Pays (x your bet)", PayTable(), false)
                .WithColor(screen.Colour)
                .WithFooter($"Your cash is ${FormatHelper.GetCommaNumber(screen.Cash)} · Session {session} · {screen.Spins} spin{(screen.Spins == 1 ? "" : "s")}")
                .Build();
        }

        public static string PayTable() =>
            $"Number x{new RouletteBet(0, "1", 1).PayoutMultiple + 1} · Red, Black, Odd, Even x{new RouletteBet(0, "red", 1).PayoutMultiple + 1} · Dozens and columns x{new RouletteBet(0, "firstdozen", 1).PayoutMultiple + 1}\n"
            + "0 and 00 only win a bet on that number. You must bet at least 10% of your cash.";

        /// <summary>The result line and colour for a settled spin.</summary>
        public static (string Status, Color Colour) Outcome(double staked, double totalPaid, double bonus)
        {
            double net = totalPaid - staked;
            string bonusText = bonus > 0 ? $" (+${FormatHelper.GetCommaNumber(bonus)} bonus)" : "";
            if (totalPaid <= 0)
                return ($"💥 No winners. **-${FormatHelper.GetCommaNumber(staked)}**", Color.Red);
            if (net <= 0)
                return ($"↩️ Paid ${FormatHelper.GetCommaNumber(totalPaid)} on ${FormatHelper.GetCommaNumber(staked)} bet{bonusText}", Color.Orange);
            return ($"🎉 **+${FormatHelper.GetCommaNumber(net)}**{bonusText}", Color.Gold);
        }

        /// <summary>Spin again, scale the bets, or change them. Every id carries the owner, so only they can press them.</summary>
        public static MessageComponent Buttons(ulong ownerId, double total, double cash)
        {
            string owner = ownerId.ToString();
            string Id(string action) => ComponentId.Build(MoneyComponents.Roulette, action, owner);
            bool canSpin = total >= 1 && cash >= 1;
            return new ComponentBuilder()
                .WithButton(total >= 1 ? $"Spin ${FormatHelper.GetCommaNumber(Math.Min(total, Math.Floor(cash)))}" : "Spin", Id("spin"), ButtonStyle.Success, new Emoji("🎡"), disabled: !canSpin, row: 0)
                .WithButton("Bets ½", Id("half"), ButtonStyle.Secondary, disabled: total < 1, row: 0)
                .WithButton("Bets x2", Id("double"), ButtonStyle.Secondary, disabled: total < 1, row: 0)
                .WithButton("All in", Id("max"), ButtonStyle.Danger, disabled: total < 1, row: 0)
                .WithButton("New bets…", Id("bet"), ButtonStyle.Secondary, row: 1)
                .WithButton("Cash out", Id("cash"), ButtonStyle.Primary, row: 1)
                .Build();
        }

        /// <summary>The bets after a ½, x2 or all-in press, scaled together and held to the cash (each at least $1; ones that fall under are dropped).</summary>
        public static List<RouletteChoice> Scale(IReadOnlyList<RouletteChoice> bets, string action, double cash)
        {
            double ceiling = Math.Floor(cash);
            double total = bets.Sum(b => b.Amount);
            if (total < 1 || ceiling < 1) return new List<RouletteChoice>();
            double wanted = action == "half" ? total / 2 : action == "double" ? total * 2 : action == "max" ? ceiling : total;
            double factor = Math.Min(wanted, ceiling) / total;
            return bets.Select(b => new RouletteChoice { Choice = b.Choice, Amount = Math.Floor(b.Amount * factor) })
                .Where(b => b.Amount >= 1).ToList();
        }
    }
}
