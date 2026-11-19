using Discord;
using DiscordBot.Exceptions;
using DiscordBot.Managers;
using DiscordBot.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    /// <summary>One player's open machine: its bet, what it last showed and how the session is going. Lives in memory.</summary>
    public sealed class SlotSession
    {
        public ulong UserId { get; init; }
        public string Name { get; init; }
        public double Bet { get; set; }
        public int Spins { get; set; }
        public double SessionNet { get; set; }
        public SlotSymbol[][] LastWindow { get; set; }
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        /// <summary>Held for the whole of a spin, so a double click can't start two.</summary>
        public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);
    }

    /// <summary>The settled result of one spin. The money has already moved when this exists.</summary>
    public sealed class SlotSpin
    {
        public SlotResult Result { get; init; }
        public double Bet { get; init; }
        public double TotalPaid { get; init; }
        public double Bonus { get; init; }
        public double Cash { get; init; }
        public string[] Notes { get; init; }
    }

    /// <summary>
    /// The slot machine's sessions and money. A spin takes the bet, spins and settles through <see cref="BetManager"/> before
    /// anything is drawn, so a failed edit or a restart half-way through can't lose money or leave a bet open.
    /// The result is then shown by editing one message, instantly. Losses go into the jackpot like any other bet, and three diamonds
    /// take the pool.
    /// </summary>
    public class SlotMachineManager
    {
        public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);
        /// <summary>The share of cash a new machine bets by default.</summary>
        public const double DefaultBetShare = 0.01;

        private readonly CoinService _coinService;
        private readonly BetManager _betManager;
        private readonly ILogger<SlotMachineManager> _logger;
        private readonly Random _random = new Random();
        private readonly Dictionary<ulong, SlotSession> _sessions = new Dictionary<ulong, SlotSession>(); //by machine message id
        private readonly object _sync = new object();

        public SlotMachineManager(CoinService coinService, BetManager betManager, ILogger<SlotMachineManager> logger)
        {
            _coinService = coinService;
            _betManager = betManager;
            _logger = logger;
        }

        /// <summary>A bet of $1 up to the player's cash, or 1% of their cash when they didn't say.</summary>
        public static double ClampBet(double wanted, double cash)
        {
            double ceiling = Math.Max(1, Math.Floor(cash));
            return Math.Clamp(Math.Floor(wanted), 1, ceiling);
        }

        public static double DefaultBet(double cash) => ClampBet(Math.Floor(cash * DefaultBetShare), cash);

        public SlotSession Open(ulong userId, string name, double bet) => new SlotSession { UserId = userId, Name = name, Bet = bet };

        /// <summary>Ties a session to its message. A player has one machine at a time, so their earlier ones close.</summary>
        public void Attach(ulong messageId, SlotSession session)
        {
            lock (_sync)
            {
                foreach (ulong old in _sessions.Where(s => s.Value.UserId == session.UserId).Select(s => s.Key).ToList())
                    _sessions.Remove(old);
                foreach (ulong stale in _sessions.Where(s => DateTime.UtcNow - s.Value.LastUsed > IdleLimit).Select(s => s.Key).ToList())
                    _sessions.Remove(stale);
                _sessions[messageId] = session;
            }
        }

        public bool TryGet(ulong messageId, out SlotSession session)
        {
            lock (_sync)
            {
                if (_sessions.TryGetValue(messageId, out session) && DateTime.UtcNow - session.LastUsed <= IdleLimit)
                    return true;
                _sessions.Remove(messageId);
                session = null;
                return false;
            }
        }

        public void Close(ulong messageId)
        {
            lock (_sync)
                _sessions.Remove(messageId);
        }

        /// <summary>The machine waiting for a spin: the last window (or blanks), the bet and the cash.</summary>
        public SlotScreen Idle(SlotSession session, double cash, string status = null) => new SlotScreen
        {
            Window = session.LastWindow,
            Status = status ?? (session.Spins == 0 ? "Pick your bet and pull the lever." : "Spin again, or change your bet."),
            Colour = Discord.Color.DarkGrey,
            Name = session.Name,
            Bet = session.Bet,
            Cash = cash,
            Spins = session.Spins,
            SessionNet = session.SessionNet,
        };

        /// <summary>
        /// Takes the bet, spins and settles it. Throws <see cref="BadInputException"/> when the player has no cash or already has a bet open.
        /// </summary>
        public async Task<SlotSpin> Spin(SlotSession session, Random random = null)
        {
            CoinAccount account = await _coinService.Get(session.UserId, session.Name);
            if (Math.Floor(account.NetWorth) < 1)
                throw new BadInputException("You're out of cash to bet. Wait for the hourly $1,000.");

            double bet = ClampBet(session.Bet, account.NetWorth);
            session.Bet = bet;
            await _betManager.InitiateBet(account, bet);

            SlotResult result;
            double baseWinnings;
            try
            {
                result = SlotMachine.Spin(random ?? _random);
                if (result.Jackpot)
                {
                    //the pool pays, up to the cap; if it holds less than the floor, the floor is still paid
                    baseWinnings = SlotMachine.JackpotPayout(bet, _coinService.JackpotPool);
                    _coinService.RemoveFromJackpot(baseWinnings);
                }
                else
                {
                    baseWinnings = Math.Floor(bet * result.Multiple);
                }
            }
            catch (Exception)
            {
                await _betManager.CancelBet(session.UserId, session.Name, bet);
                throw;
            }

            var resolved = await _betManager.ResolveBet(session.UserId, session.Name, bet, baseWinnings, game: "Slots");
            if (result.Jackpot)
                account.Stats.SlotJackpotsHit++;
            var notes = new List<string>
            {
                _betManager.TakeDailyRoll(session.UserId)?.ToString(),
                _betManager.TakeJackpotWin(session.UserId),
            };
            if (resolved.WasBonusGranted)
                notes.Add($"📈 You'll get a bonus {Constants.InterestPercentage}% net worth each hour for the rest of the day (UTC).");

            session.Spins++;
            session.SessionNet += resolved.TotalWinnings - bet;
            session.LastWindow = result.Window;
            session.LastUsed = DateTime.UtcNow;

            return new SlotSpin
            {
                Result = result,
                Bet = bet,
                TotalPaid = resolved.TotalWinnings,
                Bonus = resolved.BonusWinnings,
                Cash = account.NetWorth,
                Notes = notes.Where(n => n != null).ToArray(),
            };
        }

        /// <summary>
        /// One spin on the machine message: settles it, then shows the result with a single edit.
        /// Returns false if this machine is already spinning.
        /// </summary>
        public async Task<bool> Play(IUserMessage message, SlotSession session)
        {
            if (!await session.Gate.WaitAsync(0))
                return false;
            try
            {
                SlotSpin spin = await Spin(session);
                await Show(message, session, spin);
                return true;
            }
            finally
            {
                session.Gate.Release();
            }
        }

        /// <summary>Shows the result in one edit of the machine message.</summary>
        private async Task Show(IUserMessage message, SlotSession session, SlotSpin spin)
        {
            var (status, colour) = SlotView.Outcome(spin.Result, spin.Bet, spin.TotalPaid, spin.Bonus);
            var final = new SlotScreen
            {
                Window = spin.Result.Window,
                Status = status,
                Colour = colour,
                Name = session.Name,
                Bet = session.Bet,
                Cash = spin.Cash,
                Spins = session.Spins,
                SessionNet = session.SessionNet,
                Notes = spin.Notes,
            };
            var embed = SlotView.Build(final);
            var buttons = SlotView.Buttons(session.UserId, session.Bet, spin.Cash);
            try
            {
                await message.ModifyAsync(m =>
                {
                    m.Embed = embed;
                    m.Components = buttons;
                });
            }
            catch (Exception ex)
            {
                //the machine message is gone (deleted?), so carry on at the bottom of the channel
                _logger.LogWarning(ex, "Slot result edit failed, reposting: {Message}", ex.Message);
                var posted = await message.Channel.SendMessageAsync(embed: embed, components: buttons);
                lock (_sync)
                {
                    foreach (ulong key in _sessions.Where(s => s.Value == session).Select(s => s.Key).ToList())
                        _sessions.Remove(key);
                    _sessions[posted.Id] = session;
                }
            }
        }
    }
}
