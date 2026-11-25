using Discord;
using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Models;
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
    /// <summary>One player's open roulette table: their bets, what it last showed and how the session is going. Lives in memory.</summary>
    public sealed class RouletteSession
    {
        public ulong UserId { get; init; }
        public string Name { get; init; }
        public List<RouletteChoice> Bets { get; set; } = new List<RouletteChoice>();
        public int Spins { get; set; }
        public double SessionNet { get; set; }
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        /// <summary>Held for the whole of a spin, so a double click can't start two.</summary>
        public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Roulette tables: one message per player, edited in place with Spin again / bet buttons. A spin takes the bets and settles
    /// them through <see cref="BetManager"/> before anything is shown, so a failed edit can't lose money.
    /// </summary>
    public class RouletteManager
    {
        public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);
        /// <summary>The share of cash a spin must put down at least (see <see cref="BetManager.InitiateBet(CoinAccount, double, int?)"/>).</summary>
        public const int MinimumPercent = 10;

        private readonly CoinService _coinService;
        private readonly BetManager _betManager;
        private readonly AppSettings _appSettings;
        private readonly ILogger<RouletteManager> _logger;
        private readonly Dictionary<ulong, RouletteSession> _sessions = new Dictionary<ulong, RouletteSession>(); //by table message id
        private readonly object _sync = new object();

        public RouletteManager(CoinService coinService, BetManager betManager, AppSettings appSettings, ILogger<RouletteManager> logger)
        {
            _coinService = coinService;
            _betManager = betManager;
            _appSettings = appSettings;
            _logger = logger;
        }

        public RouletteSession Open(ulong userId, string name) => new RouletteSession { UserId = userId, Name = name };

        /// <summary>Ties a session to its message. A player has one table at a time, so their earlier ones close.</summary>
        public void Attach(ulong messageId, RouletteSession session)
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

        public bool TryGet(ulong messageId, out RouletteSession session)
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

        /// <summary>
        /// Reads bets from typed words: "red 1000 17 50" (pairs), "red-1000,17-50" or "red-1000". Amounts are 1000, 2.5k, 25%, max...
        /// (see <see cref="Money.TryParse"/>, percentages of <paramref name="cash"/>). Throws <see cref="BadSyntaxException"/> or
        /// <see cref="BadInputException"/> for something that isn't a bet.
        /// </summary>
        public static List<RouletteChoice> ParseBets(IList<string> words, double cash)
        {
            var args = words.SelectMany(w => w.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
            var pairs = new List<string>();
            if (args.Count > 0 && args.All(a => !a.Contains('-')))
            {
                if (args.Count % 2 != 0) throw new BadSyntaxException();
                for (int i = 0; i < args.Count; i += 2) pairs.Add($"{args[i]}-{args[i + 1]}");
            }
            else
                pairs = args;
            if (pairs.Count == 0) throw new BadSyntaxException();

            var bets = new List<RouletteChoice>();
            foreach (string pair in pairs)
            {
                string[] parts = pair.Split('-');
                if (parts.Length != 2 || !Money.TryParse(parts[1], cash, out double amount))
                    throw new BadSyntaxException();
                var spot = new RouletteBet(0, parts[0], 1);
                bool badNumber = spot.RoulleteBetType == BetType.Number && (spot.BetNumberChoice == null || spot.BetNumberChoice < -1 || spot.BetNumberChoice > 36);
                if (spot.RoulleteBetType == BetType.NotValid || badNumber)
                    throw new BadInputException($"`{parts[0]}` isn't something you can bet on. {RouletteView.HowToBet}");
                bets.Add(new RouletteChoice { Choice = parts[0], Amount = amount });
            }

            //refused here, before any stake is taken, so a bad table never counts as a game played
            new Roulette().EnsureValidCombinations(bets.Select(b => new RouletteBet(0, b.Choice, b.Amount)).ToList());
            double total = bets.Sum(b => b.Amount);
            if (total > Math.Floor(cash))
                throw new BadInputException($"Those bets add up to ${Common.Helpers.FormatHelper.GetCommaNumber(total)} and you only have ${Common.Helpers.FormatHelper.GetCommaNumber(Math.Floor(cash))}.");
            return bets;
        }

        /// <summary>The table waiting for a spin: the last result (or none), the bets and the cash.</summary>
        public RouletteScreen Idle(RouletteSession session, double cash, string status = null)
        {
            return new RouletteScreen
            {
                Name = session.Name,
                Pocket = null,
                Lines = session.Bets.Select(b => new RouletteLine { Label = RouletteView.Label(new RouletteBet(session.UserId, b.Choice, b.Amount)), Amount = b.Amount }).ToList(),
                Status = status ?? (session.Bets.Count == 0
                    ? "Place your bets with **New bets…**."
                    : session.Spins == 0 ? "Your bets are down. Spin when you're ready." : "Spin again, or change your bets."),
                Colour = Color.DarkGrey,
                Cash = cash,
                Spins = session.Spins,
                SessionNet = session.SessionNet,
            };
        }

        /// <summary>
        /// Takes the table's bets (held to the player's cash), spins and settles it. Throws <see cref="BadInputException"/> when the
        /// player has no cash, no bets or already has a bet open, or the bets are under the minimum.
        /// </summary>
        public async Task<RouletteScreen> Spin(RouletteSession session, Random random = null)
        {
            CoinAccount account = await _coinService.Get(session.UserId, session.Name);
            if (Math.Floor(account.NetWorth) < 1)
                throw new BadInputException("You're out of cash to bet. Wait for the hourly $1,000.");
            if (session.Bets.Count == 0)
                throw new BadInputException("There are no bets on the table. Press **New bets…**.");

            //an old table's bets may be more than the cash now: hold them to it, all together
            double asked = session.Bets.Sum(b => b.Amount);
            if (asked > Math.Floor(account.NetWorth))
                session.Bets = RouletteView.Scale(session.Bets, "keep", account.NetWorth);
            var inputBets = session.Bets.Select(b => new RouletteBet(session.UserId, b.Choice, b.Amount)).ToList();
            double staked = inputBets.Sum(b => b.Amount);

            await _betManager.InitiateBet(account, staked, MinimumPercent);

            Tuple<List<string>, List<RouletteBet>> played;
            try
            {
                played = new Roulette().Play(inputBets);
            }
            catch (Exception)
            {
                await _betManager.CancelBet(session.UserId, session.Name, staked);
                throw;
            }

            var winning = played.Item2;
            double baseWinnings = winning.Sum(w => w.Amount + w.PayoutMultiple * w.Amount);
            var resolved = await _betManager.ResolveBet(session.UserId, session.Name, staked, baseWinnings, game: "Roulette");

            var notes = new List<string>
            {
                _betManager.TakeDailyRoll(session.UserId)?.ToString(),
                _betManager.TakeJackpotWin(session.UserId),
            };
            if (resolved.WasBonusGranted)
                notes.Add(_appSettings.PrestigeDisabled
                    ? $"📈 You'll get a bonus {Constants.InterestPercentage}% net worth each hour for the rest of the day (UTC)."
                    : $"📈 You'll get a bonus $1000 + {Constants.InterestPercentage}% net worth each hour for the rest of the day (UTC).");

            var lines = inputBets.Select(b =>
            {
                bool won = winning.Contains(b);
                return new RouletteLine { Label = RouletteView.Label(b), Amount = b.Amount, Won = won, Back = won ? b.Amount + b.PayoutMultiple * b.Amount : 0 };
            }).ToList();

            session.Spins++;
            session.SessionNet += resolved.TotalWinnings - staked;
            session.LastUsed = DateTime.UtcNow;

            var (status, colour) = RouletteView.Outcome(staked, resolved.TotalWinnings, resolved.BonusWinnings);
            var screen = new RouletteScreen
            {
                Name = session.Name,
                Pocket = RouletteView.PocketName(played.Item1[0]),
                Tags = played.Item1.Skip(1).Where(t => t != "None").Select(RouletteView.Pretty).ToArray(),
                Lines = lines,
                Status = status,
                Colour = colour,
                Cash = account.NetWorth,
                Spins = session.Spins,
                SessionNet = session.SessionNet,
                Notes = notes.Where(n => n != null).ToArray(),
            };
            return screen;
        }

        /// <summary>One spin on the table message: settles it, then shows the result with a single edit. False if the table is already spinning.</summary>
        public async Task<bool> Play(IUserMessage message, RouletteSession session)
        {
            if (!await session.Gate.WaitAsync(0))
                return false;
            try
            {
                RouletteScreen screen = await Spin(session);
                await Show(message, session, screen);
                return true;
            }
            finally
            {
                session.Gate.Release();
            }
        }

        public async Task Show(IUserMessage message, RouletteSession session, RouletteScreen screen)
        {
            var embed = RouletteView.Build(screen);
            var buttons = RouletteView.Buttons(session.UserId, session.Bets.Sum(b => b.Amount), screen.Cash);
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
                //the table message is gone (deleted?), so carry on at the bottom of the channel
                _logger.LogWarning(ex, "Roulette result edit failed, reposting: {Message}", ex.Message);
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
