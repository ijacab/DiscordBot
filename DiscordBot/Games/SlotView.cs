using Common.Helpers;
using Discord;
using DiscordBot.Models;
using System;
using System.Linq;
using System.Text;

namespace DiscordBot.Games
{
    /// <summary>What the machine message shows at one moment. The manager builds one per spin.</summary>
    public sealed class SlotScreen
    {
        /// <summary>[reel][row] as in <see cref="SlotResult.Window"/>, or null for a machine that hasn't spun yet.</summary>
        public SlotSymbol[][] Window { get; init; }
        /// <summary>One line under the window: the result, or what is happening.</summary>
        public string Status { get; init; }
        public Color Colour { get; init; } = Color.DarkGrey;
        public string Name { get; init; }
        public double Bet { get; init; }
        public double Cash { get; init; }
        public int Spins { get; init; }
        public double SessionNet { get; init; }
        /// <summary>Daily roll and jackpot lines, shown under the result.</summary>
        public string[] Notes { get; init; } = Array.Empty<string>();
    }

    /// <summary>The slot machine's embed and buttons. Pure text and ids, so it can be tested without Discord.</summary>
    public static class SlotView
    {
        public const string Title = "🎰 Lucky Slots";
        private const string Blank = "❔";

        /// <summary>The window of three reels and three rows, with arrows on the payline.</summary>
        public static string Window(SlotSymbol[][] window)
        {
            var text = new StringBuilder();
            for (int row = 0; row < SlotResult.Rows; row++)
            {
                bool line = row == SlotResult.PaylineRow;
                string symbols = string.Join(" ┃ ", Enumerable.Range(0, SlotResult.Reels)
                    .Select(reel => window == null ? Blank : SlotMachine.Emoji(window[reel][row])));
                text.AppendLine(line ? $"▶️ {symbols} ◀️" : $"▫️ {symbols} ▫️");
            }
            return text.ToString().TrimEnd();
        }

        public static Embed Build(SlotScreen screen)
        {
            var text = new StringBuilder();
            text.AppendLine(Window(screen.Window));
            text.AppendLine();
            text.Append(screen.Status);
            foreach (string note in screen.Notes.Where(n => !string.IsNullOrEmpty(n)))
                text.Append("\n").Append(note);

            string session = screen.SessionNet == 0 ? "$0" : $"{(screen.SessionNet > 0 ? "+" : "-")}${FormatHelper.GetCommaNumber(Math.Abs(screen.SessionNet))}";
            return new EmbedBuilder()
                .WithAuthor(screen.Name == null ? Title : $"{Title} · {screen.Name}")
                .WithDescription(text.ToString())
                .AddField("Pays (x your bet)", PayTable(), false)
                .WithColor(screen.Colour)
                .WithFooter($"Bet ${FormatHelper.GetCommaNumber(screen.Bet)} · Cash ${FormatHelper.GetCommaNumber(screen.Cash)} · Session {session} · {screen.Spins} spin{(screen.Spins == 1 ? "" : "s")}")
                .Build();
        }

        /// <summary>The paytable, from the same numbers the machine pays by.</summary>
        public static string PayTable()
        {
            string Three(SlotSymbol s) => $"{SlotMachine.Emoji(s)}{SlotMachine.Emoji(s)}{SlotMachine.Emoji(s)} x{SlotMachine.PayFor(s):0}";
            return $"7️⃣7️⃣7️⃣ x{SlotMachine.PayFor(SlotSymbol.Seven):0} · {Three(SlotSymbol.Star)} · {Three(SlotSymbol.Bell)} · {Three(SlotSymbol.Grape)} · {Three(SlotSymbol.Orange)} · {Three(SlotSymbol.Lemon)} · {Three(SlotSymbol.Cherry)}\n"
                + $"🍒🍒 x{SlotMachine.TwoCherriesMultiple:0} · 🍒 on the left reel x{SlotMachine.LeftCherryMultiple:0}\n"
                + $"💎💎💎 **JACKPOT**: takes the pool, from x{SlotMachine.JackpotFloorMultiple:0} up to x{SlotMachine.JackpotCapMultiple:0} your bet";
        }

        /// <summary>The result line and colour for a finished spin.</summary>
        public static (string Status, Color Colour) Outcome(SlotResult result, double bet, double totalPaid, double bonus)
        {
            double net = totalPaid - bet;
            if (result.Jackpot)
                return ($"💎 **JACKPOT!** You won **${FormatHelper.GetCommaNumber(totalPaid)}**!", Color.Magenta);
            if (result.Multiple <= 0)
                return ($"💥 {result.Label} · **-${FormatHelper.GetCommaNumber(bet)}**", Color.Red);

            string bonusText = bonus > 0 ? $" (+${FormatHelper.GetCommaNumber(bonus)} bonus)" : "";
            if (net <= 0)
                return ($"↩️ {result.Label} · x{result.Multiple:0.##} · bet back{bonusText}", Color.Orange);
            return ($"🎉 {result.Label} · x{result.Multiple:0.##} · **+${FormatHelper.GetCommaNumber(net)}**{bonusText}", Color.Gold);
        }

        /// <summary>Spin, change the bet, or cash out. Every id carries the owner, so only they can press them.</summary>
        public static MessageComponent Buttons(ulong ownerId, double bet, double cash)
        {
            string owner = ownerId.ToString();
            string Id(string action) => ComponentId.Build(MoneyComponents.Slots, action, owner);
            bool broke = cash < 1;
            return new ComponentBuilder()
                .WithButton($"Spin ${FormatHelper.GetCommaNumber(Math.Min(bet, Math.Max(1, Math.Floor(cash))))}", Id("spin"), ButtonStyle.Success, new Emoji("🎰"), disabled: broke, row: 0)
                .WithButton("Bet ½", Id("half"), ButtonStyle.Secondary, row: 0)
                .WithButton("Bet x2", Id("double"), ButtonStyle.Secondary, row: 0)
                .WithButton("Max", Id("max"), ButtonStyle.Danger, row: 0)
                .WithButton("Bet…", Id("bet"), ButtonStyle.Secondary, row: 1)
                .WithButton("Cash out", Id("cash"), ButtonStyle.Primary, row: 1)
                .Build();
        }

        /// <summary>The bet after a ½, x2 or max press: at least $1 and never more than the cash.</summary>
        public static double NextBet(string action, double bet, double cash)
        {
            double ceiling = Math.Max(1, Math.Floor(cash));
            double next = action == "half" ? Math.Floor(bet / 2) : action == "double" ? bet * 2 : action == "max" ? ceiling : bet;
            return Math.Clamp(next, 1, ceiling);
        }
    }
}
