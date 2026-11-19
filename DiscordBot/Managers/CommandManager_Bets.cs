using Common.Helpers;
using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using DiscordBot.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Managers
{
    /// <summary>
    /// Calls and puts (`.call`, `.put`, `.options`): leveraged bets on where a stock closes. Rules and pricing are in
    /// <see cref="LeverageRules"/> and <see cref="LeveragePricer"/>; money in <see cref="StockService.OpenBet"/>.
    /// </summary>
    public partial class CommandManager
    {
        private Task CallOption(DiscordSocketClient client, SocketMessage message, List<string> args) => BetCommand(message, args, isCall: true);

        private Task PutOption(DiscordSocketClient client, SocketMessage message, List<string> args) => BetCommand(message, args, isCall: false);

        /// <summary>`.call SYMBOL [amount] [2x|5x|10x] [15m..24h]`: the extras in any order. No amount shows what $1,000 would do.</summary>
        private async Task BetCommand(SocketMessage message, List<string> args, bool isCall)
        {
            if (args.Count < 1) throw new BadSyntaxException();
            string symbol = args[0];
            string amount = null;
            int leverage = LeverageRules.DefaultLeverage;
            TimeSpan duration = LeverageRules.DefaultDuration;
            foreach (string arg in args.Skip(1))
            {
                if (LeverageRules.TryParseLeverage(arg, out int l)) leverage = l;
                else if (DurationParser.TryParse(arg, out TimeSpan d)) duration = d;
                else if (amount == null) amount = arg;
                else throw new BadSyntaxException();
            }

            if (amount == null)
            {
                var quote = _stockService.QuoteBet(symbol, isCall, leverage, duration);
                string how = quote.TooDear ? "\n❌ Too likely to pay right now, so it's not for sale. Use less leverage or a shorter bet." : "";
                await message.Channel.SendMessageAsync($"**What $1,000 on a {leverage}x {quote.Stock.Symbol} {(isCall ? "call" : "put")} for {FormatRemaining(duration)} gets you:**\n"
                    + BetPayoffText(isCall, leverage, quote.Stock.FairPrice, 1000, Math.Round(1000 * quote.RidingShare, 2), duration, quote.ExpectedMultiple) + how
                    + $"\nDo it: `.{(isCall ? "call" : "put")} {quote.Stock.Symbol} 1000 {leverage}x {FormatDurationArg(duration)}` (or 10%, max).");
                return;
            }

            EnsureNotBetting(message.Author.Id, "betting on stocks");
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            var (bet, betQuote) = _stockService.OpenBet(account, message.Channel.Id, symbol, isCall, amount, leverage, duration);
            string arrow = isCall ? "📈 UP" : "📉 DOWN";
            await message.Channel.SendMessageAsync($"{message.Author.Mention} just put **${Dollars(bet.Paid)}** on **{bet.Symbol}** going **{arrow}** at {bet.Leverage}x. Results in {FormatRemaining(duration)}.\n"
                + BetPayoffText(isCall, bet.Leverage, bet.EntryPrice, bet.Paid, bet.Stake, duration, betQuote.ExpectedMultiple)
                + $"\nCash left: ${Dollars(account.NetWorth)}. `.options` to watch it sweat.");
        }

        private static string FormatDurationArg(TimeSpan d) => d.TotalHours >= 1 && d.TotalMinutes % 60 == 0 ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalMinutes}m";

        private static string Dollars(double amount) => FormatHelper.GetCommaNumber(Math.Round(amount, 2));

        /// <summary>
        /// The payout table for a bet: the fee, what wipes it out, where it breaks even, a couple of good outcomes and the max.
        /// All at the close, on the fair price.
        /// </summary>
        public static string BetPayoffText(bool isCall, int leverage, double entry, double paid, double stake, TimeSpan duration, double? expectedMultiple = null)
        {
            double fee = Math.Round(paid - stake, 2);
            string Price(double p) => "$" + Stock.FormatPrice(p);
            string Pct(double move) => (move >= 0 ? "+" : "") + (move * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
            double At(double move) => LeverageRules.Value(stake, isCall, leverage, entry, entry * (1 + (isCall ? move : -move)));
            double priceFor(double move) => entry * (1 + (isCall ? move : -move));

            double ridingShare = paid > 0 ? stake / paid : 1;
            double breakEven = LeverageRules.BreakEvenMove(ridingShare, leverage);
            double wipe = LeverageRules.WipeoutPrice(isCall, leverage, entry);
            double? max = LeverageRules.MaxOutPrice(isCall, leverage, entry);
            var lines = new List<string>
            {
                $"${Dollars(stake)} rides at {leverage}x (${Dollars(fee)} fee into the jackpot). Now {Price(entry)}. Paid out on the price in {FormatRemaining(duration)}:",
                $"💀 {Price(wipe)} or {(isCall ? "lower" : "higher")} ({Pct(isCall ? wipe / entry - 1 : wipe / entry - 1)}): wiped out, **$0**",
                $"😐 {Price(priceFor(breakEven))} ({Pct(isCall ? breakEven : -breakEven)}): you get your ${Dollars(paid)} back",
            };
            foreach (double move in new[] { 0.05, 0.10, 0.25 })
            {
                double value = At(move);
                if (max.HasValue && (isCall ? priceFor(move) >= max.Value : priceFor(move) <= max.Value)) break;
                lines.Add($"🤑 {Price(priceFor(move))} ({Pct(isCall ? move : -move)}): **${Dollars(value)}** ({value / paid:0.0}x)");
            }
            if (max.HasValue)
                lines.Add($"🚀 {Price(max.Value)} or {(isCall ? "higher" : "lower")} ({Pct(max.Value / entry - 1)}): maxed out at **${Dollars(stake * LeverageRules.MaxMultiple)}**");
            else
                lines.Add($"🚀 If {Price(0)}: **${Dollars(At(1))}**, the most a {leverage}x put can make");
            if (expectedMultiple.HasValue && paid > 0)
            {
                double payback = expectedMultiple.Value * stake / paid;
                lines.Add(payback < LeverageRules.PoorPayback
                    ? $"⚠️ On average this pays back **{payback:P0}** of what you put in. The stock's likely to move against you. Your funeral."
                    : $"🎲 On average it pays back {payback:P0} of what you put in. The rest is the house's.");
            }
            return string.Join("\n", lines);
        }

        /// <summary>One open bet on `.options` / `.portfolio`.</summary>
        private static string BetLine(LeveragedBet bet, double fair)
        {
            double value = bet.ValueAt(fair);
            double move = LeverageRules.Move(bet.IsCall, bet.EntryPrice, fair);
            string mood = value <= 0 ? "💀" : value >= bet.Paid ? "🤑" : "😬";
            return $"{mood} {bet.Leverage}x **{bet.Symbol}** {bet.Kind} · in at ${Stock.FormatPrice(bet.EntryPrice)}, now ${Stock.FormatPrice(fair)} ({move * 100:+0.0;-0.0;0.0}% your way) · "
                + $"pays **${Dollars(value)}** if it closed now (paid ${Dollars(bet.Paid)}) · {FormatRemaining(bet.ExpiresAt - DateTime.UtcNow)} left";
        }

        private async Task Options(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var market = _stockService.GetMarket();
            var bets = market.GetBets(message.Author.Id).OrderBy(b => b.ExpiresUtcTicks).ToList();
            var old = market.GetOptions(message.Author.Id).OrderBy(o => o.ExpiresAtUtcTicks).ToList();
            market.RecentSettled.TryGetValue(message.Author.Id, out var recent);

            if (bets.Count == 0 && old.Count == 0 && (recent == null || recent.Count == 0))
            {
                await message.Channel.SendMessageAsync($"{message.Author.Mention} you've got no calls or puts. Grow a pair: `.call SYMBOL` shows what a bet would pay.");
                return;
            }

            var lines = new List<string>();
            if (bets.Count > 0 || old.Count > 0) lines.Add("**Open**");
            foreach (var bet in bets)
                lines.Add(BetLine(bet, market.Find(bet.Symbol)?.FairPrice ?? bet.EntryPrice));
            foreach (var option in old)
            {
                double fair = market.Find(option.Symbol)?.FairPrice ?? option.Strike;
                lines.Add($"🎟️ old {option.Contracts}x **{option.Symbol}** {(option.IsCall ? "call" : "put")} strike ${Stock.FormatPrice(option.Strike)} · {FormatRemaining(option.ExpiresAt - DateTime.UtcNow)} left · worth ${Dollars(option.PayoutAt(fair))} now");
            }
            if (recent != null && recent.Count > 0)
                lines.Add("\n**Recently closed**\n" + string.Join("\n", recent));
            await message.SendRichEmbedMessage($"{message.Author.Username}'s calls and puts", string.Join("\n", lines));
        }

        /// <summary>Posts each call and put that closed, in the channel it was made in.</summary>
        private async Task AnnounceSettledBets(DiscordSocketClient client)
        {
            foreach (var settled in _stockService.TakeSettledBets())
            {
                var bet = settled.Bet;
                if (!(client.GetChannel(bet.ChannelId) is IMessageChannel channel)) continue;
                string name = _coinService.GetAll().Accounts.FirstOrDefault(a => a.UserId == bet.UserId)?.Name ?? "someone";
                string move = $"{bet.Symbol} closed at ${Stock.FormatPrice(settled.ClosePrice)} (in at ${Stock.FormatPrice(bet.EntryPrice)})";
                string text = settled.Payout <= 0
                    ? $"💀 **{name}**'s {bet.Leverage}x {bet.Symbol} {bet.Kind} got **wiped the fuck out**. ${Dollars(bet.Paid)} gone. {move}."
                    : settled.Payout >= bet.Paid
                        ? $"🤑 **{name}**'s {bet.Leverage}x {bet.Symbol} {bet.Kind} paid **${Dollars(settled.Payout)}** on ${Dollars(bet.Paid)} ({settled.Payout / bet.Paid:0.0}x). {move}."
                        : $"😬 **{name}**'s {bet.Leverage}x {bet.Symbol} {bet.Kind} limped home with ${Dollars(settled.Payout)} of their ${Dollars(bet.Paid)}. {move}.";
                try
                {
                    await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Couldn't announce a settled bet in {Channel}", bet.ChannelId);
                }
            }
        }
    }
}
