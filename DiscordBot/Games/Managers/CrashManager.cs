using Discord;
using DiscordBot.Exceptions;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    /// <summary>One player's launch. Lives in memory; it's over in under a minute.</summary>
    public sealed class CrashRound
    {
        public ulong UserId { get; init; }
        public string Name { get; init; }
        public double Bet { get; init; }
        public double CrashAt { get; init; }
        public double? AutoCashOut { get; init; }
        public DateTime LaunchedAt { get; init; }
        public IUserMessage Message { get; set; }
        public bool Finished { get; set; }
        /// <summary>One settle at a time: a click and the loop can't both pay.</summary>
        public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Crash's money and flow. The bet goes through <see cref="BetManager"/> like every other game (daily roll, jackpot, stats,
    /// 75% of losses into the jackpot), and the round's message is edited as the rocket climbs.
    /// </summary>
    public class CrashManager
    {
        public const string ComponentGame = "crash";
        private readonly CoinService _coinService;
        private readonly BetManager _betManager;
        private readonly ILogger<CrashManager> _logger;
        private readonly ConcurrentDictionary<ulong, CrashRound> _rounds = new ConcurrentDictionary<ulong, CrashRound>();

        public CrashManager(CoinService coinService, BetManager betManager, ILogger<CrashManager> logger)
        {
            _coinService = coinService;
            _betManager = betManager;
            _logger = logger;
        }

        /// <summary>Takes the bet and launches. The round runs in the background until it crashes or is cashed out.</summary>
        public async Task Launch(CoinAccount account, double bet, double? autoCashOut, IMessageChannel channel, Random random = null)
        {
            if (_rounds.ContainsKey(account.UserId))
                throw new BadInputException("You've already got a rocket in the air, you lunatic. Cash out or watch it burn first.");
            await _betManager.InitiateBet(account, bet);
            var round = new CrashRound
            {
                UserId = account.UserId,
                Name = account.Name,
                Bet = bet,
                CrashAt = CrashGame.CrashPoint((random ?? Random.Shared).NextDouble()),
                AutoCashOut = autoCashOut,
                LaunchedAt = DateTime.UtcNow,
            };
            _rounds[account.UserId] = round;
            try
            {
                round.Message = await channel.SendMessageAsync(embed: Flying(round, 1.00), components: CashOutButton(round));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Crash message failed; settling at once");
            }
            _ = Task.Run(() => Fly(round));
        }

        private async Task Fly(CrashRound round)
        {
            try
            {
                while (!round.Finished)
                {
                    await Task.Delay(CrashGame.TickEvery);
                    double now = CrashGame.MultiplierAt(DateTime.UtcNow - round.LaunchedAt);
                    if (now >= round.CrashAt || (round.AutoCashOut.HasValue && now >= round.AutoCashOut.Value) || now >= CrashGame.MaxMultiplier)
                    {
                        await Settle(round, clicked: false);
                        return;
                    }
                    if (round.Message != null)
                        await round.Message.ModifyAsync(m => m.Embed = Flying(round, now));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Crash round for {User} failed; settling", round.UserId);
                await Settle(round, clicked: false);
            }
        }

        /// <summary>The Cash out button. False when there's no rocket of theirs in the air.</summary>
        public async Task<bool> CashOut(ulong userId)
        {
            if (!_rounds.TryGetValue(userId, out var round)) return false;
            await Settle(round, clicked: true);
            return true;
        }

        /// <summary>
        /// Ends the round once. A click cashes out at the multiplier right now if the rocket hasn't blown yet; the loop ends it at
        /// the auto cash-out, the cap or the crash. The multiplier comes from the clock, not from what was last drawn.
        /// </summary>
        private async Task Settle(CrashRound round, bool clicked)
        {
            await round.Gate.WaitAsync();
            try
            {
                if (round.Finished) return;
                round.Finished = true;
                _rounds.TryRemove(round.UserId, out _);

                double now = CrashGame.MultiplierAt(DateTime.UtcNow - round.LaunchedAt);
                double? cashedAt = CashedAt(round, now);
                double winnings = cashedAt.HasValue ? Math.Floor(round.Bet * cashedAt.Value * 100) / 100 : 0;
                await _betManager.ResolveBet(round.UserId, round.Name, round.Bet, winnings, game: "Crash");
                CoinAccount account = await _coinService.Get(round.UserId, round.Name);
                string extra = string.Join("\n", new[] { _betManager.TakeDailyRoll(round.UserId)?.ToString(), _betManager.TakeJackpotWin(round.UserId) }
                    .Where(x => !string.IsNullOrEmpty(x)));
                if (round.Message != null)
                {
                    var embed = cashedAt.HasValue ? Landed(round, cashedAt.Value, winnings, account.NetWorth, extra) : Crashed(round, account.NetWorth, extra);
                    await round.Message.ModifyAsync(m => { m.Embed = embed; m.Components = AgainButton(round); });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Settling a crash round for {User} failed", round.UserId);
            }
            finally
            {
                round.Gate.Release();
            }
        }

        /// <summary>
        /// Where the round ended for the player, or null when it blew up first. An auto cash-out the rocket got to pays at the
        /// target, even if the loop noticed a moment late; reaching the cap pays the cap; otherwise they get what's on the clock
        /// if it hasn't blown yet (a click, or the loop failing).
        /// </summary>
        public static double? CashedAt(CrashRound round, double now)
        {
            if (round.AutoCashOut is double auto && auto <= round.CrashAt && auto <= now) return auto;
            if (now >= CrashGame.MaxMultiplier && round.CrashAt >= CrashGame.MaxMultiplier) return CrashGame.MaxMultiplier;
            if (now < round.CrashAt) return now;
            return null;
        }

        // ---- the message

        private static string Bar(double multiplier)
        {
            int filled = (int)Math.Clamp(Math.Round(Math.Log(multiplier) / Math.Log(CrashGame.MaxMultiplier) * 12), 0, 12);
            return new string('▰', filled) + new string('▱', 12 - filled);
        }

        private static Embed Flying(CrashRound r, double now)
        {
            string auto = r.AutoCashOut.HasValue ? $"\nAuto cash-out at **{r.AutoCashOut.Value:0.00}x**." : "";
            return new EmbedBuilder()
                .WithTitle($"🚀 {r.Name}'s rocket · {Money.Format(r.Bet)} on board")
                .WithDescription($"# {now:0.00}x\n{Bar(now)} 🚀\nCash out now for **{Money.Format(Math.Floor(r.Bet * now * 100) / 100)}**. It could blow any second.{auto}")
                .WithColor(Color.Gold)
                .Build();
        }

        private static Embed Landed(CrashRound r, double at, double winnings, double cash, string extra) => new EmbedBuilder()
            .WithTitle($"💰 {r.Name} bailed out at {at:0.00}x")
            .WithDescription($"Took **{Money.Format(winnings)}** ({Money.Signed(winnings - r.Bet)}). The rocket went on to {(r.CrashAt >= CrashGame.MaxMultiplier ? "the fucking moon" : $"blow at {r.CrashAt:0.00}x")}.\nCash: {Money.Format(cash)}" + (extra.Length > 0 ? "\n" + extra : ""))
            .WithColor(Color.Green)
            .Build();

        private static Embed Crashed(CrashRound r, double cash, string extra) => new EmbedBuilder()
            .WithTitle(r.CrashAt <= 1 ? $"💥 Blew up on the launch pad" : $"💥 CRASHED at {r.CrashAt:0.00}x")
            .WithDescription($"{r.Name} got greedy and lost **{Money.Format(r.Bet)}**. Should've bailed, dickhead.\nCash: {Money.Format(cash)}" + (extra.Length > 0 ? "\n" + extra : ""))
            .WithColor(Color.Red)
            .Build();

        private static MessageComponent CashOutButton(CrashRound r) => new ComponentBuilder()
            .WithButton("💰 Cash out", ComponentId.Build(ComponentGame, "out", r.UserId.ToString()), ButtonStyle.Success)
            .Build();

        private static MessageComponent AgainButton(CrashRound r) => new ComponentBuilder()
            .WithButton($"🚀 Again · {Money.Format(r.Bet)}", ComponentId.Build(ComponentGame, "again", r.UserId.ToString(), r.Bet.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.AutoCashOut?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""), ButtonStyle.Primary)
            .Build();
    }
}
