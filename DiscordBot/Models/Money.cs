using Common.Helpers;
using DiscordBot.Exceptions;
using System;
using System.Globalization;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Models
{
    /// <summary>
    /// The one place money rules live. Every amount a player types goes through <see cref="TryParse"/> (so NaN, infinity,
    /// negatives and junk never reach a balance), money going into an account goes through <see cref="Credit"/> (debt first),
    /// money coming out through <see cref="Debit"/>, and money taken by force through <see cref="Seize"/>. Worth is worked out
    /// once, in <see cref="Services.WorthService"/>.
    /// </summary>
    public static class Money
    {
        /// <summary>Taken while the whole save is written out. Money changes that touch two accounts at once can take it too.</summary>
        public static readonly object Sync = new object();

        /// <summary>The smallest bet a typed "min" means, as a share of cash.</summary>
        public const double MinBetShare = 0.10;

        /// <summary>"$1,234.56".</summary>
        public static string Format(double amount) => (amount < 0 ? "-$" : "$") + FormatHelper.GetCommaNumber(Math.Abs(Math.Round(amount, 2)));

        /// <summary>"+$1,234" or "-$1,234".</summary>
        public static string Signed(double amount) => (amount >= 0 ? "+$" : "-$") + FormatHelper.GetCommaNumber(Math.Abs(Math.Round(amount, 2)));

        /// <summary>Rounds down to the cent.</summary>
        public static double Cents(double amount) => Math.Floor(amount * 100 + 1e-7) / 100;

        /// <summary>
        /// Reads an amount of cash a player typed: <c>1000</c>, <c>1,000</c>, <c>$1000</c>, <c>2.5k</c>, <c>3m</c>, <c>1b</c>,
        /// <c>25%</c> (of <paramref name="cash"/>), <c>max</c>/<c>all</c> (all of it) or <c>min</c> (<see cref="MinBetShare"/> of it).
        /// False for anything else, including NaN, infinity, 0 and negatives. Rounded down to the cent.
        /// </summary>
        public static bool TryParse(string text, double cash, out double amount)
        {
            amount = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim().ToLowerInvariant().Replace(",", "").Replace("$", "").Replace("_", "");
            cash = double.IsFinite(cash) ? Math.Max(0, cash) : 0;

            double value;
            if (t == "max" || t == "all" || t == "allin" || t == "all-in") value = cash;
            else if (t == "min") value = cash * MinBetShare;
            else if (t == "half") value = cash / 2;
            else if (t.EndsWith("%"))
            {
                if (!TryNumber(t.TrimEnd('%'), out double percent) || percent <= 0 || percent > 100) return false;
                value = cash * percent / 100;
            }
            else
            {
                double scale = 1;
                if (t.EndsWith("k")) { scale = 1e3; t = t[..^1]; }
                else if (t.EndsWith("m")) { scale = 1e6; t = t[..^1]; }
                else if (t.EndsWith("b")) { scale = 1e9; t = t[..^1]; }
                if (!TryNumber(t, out double number)) return false;
                value = number * scale;
            }

            value = Cents(value);
            if (!double.IsFinite(value) || value <= 0) return false;
            amount = value;
            return true;
        }

        private static bool TryNumber(string t, out double number) =>
            double.TryParse(t, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);

        /// <summary>Throws when an amount isn't a real, positive number. The last line of defence before a balance changes.</summary>
        public static void EnsureValid(double amount, string what = "amount")
        {
            if (!double.IsFinite(amount) || amount < 0)
                throw new BadInputException($"That {what} is bullshit. Use a real number.");
        }

        /// <summary>Pays money into an account. It pays off any debt first (into the jackpot); the rest is cash. Returns what reached cash.</summary>
        public static double Credit(CoinAccounts data, CoinAccount to, double amount)
        {
            EnsureValid(amount);
            if (amount <= 0) return 0;
            double toCash = data.PayDebtFrom(to, amount);
            to.NetWorth += toCash;
            return toCash;
        }

        /// <summary>Takes cash out of an account. Refuses (throws) when it isn't there.</summary>
        public static void Debit(CoinAccount from, double amount, string refusal = null)
        {
            EnsureValid(amount);
            if (amount > from.NetWorth + 1e-6)
                throw new BadInputException(refusal ?? $"You've got {Format(Math.Max(0, from.NetWorth))} in cash, genius. You can't spend {Format(amount)}.");
            from.NetWorth -= amount;
        }

        /// <summary>Charges a cost that has to be paid: cash first, and whatever cash can't cover goes on their debt.</summary>
        public static double Charge(CoinAccount from, double amount)
        {
            EnsureValid(amount);
            double fromCash = Math.Min(Math.Max(0, from.NetWorth), amount);
            from.NetWorth -= fromCash;
            double toDebt = Math.Round(amount - fromCash, 2);
            if (toDebt > 0)
            {
                from.Debt = Math.Round(from.Debt + toDebt, 2);
                from.Stats.PeakDebt = Math.Max(from.Stats.PeakDebt, from.Debt);
            }
            return toDebt;
        }

        /// <summary>
        /// Takes up to <paramref name="amount"/> from the victim by force and gives it to the taker: their cash first, and the rest
        /// goes on their debt, but only as much debt as their other assets can back (<paramref name="debtRoom"/>: stocks and
        /// property less what they already owe). Money that isn't there can't be taken, so nothing is ever printed out of thin
        /// air. A null room puts any shortfall on their debt. Returns what was taken and how much of it went on debt.
        /// </summary>
        public static (double Taken, double ToDebt) Seize(CoinAccount victim, CoinAccount taker, double amount, double? debtRoom = null)
        {
            EnsureValid(amount);
            double fromCash = Math.Min(Math.Max(0, victim.NetWorth), amount);
            double toDebt = Math.Round(amount - fromCash, 2);
            if (debtRoom.HasValue) toDebt = Math.Max(0, Math.Min(toDebt, Math.Floor(debtRoom.Value * 100) / 100));
            victim.NetWorth -= fromCash;
            if (toDebt > 0)
            {
                victim.Debt = Math.Round(victim.Debt + toDebt, 2);
                victim.Stats.PeakDebt = Math.Max(victim.Stats.PeakDebt, victim.Debt);
            }
            double taken = Math.Round(fromCash + toDebt, 2);
            if (taker != null) taker.NetWorth += taken;
            return (taken, toDebt);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, double> _held = new System.Collections.Concurrent.ConcurrentDictionary<ulong, double>();

        /// <summary>
        /// Notes cash a player has put somewhere other than a bet (a dungeon buy-in, a Face Off backing), so it still counts as
        /// theirs: the jackpot and the interest bonus can't be gamed by parking cash there and betting a dollar.
        /// </summary>
        public static void Hold(ulong userId, double amount)
        {
            if (!double.IsFinite(amount) || amount <= 0) return;
            _held.AddOrUpdate(userId, amount, (_, before) => before + amount);
        }

        /// <summary>Releases held cash, when it's paid back or lost.</summary>
        public static void Release(ulong userId, double amount)
        {
            if (!double.IsFinite(amount) || amount <= 0) return;
            _held.AddOrUpdate(userId, 0, (_, before) => Math.Max(0, before - amount));
            if (_held.TryGetValue(userId, out double left) && left <= 0.005) _held.TryRemove(userId, out _);
        }

        /// <summary>Clears everything held by a player (their run or backing is over).</summary>
        public static void ReleaseAll(ulong userId) => _held.TryRemove(userId, out _);

        public static double Held(ulong userId) => _held.TryGetValue(userId, out double held) ? held : 0;

        /// <summary>How much more debt the victim's assets can back: their worth beyond their cash. 0 when they have nothing else.</summary>
        public static double DebtRoom(CoinAccount victim, double totalWorth) => Math.Max(0, totalWorth - Math.Max(0, victim.NetWorth));
    }
}
