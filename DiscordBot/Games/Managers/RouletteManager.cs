using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Games.Managers
{
    /// <summary>
    /// Roulette: reading bets, playing a spin and remembering each player's last bets for the buttons. A spin takes the bets and
    /// settles them through <see cref="BetManager"/> before anything is shown, so a failed message can't lose money. There is no shared
    /// table: every press of a button plays for whoever pressed it.
    /// </summary>
    public class RouletteManager
    {
        /// <summary>The share of cash a spin must put down at least (see <see cref="BetManager.InitiateBet(CoinAccount, double, int?)"/>).</summary>
        public const int MinimumPercent = 10;
        /// <summary>Last bets are kept this long for the buttons.</summary>
        public static readonly TimeSpan Remembered = TimeSpan.FromHours(6);

        private readonly CoinService _coinService;
        private readonly BetManager _betManager;
        private readonly AppSettings _appSettings;
        private readonly Dictionary<ulong, (List<RouletteChoice> Bets, DateTime At)> _last = new Dictionary<ulong, (List<RouletteChoice>, DateTime)>();
        private readonly object _sync = new object();

        public RouletteManager(CoinService coinService, BetManager betManager, AppSettings appSettings)
        {
            _coinService = coinService;
            _betManager = betManager;
            _appSettings = appSettings;
        }

        public bool PrestigeDisabled => _appSettings.PrestigeDisabled;

        /// <summary>The bets this player last played, for Spin again and the scaling buttons. False when they have none (or it was a while ago).</summary>
        public bool TryLastBets(ulong userId, out List<RouletteChoice> bets)
        {
            lock (_sync)
            {
                if (_last.TryGetValue(userId, out var last) && DateTime.UtcNow - last.At <= Remembered)
                {
                    bets = last.Bets.ToList();
                    return true;
                }
            }
            bets = null;
            return false;
        }

        private void Remember(ulong userId, List<RouletteChoice> bets)
        {
            lock (_sync)
            {
                _last[userId] = (bets.ToList(), DateTime.UtcNow);
                foreach (ulong stale in _last.Where(p => DateTime.UtcNow - p.Value.At > Remembered).Select(p => p.Key).ToList())
                    _last.Remove(stale);
            }
        }

        /// <summary>
        /// Reads bets from typed words: "red 1000 17 50" (spot then amount), "red-1000,17-50", or just "red" for the minimum bet (10% of your cash). Amounts are 1000, 2.5k, 25%, max...
        /// (see <see cref="Money.TryParse"/>, percentages of <paramref name="cash"/>). Throws <see cref="BadSyntaxException"/> or
        /// <see cref="BadInputException"/> for something that isn't a bet, an invalid mix, or more than the cash.
        /// </summary>
        public static List<RouletteChoice> ParseBets(IList<string> words, double cash)
        {
            var tokens = words.SelectMany(w => w.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
            if (tokens.Count == 0) throw new BadSyntaxException();

            //each bet is a spot then an amount ("red 1000", "17-50"), and with no amount it is the minimum bet, 10% of your cash
            var bets = new List<RouletteChoice>();
            for (int i = 0; i < tokens.Count; i++)
            {
                string spotText, amountText;
                if (tokens[i].Contains('-'))
                {
                    string[] parts = tokens[i].Split('-');
                    if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) throw new BadSyntaxException();
                    (spotText, amountText) = (parts[0], parts[1]);
                }
                else
                {
                    spotText = tokens[i];
                    bool amountFollows = i + 1 < tokens.Count && !tokens[i + 1].Contains('-') && !IsNamedSpot(tokens[i + 1]);
                    amountText = amountFollows ? tokens[++i] : "min";
                }

                if (!Money.TryParse(amountText, cash, out double amount))
                    throw new BadSyntaxException();
                var spot = new RouletteBet(0, spotText, 1);
                bool badNumber = spot.RoulleteBetType == BetType.Number && (spot.BetNumberChoice == null || spot.BetNumberChoice < -1 || spot.BetNumberChoice > 36);
                if (spot.RoulleteBetType == BetType.NotValid || badNumber)
                    throw new BadInputException($"`{spotText}` isn't something you can bet on. {RouletteView.HowToBet}");
                bets.Add(new RouletteChoice { Choice = spotText, Amount = amount });
            }

            //refused here, before any stake is taken, so a bad bet never counts as a game played
            new Roulette().EnsureValidCombinations(bets.Select(b => new RouletteBet(0, b.Choice, b.Amount)).ToList());
            double total = bets.Sum(b => b.Amount);
            if (total > cash + 0.01) //a cent of slack, so "max" (all your cash, cents included) is never refused
                throw new BadInputException($"Those bets add up to ${total.ToString("N2", CultureInfo.InvariantCulture)} and you only have ${cash.ToString("N2", CultureInfo.InvariantCulture)}.");
            return bets;
        }

        /// <summary>A named spot (red, odd, firstdozen...) as opposed to a number, which could also be an amount.</summary>
        private static bool IsNamedSpot(string word) =>
            !int.TryParse(word, out _) && Enum.TryParse<BetType>(word, true, out var type) && type != BetType.NotValid && type != BetType.Number;

        /// <summary>The bets held to the cash: a cent over is taken off the biggest bet, more than that scales them all down together.</summary>
        private static List<RouletteChoice> HeldToCash(List<RouletteChoice> bets, double cash)
        {
            double total = bets.Sum(b => b.Amount);
            if (total <= cash) return bets;
            if (total - cash < 0.02)
            {
                var biggest = bets.OrderByDescending(b => b.Amount).First();
                return bets.Select(b => b == biggest ? new RouletteChoice { Choice = b.Choice, Amount = Math.Floor((b.Amount - (total - cash)) * 100) / 100 } : b).ToList();
            }
            return RouletteView.Scale(bets, "keep", cash);
        }

        /// <summary>
        /// Takes the bets (held to the player's cash), spins and settles it. Throws <see cref="BadInputException"/> when the player has no
        /// cash or no bets, already has a bet open, or the bets are under the minimum.
        /// </summary>
        public async Task<RouletteResult> Spin(ulong userId, string name, List<RouletteChoice> wanted)
        {
            CoinAccount account = await _coinService.Get(userId, name);
            if (Math.Floor(account.NetWorth) < 1)
                throw new BadInputException("You're out of cash to bet. Wait for the hourly $1,000.");

            var bets = HeldToCash(wanted, account.NetWorth);
            if (bets.Count == 0)
                throw new BadInputException("There are no bets to play. Press **New bets…**.");
            var inputBets = bets.Select(b => new RouletteBet(userId, b.Choice, b.Amount)).ToList();
            double staked = inputBets.Sum(b => b.Amount);

            await _betManager.InitiateBet(account, staked, MinimumPercent);

            Tuple<List<string>, List<RouletteBet>> played;
            try
            {
                played = new Roulette().Play(inputBets);
            }
            catch (Exception)
            {
                await _betManager.CancelBet(userId, name, staked);
                throw;
            }

            var winning = played.Item2;
            double baseWinnings = winning.Sum(w => w.Amount + w.PayoutMultiple * w.Amount);
            var resolved = await _betManager.ResolveBet(userId, name, staked, baseWinnings, game: "Roulette");
            Remember(userId, bets);

            var notes = new[]
            {
                _betManager.TakeDailyRoll(userId)?.ToString(),
                _betManager.TakeJackpotWin(userId),
            };
            return new RouletteResult
            {
                Name = name,
                Pocket = RouletteView.PocketName(played.Item1[0]),
                Tags = played.Item1.Skip(1).Where(t => t != "None").Select(RouletteView.Pretty).ToArray(),
                Lines = inputBets.Select(b =>
                {
                    bool won = winning.Contains(b);
                    return new RouletteLine { Label = RouletteView.Label(b), Amount = b.Amount, Won = won, Back = won ? b.Amount + b.PayoutMultiple * b.Amount : 0 };
                }).ToList(),
                Staked = staked,
                TotalPaid = resolved.TotalWinnings,
                Bonus = resolved.BonusWinnings,
                Cash = account.NetWorth,
                BonusGranted = resolved.WasBonusGranted,
                Notes = notes.Where(n => n != null).ToArray(),
            };
        }
    }
}
