using Common.Helpers;
using Discord;
using DiscordBot.Games.Managers;
using DiscordBot.Games.Models;
using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Games
{
    /// <summary>One bet: what it's on ("red", "17", "00", "thirddozen") and how much.</summary>
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

    /// <summary>A settled spin. The money has already moved when this exists.</summary>
    public sealed class RouletteResult
    {
        public string Name { get; init; }
        /// <summary>The pocket that came up ("23", "0", "00") and the bet types it satisfies ("Red", "Odd", "2nd dozen"...).</summary>
        public string Pocket { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
        public IReadOnlyList<RouletteLine> Lines { get; init; } = Array.Empty<RouletteLine>();
        public double Staked { get; init; }
        public double TotalPaid { get; init; }
        public double Bonus { get; init; }
        public double Cash { get; init; }
        public bool BonusGranted { get; init; }
        /// <summary>The daily roll and jackpot lines, shown under the result.</summary>
        public string[] Notes { get; init; } = Array.Empty<string>();
    }

    /// <summary>The roulette message and buttons. Pure text and ids, so it can be tested without Discord.</summary>
    public static class RouletteView
    {
        public const string HowToBet = "Bet with `.roulette red 1000` or `.roulette 17 50 odd 25%`, or press **New bets…**: each bet is a spot then an amount. Spots: a number (`0`-`36`, `00`), `red`, `black`, `odd`, `even`, `firstdozen`, `seconddozen`, `thirddozen`, `firstcolumn`, `secondcolumn`, `thirdcolumn`.";

        /// <summary>"-1" is the double zero.</summary>
        public static string PocketName(string value) => value == "-1" ? "00" : value;

        /// <summary>"FirstDozen" as "1st dozen", "Red" as "Red".</summary>
        public static string Pretty(string betType) => betType
            .Replace("First", "1st ").Replace("Second", "2nd ").Replace("Third", "3rd ")
            .Replace("Dozen", "dozen").Replace("Column", "column").Trim();

        /// <summary>What a bet is on: "17", "00", "Red", "1st dozen".</summary>
        public static string Label(RouletteBet bet) =>
            bet.RoulleteBetType == BetType.Number ? (bet.BetNumberChoice == -1 ? "00" : bet.BetNumberChoice.ToString()) : Pretty(bet.RoulleteBetType.ToString());

        /// <summary>
        /// The result as the plain message roulette has always posted: the pocket, then either "no successful bets" or each winning
        /// bet with what it paid, and the cash. <paramref name="mention"/> is how the player is named on a win.
        /// </summary>
        public static string Text(RouletteResult result, string mention, bool prestigeDisabled)
        {
            var text = new StringBuilder();
            text.Append($"Winning results were **{result.Pocket}**" + (result.Tags.Length > 0 ? ", " + string.Join(", ", result.Tags) : "") + ".\n");
            var winners = result.Lines.Where(l => l.Won).ToList();
            if (winners.Count == 0)
            {
                text.Append($"{result.Name} you did not make any successful bets. Your cash is now ${FormatHelper.GetCommaNumber(result.Cash)}.");
            }
            else
            {
                text.Append("`Your winning bets were:`\n");
                foreach (var line in winners)
                    text.Append($"{line.Label}: ${FormatHelper.GetCommaNumber(line.Amount)} -> ${FormatHelper.GetCommaNumber(line.Back)}\n");
                text.Append($"(+ bonus of ${FormatHelper.GetCommaNumber(result.Bonus)})\n");
                text.Append($"{mention} `Your cash is now ${FormatHelper.GetCommaNumber(result.Cash)}`");
            }

            if (result.BonusGranted)
                text.Append(prestigeDisabled
                    ? $"\n\n*You will get a bonus {Constants.InterestPercentage}% net worth each hour for the rest of the day (UTC).*"
                    : $"\n\n*You will get a bonus $1000 + {Constants.InterestPercentage}% net worth each hour for the rest of the day (UTC).*");
            foreach (string note in result.Notes.Where(n => !string.IsNullOrEmpty(n)))
                text.Append("\n\n").Append(note);
            return text.ToString();
        }

        /// <summary>
        /// Spin again, scale your last bets, or place new ones. They carry no owner: anyone can press them, and each press plays
        /// for whoever pressed it, with their own last bets, so a whole channel can share one result message.
        /// </summary>
        public static MessageComponent Buttons()
        {
            string Id(string action) => ComponentId.Build(MoneyComponents.Roulette, action);
            return new ComponentBuilder()
                .WithButton("Spin again", Id("spin"), ButtonStyle.Success, new Emoji("🎡"), row: 0)
                .WithButton("Bets ½", Id("half"), ButtonStyle.Secondary, row: 0)
                .WithButton("Bets x2", Id("double"), ButtonStyle.Secondary, row: 0)
                .WithButton("All in", Id("max"), ButtonStyle.Danger, row: 0)
                .WithButton("New bets…", Id("bet"), ButtonStyle.Primary, row: 1)
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
