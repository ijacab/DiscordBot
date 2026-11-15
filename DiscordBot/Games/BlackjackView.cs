using Common.Helpers;
using Discord;
using DiscordBot.Games.Models;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static DiscordBot.Games.Blackjack;

namespace DiscordBot.Games
{
    /// <summary>
    /// The blackjack table: one embed that is edited in place from the lobby, through the hands, to the results.
    /// Pure text, so it can be tested without Discord.
    /// </summary>
    public static class BlackjackView
    {
        public const string Title = "🃏 Blackjack";
        private const string Rule = "──────────";

        /// <summary>The table before the deal: who's sitting down and for how much.</summary>
        public static Embed Lobby(Blackjack game)
        {
            var text = new StringBuilder();
            foreach (var player in Players(game))
                text.AppendLine($"**{player.Username}** · ${FormatHelper.GetCommaNumber(player.BetAmount)}");
            text.AppendLine();
            text.Append("Press **Join…** to sit down and **Start** to deal.");
            return new EmbedBuilder().WithTitle($"{Title} · table open").WithDescription(text.ToString())
                .WithColor(Color.Green).WithFooter("or type .bj 1000 and .bj start").Build();
        }

        /// <summary>The hands while players decide. The dealer shows one card.</summary>
        public static Embed InPlay(Blackjack game)
        {
            var dealer = game.GetDealer();
            var text = new StringBuilder();
            text.AppendLine($"**Dealer** {Hand(dealer.Cards)} `??` · showing **{BestTotal(dealer)}**");
            text.AppendLine(Rule);
            foreach (var player in Players(game))
                text.AppendLine($"**{player.Username}** · ${FormatHelper.GetCommaNumber(player.BetAmount)} · {Hand(player.Cards)} {Total(player)} · {Status(player)}");
            return new EmbedBuilder().WithTitle(Title).WithDescription(text.ToString())
                .WithColor(Color.Blue).WithFooter("Hit or Stay. The table settles itself after 90 seconds.").Build();
        }

        /// <summary>One player's line in the results.</summary>
        public sealed class ResultLine
        {
            public BlackjackPlayer Player { get; init; }
            public BlackjackResultType Result { get; init; }
            public double NetWorth { get; init; }
            /// <summary>Extra lines under the player, like the daily roll or a jackpot win.</summary>
            public IReadOnlyList<string> Notes { get; init; } = new List<string>();
        }

        /// <summary>The dealer's final hand and how everyone did.</summary>
        public static Embed Results(Blackjack game, IEnumerable<ResultLine> lines)
        {
            var dealer = game.GetDealer();
            var text = new StringBuilder();
            text.AppendLine($"**Dealer** {Hand(dealer.Cards)} {Total(dealer)}");
            text.AppendLine(Rule);
            foreach (var line in lines)
            {
                var player = line.Player;
                string bonus = player.BonusWinnings > 0 ? $" (+${FormatHelper.GetCommaNumber(player.BonusWinnings)} bonus)" : "";
                text.AppendLine($"{Icon(line.Result, player)} **{player.Username}** {Label(line.Result, player)} · {Hand(player.Cards)} {Total(player)}");
                text.AppendLine($"⠀↳ ${FormatHelper.GetCommaNumber(player.BetAmount)} → ${FormatHelper.GetCommaNumber(player.BaseWinnings)}{bonus} · cash ${FormatHelper.GetCommaNumber(line.NetWorth)}");
                foreach (string note in line.Notes)
                    text.AppendLine($"⠀↳ {note}");
            }
            string description = text.ToString();
            if (description.Length > 4000) description = description.Substring(0, 3999) + "…"; //an embed description holds 4096
            return new EmbedBuilder().WithTitle($"{Title} · results").WithDescription(description).WithColor(Color.Gold).Build();
        }

        public static string Card(Card card) => $"`{card.Name}{SuitSymbol(card.Suit)}`";

        public static string Hand(IEnumerable<Card> cards) => string.Join(" ", cards.Select(Card));

        private static string SuitSymbol(DiscordBot.Games.Models.Suit suit) => suit switch
        {
            DiscordBot.Games.Models.Suit.Hearts => "♥",
            DiscordBot.Games.Models.Suit.Diamonds => "♦",
            DiscordBot.Games.Models.Suit.Clubs => "♣",
            _ => "♠",
        };

        /// <summary>The best total that isn't bust, or 0 when every total is.</summary>
        public static int BestTotal(BlackjackPlayer player) => player.GetPossibleTotalValues().DefaultIfEmpty(0).Max();

        /// <summary>The hand's total in bold, "soft" when an ace could still count as 1, or bust.</summary>
        public static string Total(BlackjackPlayer player)
        {
            var totals = player.GetPossibleTotalValues().ToList();
            if (totals.Count == 0) return $"**bust** ({player.GetPossibleTotalValues(false).Min()})";
            int best = totals.Max();
            return totals.Count > 1 ? $"**soft {best}**" : $"**{best}**";
        }

        /// <summary>Where a player is while the table is in play.</summary>
        public static string Status(BlackjackPlayer player)
        {
            if (!player.GetPossibleTotalValues().Any()) return "💥 bust";
            if (!player.IsFinishedPlaying) return "🎴 deciding";
            if (player.Cards.Count >= 5) return "🖐️ five cards";
            if (BestTotal(player) == 21) return "🎯 21";
            return "✋ stood";
        }

        public static string Icon(BlackjackResultType result, BlackjackPlayer player) => result switch
        {
            BlackjackResultType.Lose => player.GetPossibleTotalValues().Any() ? "❌" : "💥",
            BlackjackResultType.Draw => "➖",
            BlackjackResultType.Win => "✅",
            _ => "🎯",
        };

        public static string Label(BlackjackResultType result, BlackjackPlayer player) => result switch
        {
            BlackjackResultType.Lose => player.GetPossibleTotalValues().Any() ? "lost" : "bust",
            BlackjackResultType.Draw => "push",
            BlackjackResultType.Win => "won",
            BlackjackResultType.WinTwentyOne => "hit 21",
            BlackjackResultType.WinFiveCard => "five-card win",
            BlackjackResultType.WinFiveCardTwentyOne => "five-card 21",
            BlackjackResultType.WinTwentyToTwentyOne => "20 → 21",
            BlackjackResultType.WinFiveCardTwentyToTwentyOne => "five-card 20 → 21",
            _ => "won",
        };

        private static IEnumerable<BlackjackPlayer> Players(Blackjack game) => game.Players.Where(p => !p.IsDealer);
    }
}
