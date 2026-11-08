using System;
using System.Collections.Generic;
using System.Linq;
using static DiscordBot.Models.CoinAccounts;

namespace DiscordBot.Models
{
    public enum PowerUpKind
    {
        /// <summary>A buff that runs for a set time once used.</summary>
        Timed,
        /// <summary>A buff that is spent a use at a time (and still lapses after a day).</summary>
        Charges,
        /// <summary>Fires the moment it is used.</summary>
        Instant
    }

    public static class PowerUpIds
    {
        public const string HotStreak = "hotstreak";
        public const string GoldenTicket = "goldenticket";
        public const string SafetyNet = "safetynet";
        public const string JackpotFever = "jackpotfever";
        public const string TaxHaven = "taxhaven";
        public const string InsiderTrader = "insidertrader";
        public const string CompoundInterest = "compoundinterest";
        public const string Shield = "shield";
        public const string LoadedDice = "loadeddice";
        public const string BlueShell = "blueshell";
        public const string Pickpocket = "pickpocket";
        public const string Stimulus = "stimulus";
        public const string JackpotDip = "jackpotdip";
        public const string FreeCardPull = "freecardpull";
        public const string InterestPass = "interestpass";
    }

    public class PowerUpDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>Relative chance to be drawn: common 10, uncommon 6, rare 3.</summary>
        public int Weight { get; set; }
        public PowerUpKind Kind { get; set; }
        /// <summary>How long a timed buff runs. For a charge buff, how long its charges last.</summary>
        public TimeSpan Duration { get; set; }
        public int Charges { get; set; }
    }

    /// <summary>A power-up won in a raffle and not used yet.</summary>
    public class HeldPowerUp
    {
        public string Id { get; set; }
        public long ExpiresAtTicks { get; set; }
    }

    /// <summary>A power-up that has been used and is now running.</summary>
    public class ActivePowerUp
    {
        public string Id { get; set; }
        public long ExpiresAtTicks { get; set; }
        /// <summary>Uses left, or <see cref="Unlimited"/> for a timed buff.</summary>
        public int ChargesLeft { get; set; }
        public const int Unlimited = -1;
    }

    public static class PowerUpCatalog
    {
        public const int Common = 10, Uncommon = 6, Rare = 3;

        public static readonly TimeSpan HoldTime = TimeSpan.FromHours(24);
        public const int MaxHeld = 10;

        public const double HotStreakBonus = 0.25;
        public const double SafetyNetRefund = 0.5;
        public const double JackpotFeverChance = 0.15;
        public const int LoadedDiceRollNeeded = 8;
        public const double CompoundInterestMultiplier = 3;
        public const double BlueShellPercent = 0.10;
        public const double PickpocketPercent = 0.03;
        public const int PickpocketVictims = 3;
        public const double StimulusPercent = 0.05;
        public const double StimulusMin = 2_000;
        public const double JackpotDipPercent = 0.15;

        public static readonly IReadOnlyList<PowerUpDefinition> All = new List<PowerUpDefinition>
        {
            new PowerUpDefinition { Id = PowerUpIds.HotStreak, Name = "Hot Streak", Weight = Common, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(3),
                Description = "For 3 hours, every winning bet pays 25% extra." },
            new PowerUpDefinition { Id = PowerUpIds.GoldenTicket, Name = "Golden Ticket", Weight = Rare, Kind = PowerUpKind.Charges, Charges = 1, Duration = HoldTime,
                Description = "Your next winning bet pays double." },
            new PowerUpDefinition { Id = PowerUpIds.SafetyNet, Name = "Safety Net", Weight = Common, Kind = PowerUpKind.Charges, Charges = 3, Duration = HoldTime,
                Description = "Your next 3 losing bets refund half of what you lost." },
            new PowerUpDefinition { Id = PowerUpIds.JackpotFever, Name = "Jackpot Fever", Weight = Uncommon, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(6),
                Description = "For 6 hours an all-in cash bet has a 15% chance (up from 3%) to win the jackpot." },
            new PowerUpDefinition { Id = PowerUpIds.TaxHaven, Name = "Tax Haven", Weight = Common, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(6),
                Description = "For 6 hours you pay no sell tax on stocks." },
            new PowerUpDefinition { Id = PowerUpIds.InsiderTrader, Name = "Insider Trader", Weight = Common, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(6),
                Description = "For 6 hours you buy and sell stocks with no 1% spread." },
            new PowerUpDefinition { Id = PowerUpIds.CompoundInterest, Name = "Compound Interest", Weight = Uncommon, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(3),
                Description = "For 3 hours your hourly interest is tripled." },
            new PowerUpDefinition { Id = PowerUpIds.Shield, Name = "Shield", Weight = Uncommon, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(12),
                Description = "For 12 hours nobody can steal from you: no daily roll steal, blue shell or pickpocket." },
            new PowerUpDefinition { Id = PowerUpIds.LoadedDice, Name = "Loaded Dice", Weight = Uncommon, Kind = PowerUpKind.Charges, Charges = 1, Duration = HoldTime,
                Description = "Your next gamble gets the daily roll again, and it steals from 1st place on an 8, 9 or 10." },
            new PowerUpDefinition { Id = PowerUpIds.BlueShell, Name = "Blue Shell", Weight = Rare, Kind = PowerUpKind.Instant,
                Description = "Steal 10% of 1st place's cash. Can't be used by 1st place, or on a shielded leader." },
            new PowerUpDefinition { Id = PowerUpIds.Pickpocket, Name = "Pickpocket", Weight = Uncommon, Kind = PowerUpKind.Instant,
                Description = "Take 3% of the cash of up to 3 random other active players who aren't shielded." },
            new PowerUpDefinition { Id = PowerUpIds.Stimulus, Name = "Stimulus", Weight = Common, Kind = PowerUpKind.Instant,
                Description = "Get 5% of your net worth, at least $2,000." },
            new PowerUpDefinition { Id = PowerUpIds.JackpotDip, Name = "Jackpot Dip", Weight = Rare, Kind = PowerUpKind.Instant,
                Description = "Claim 15% of the jackpot pool." },
            new PowerUpDefinition { Id = PowerUpIds.FreeCardPull, Name = "Free Card Pull", Weight = Common, Kind = PowerUpKind.Instant,
                Description = "Your free daily card pull is available again." },
            new PowerUpDefinition { Id = PowerUpIds.InterestPass, Name = "Interest Pass", Weight = Common, Kind = PowerUpKind.Instant,
                Description = "Today counts as a bonus day, so you earn hourly interest without needing a 50% bet." }
        };

        public static PowerUpDefinition Find(string id) =>
            All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Picks a power-up in proportion to its weight. <paramref name="roll"/> is in [0, 1).</summary>
        public static PowerUpDefinition Pick(double roll)
        {
            int total = All.Sum(p => p.Weight);
            double target = Math.Max(0, Math.Min(0.999999999, roll)) * total;
            double running = 0;
            foreach (var power in All)
            {
                running += power.Weight;
                if (target < running) return power;
            }
            return All[All.Count - 1];
        }
    }

    /// <summary>Reading and changing the power-ups on an account. Expired entries are dropped whenever they are read.</summary>
    public static class PowerUps
    {
        public static void Prune(this CoinAccount account, DateTime now)
        {
            long ticks = now.Ticks;
            account.HeldPowerUps.RemoveAll(h => h.ExpiresAtTicks <= ticks);
            account.ActivePowerUps.RemoveAll(a => a.ExpiresAtTicks <= ticks || a.ChargesLeft == 0);
        }

        /// <summary>Adds a won power-up to the account's holdings. When full, the oldest is dropped.</summary>
        public static void Grant(this CoinAccount account, string id, DateTime now)
        {
            account.Prune(now);
            while (account.HeldPowerUps.Count >= PowerUpCatalog.MaxHeld)
                account.HeldPowerUps.RemoveAt(0);
            account.HeldPowerUps.Add(new HeldPowerUp { Id = id, ExpiresAtTicks = (now + PowerUpCatalog.HoldTime).Ticks });
        }

        public static bool IsActive(this CoinAccount account, string id, DateTime now)
        {
            account.Prune(now);
            return account.ActivePowerUps.Any(a => a.Id == id);
        }

        public static bool IsShielded(this CoinAccount account, DateTime now) => account.IsActive(PowerUpIds.Shield, now);

        /// <summary>Spends one use of a charge power-up. False when it isn't running.</summary>
        public static bool TryConsumeCharge(this CoinAccount account, string id, DateTime now)
        {
            account.Prune(now);
            var active = account.ActivePowerUps.FirstOrDefault(a => a.Id == id);
            if (active == null) return false;
            if (active.ChargesLeft > 0)
            {
                active.ChargesLeft--;
                if (active.ChargesLeft == 0) account.ActivePowerUps.Remove(active);
            }
            return true;
        }

        /// <summary>Starts a timed or charge power-up. Using another of the same kind extends the time or adds the charges.</summary>
        public static void Start(this CoinAccount account, PowerUpDefinition power, DateTime now)
        {
            account.Prune(now);
            var existing = account.ActivePowerUps.FirstOrDefault(a => a.Id == power.Id);
            if (power.Kind == PowerUpKind.Timed)
            {
                if (existing != null)
                    existing.ExpiresAtTicks += power.Duration.Ticks;
                else
                    account.ActivePowerUps.Add(new ActivePowerUp { Id = power.Id, ExpiresAtTicks = (now + power.Duration).Ticks, ChargesLeft = ActivePowerUp.Unlimited });
            }
            else
            {
                if (existing != null)
                {
                    existing.ChargesLeft += power.Charges;
                    existing.ExpiresAtTicks = (now + power.Duration).Ticks;
                }
                else
                    account.ActivePowerUps.Add(new ActivePowerUp { Id = power.Id, ExpiresAtTicks = (now + power.Duration).Ticks, ChargesLeft = power.Charges });
            }
        }

        /// <summary>
        /// Extra (or refunded) money from the player's power-ups on a resolved bet. <paramref name="baseWinnings"/> is everything
        /// returned to them, including their stake, so a win has baseWinnings above the bet. Spends charges as needed.
        /// </summary>
        public static double BetBonus(this CoinAccount account, double baseWinnings, double betAmount, DateTime now)
        {
            double netGain = baseWinnings - betAmount;
            double bonus = 0;
            if (netGain > 0)
            {
                if (account.IsActive(PowerUpIds.HotStreak, now))
                    bonus += netGain * PowerUpCatalog.HotStreakBonus;
                if (account.TryConsumeCharge(PowerUpIds.GoldenTicket, now))
                    bonus += netGain;
            }
            else if (netGain < 0 && account.TryConsumeCharge(PowerUpIds.SafetyNet, now))
            {
                bonus += -netGain * PowerUpCatalog.SafetyNetRefund;
            }
            return bonus;
        }
    }
}
