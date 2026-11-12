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
        public const string RedShell = "redshell";
        public const string LightningBolt = "lightningbolt";
        public const string RobinHood = "robinhood";
        public const string HostileTakeover = "hostiletakeover";

        //catch-up: they pull the top of the leaderboard back, and nothing blocks them
        public const string TaxTheRich = "taxtherich";
        public const string Audit = "audit";
        public const string Bailout = "bailout";
        public const string Squatters = "squatters";
        public const string Leech = "leech";

        public const string Mirror = "mirror";
        public const string GoldenParachute = "goldenparachute";
        public const string TollBooth = "tollbooth";
        public const string TimeMachine = "timemachine";
        public const string Echo = "echo";
        public const string Copycat = "copycat";
        public const string Scratchie = "scratchie";
        public const string RiggedRaffle = "riggedraffle";
        public const string Heist = "heist";
        public const string MusicalChairs = "musicalchairs";
        public const string StickyFingers = "stickyfingers";
        public const string FreezeRay = "freezeray";

        //debuffs put on a victim; they can't be drawn
        public const string Audited = "audited";
        public const string Frozen = "frozen";
        public const string Squatted = "squatted";
        public const string Leeched = "leeched";
    }

    /// <summary>
    /// How the raffle treats a power-up. Players near the top only draw boosts; players further down draw attacks and
    /// catch-up items more often, and catch-up items get much more common when 1st place runs away with it.
    /// </summary>
    public enum PowerUpCategory
    {
        /// <summary>Helps only the player using it.</summary>
        Boost,
        /// <summary>Takes from someone else. Shields, Mirrors and Golden Parachutes work against these.</summary>
        Attack,
        /// <summary>Pulls the leaders back. Nothing blocks them, and the top of the leaderboard can't use them.</summary>
        CatchUp,
        /// <summary>A penalty on a victim. Never drawn.</summary>
        Debuff
    }

    public class PowerUpDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>Relative chance to be drawn: common 10, uncommon 6, rare 3.</summary>
        public int Weight { get; set; }
        public PowerUpKind Kind { get; set; }
        public PowerUpCategory Category { get; set; } = PowerUpCategory.Boost;
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
        /// <summary>For a Squatted or Leeched debuff, the player the money goes to.</summary>
        public ulong BeneficiaryUserId { get; set; }
        /// <summary>For a Squatted debuff, the one property (<see cref="PropertyHolding.Id"/>) whose rent the squatter takes. Null on old saves, which squat the first property.</summary>
        public string PropertyId { get; set; }
        public const int Unlimited = -1;
    }

    public static class PowerUpCatalog
    {
        public const int Common = 10, Uncommon = 6, Rare = 3;

        public static readonly TimeSpan HoldTime = TimeSpan.FromHours(24);
        public const int MaxHeld = 10;

        public const double HotStreakBonus = 0.5;
        /// <summary>A Golden Ticket win pays this many times the normal win.</summary>
        public const double GoldenTicketMultiplier = 3;
        public const double SafetyNetRefund = 0.75;
        public const int SafetyNetCharges = 5;
        public const double JackpotFeverChance = 0.30;
        public const int LoadedDiceRollNeeded = 6;
        public const double CompoundInterestMultiplier = 6;
        public const double BlueShellPercent = 0.20;
        public const double PickpocketPercent = 0.06;
        public const int PickpocketVictims = 3;
        public const double StimulusPercent = 0.10;
        public const double StimulusMin = 5_000;
        public const double JackpotDipPercent = 0.30;
        public const double RedShellPercent = 0.10;
        public const double LightningBoltPercent = 0.04;
        public const int LightningBoltVictims = 5;
        public const double RobinHoodPercent = 0.05;
        public const int RobinHoodVictims = 3;
        public const double HostileTakeoverCashPercent = 0.5;
        public const double HostileTakeoverWorthPercent = 0.20;

        public const double TaxTheRichPercent = 0.10;
        public const int TaxTheRichPayers = 3;
        public static readonly TimeSpan AuditDuration = TimeSpan.FromHours(6);
        /// <summary>Share of an audited player's net gain on a winning bet that goes to the jackpot.</summary>
        public const double AuditSkim = 0.5;
        public const double BailoutDebtCapPercent = 0.20;
        public const double BailoutBonusPercent = 0.05;
        public static readonly TimeSpan SquatDuration = TimeSpan.FromHours(12);
        public static readonly TimeSpan LeechDuration = TimeSpan.FromHours(6);
        public const double LeechSkim = 0.15;

        public const double TollBoothSkim = 0.03;
        public static readonly TimeSpan TimeMachineWindow = TimeSpan.FromHours(1);
        public const double HeistWinPercent = 0.30;
        public const double HeistLosePercent = 0.10;
        public const double HeistLoseMin = 1_000;
        public static readonly TimeSpan FreezeDuration = TimeSpan.FromHours(3);

        /// <summary>Scratchie prizes: (chance, flat amount, share of your net worth). The last row is the whole jackpot.</summary>
        public static readonly IReadOnlyList<(double Chance, double Flat, double WorthShare)> ScratchiePrizes = new List<(double, double, double)>
        {
            (0.60, 5_000, 0),
            (0.30, 5_000, 0.05),
            (0.09, 5_000, 0.20),
            (0.01, 0, 0), //the jackpot
        };

        /// <summary>
        /// How many of the leaderboard count as "the top": 3, but never more than half the board, so on a small server
        /// someone can still draw catch-up items.
        /// </summary>
        public static int TopTier(int rankedCount) => Math.Min(3, Math.Max(1, rankedCount / 2));

        /// <summary>1st place counts as running away when they're worth more than this many times the median active player.</summary>
        public const double RunawayMultiple = 3;

        public static readonly IReadOnlyList<PowerUpDefinition> All = new List<PowerUpDefinition>
        {
            //boosts
            new PowerUpDefinition { Id = PowerUpIds.HotStreak, Name = "Hot Streak", Weight = Common, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(3),
                Description = "For 3 hours, every winning bet pays 50% extra." },
            new PowerUpDefinition { Id = PowerUpIds.GoldenTicket, Name = "Golden Ticket", Weight = Rare, Kind = PowerUpKind.Charges, Charges = 1, Duration = HoldTime,
                Description = "Your next winning bet pays triple." },
            new PowerUpDefinition { Id = PowerUpIds.SafetyNet, Name = "Safety Net", Weight = Common, Kind = PowerUpKind.Charges, Charges = SafetyNetCharges, Duration = HoldTime,
                Description = "Your next 5 losing bets refund 75% of what you lost." },
            new PowerUpDefinition { Id = PowerUpIds.JackpotFever, Name = "Jackpot Fever", Weight = Uncommon, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(6),
                Description = "For 6 hours an all-in cash bet has a 30% chance (up from 3%) to win the jackpot." },
            new PowerUpDefinition { Id = PowerUpIds.TaxHaven, Name = "Tax Haven", Weight = Common, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(12),
                Description = "For 12 hours you pay no sell tax on stocks." },
            new PowerUpDefinition { Id = PowerUpIds.InsiderTrader, Name = "Insider Trader", Weight = Common, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(12),
                Description = "For 12 hours you buy and sell stocks with no 1% spread." },
            new PowerUpDefinition { Id = PowerUpIds.CompoundInterest, Name = "Compound Interest", Weight = Uncommon, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(3),
                Description = "For 3 hours your hourly interest is 6x." },
            new PowerUpDefinition { Id = PowerUpIds.Shield, Name = "Shield", Weight = Uncommon, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(12),
                Description = "For 12 hours attacks can't touch you: no daily roll steal, shells, bolts, pickpockets or heists. Catch-up items still get through." },
            new PowerUpDefinition { Id = PowerUpIds.Stimulus, Name = "Stimulus", Weight = Common, Kind = PowerUpKind.Instant,
                Description = "Get 10% of your net worth, at least $5,000." },
            new PowerUpDefinition { Id = PowerUpIds.JackpotDip, Name = "Jackpot Dip", Weight = Rare, Kind = PowerUpKind.Instant,
                Description = "Claim 30% of the jackpot pool." },
            new PowerUpDefinition { Id = PowerUpIds.FreeCardPull, Name = "Free Card Pull", Weight = Common, Kind = PowerUpKind.Instant,
                Description = "Your free daily card pull is available again." },
            new PowerUpDefinition { Id = PowerUpIds.InterestPass, Name = "Interest Pass", Weight = Common, Kind = PowerUpKind.Instant,
                Description = "Today counts as a bonus day, so you earn hourly interest without needing a 50% bet." },
            new PowerUpDefinition { Id = PowerUpIds.Mirror, Name = "Mirror", Weight = Uncommon, Kind = PowerUpKind.Charges, Charges = 1, Duration = HoldTime,
                Description = "The next attack on you bounces back: the attacker pays you what they tried to take, or gets frozen by their own Freeze Ray." },
            new PowerUpDefinition { Id = PowerUpIds.GoldenParachute, Name = "Golden Parachute", Weight = Uncommon, Kind = PowerUpKind.Charges, Charges = 1, Duration = HoldTime,
                Description = "The next attack that would put you in debt only takes your cash. The debt part is cancelled." },
            new PowerUpDefinition { Id = PowerUpIds.TollBooth, Name = "Toll Booth", Weight = Uncommon, Kind = PowerUpKind.Timed, Duration = TimeSpan.FromHours(2),
                Description = "For 2 hours, 3% of every bet anyone else wins is skimmed off to you." },
            new PowerUpDefinition { Id = PowerUpIds.TimeMachine, Name = "Time Machine", Weight = Uncommon, Kind = PowerUpKind.Instant,
                Description = "Undo your last losing bet: get back what you lost, if it was in the last hour." },
            new PowerUpDefinition { Id = PowerUpIds.Echo, Name = "Echo", Weight = Rare, Kind = PowerUpKind.Charges, Charges = 1, Duration = HoldTime,
                Description = "Your next instant power-up fires twice." },
            new PowerUpDefinition { Id = PowerUpIds.Copycat, Name = "Copycat", Weight = Common, Kind = PowerUpKind.Instant,
                Description = "Get a copy of the last power-up someone else used." },
            new PowerUpDefinition { Id = PowerUpIds.Scratchie, Name = "Scratchie", Weight = Common, Kind = PowerUpKind.Instant,
                Description = "Scratch it: 60% $5,000, 30% 5% of your net worth, 9% 20% of your net worth, 1% the WHOLE jackpot." },
            new PowerUpDefinition { Id = PowerUpIds.RiggedRaffle, Name = "Rigged Raffle", Weight = Rare, Kind = PowerUpKind.Charges, Charges = 1, Duration = HoldTime,
                Description = "You win the next raffle draw you're entered in." },

            //attacks
            new PowerUpDefinition { Id = PowerUpIds.LoadedDice, Name = "Loaded Dice", Weight = Uncommon, Kind = PowerUpKind.Charges, Charges = 1, Duration = HoldTime, Category = PowerUpCategory.Attack,
                Description = "Your next gamble gets the daily roll again, and it steals from 1st place on a 6 or higher." },
            new PowerUpDefinition { Id = PowerUpIds.BlueShell, Name = "Blue Shell", Weight = Rare, Kind = PowerUpKind.Instant, Category = PowerUpCategory.Attack,
                Description = "Steal 20% of 1st place's net worth (cash, stocks and property). Any part they can't pay from cash goes on their debt." },
            new PowerUpDefinition { Id = PowerUpIds.Pickpocket, Name = "Pickpocket", Weight = Uncommon, Kind = PowerUpKind.Instant, Category = PowerUpCategory.Attack,
                Description = "Take 6% of the net worth of up to 3 random other active players. Any part they can't pay from cash goes on their debt." },
            new PowerUpDefinition { Id = PowerUpIds.RedShell, Name = "Red Shell", Weight = Uncommon, Kind = PowerUpKind.Instant, Category = PowerUpCategory.Attack,
                Description = "Steal 10% of the net worth of the player directly above you. Any part they can't pay from cash goes on their debt." },
            new PowerUpDefinition { Id = PowerUpIds.LightningBolt, Name = "Lightning Bolt", Weight = Rare, Kind = PowerUpKind.Instant, Category = PowerUpCategory.Attack,
                Description = "Strike up to 5 of the players nearest above you for 4% of their net worth each. Any part they can't pay from cash goes on their debt." },
            new PowerUpDefinition { Id = PowerUpIds.RobinHood, Name = "Robin Hood", Weight = Rare, Kind = PowerUpKind.Instant, Category = PowerUpCategory.Attack,
                Description = "Take 5% of the net worth of each of the top 3 players other than you. The lot is split between the poorest half of active players, you included." },
            new PowerUpDefinition { Id = PowerUpIds.HostileTakeover, Name = "Hostile Takeover", Weight = Rare, Kind = PowerUpKind.Instant, Category = PowerUpCategory.Attack,
                Description = "Seize half of 1st place's cash or 20% of their net worth, whichever is bigger. Any part they can't pay from cash goes on their debt." },
            new PowerUpDefinition { Id = PowerUpIds.Heist, Name = "Heist", Weight = Rare, Kind = PowerUpKind.Instant, Category = PowerUpCategory.Attack,
                Description = "Flip a coin against 1st place. Heads: take 30% of their net worth. Tails: you're caught and pay them 10% of yours (at least $1,000, debt if you're short)." },
            new PowerUpDefinition { Id = PowerUpIds.MusicalChairs, Name = "Musical Chairs", Weight = Rare, Kind = PowerUpKind.Instant, Category = PowerUpCategory.Attack,
                Description = "Swap cash with a random active player who has more cash than you." },
            new PowerUpDefinition { Id = PowerUpIds.StickyFingers, Name = "Sticky Fingers", Weight = Uncommon, Kind = PowerUpKind.Instant, Category = PowerUpCategory.Attack,
                Description = "Steal a random unused power-up from the player directly above you." },
            new PowerUpDefinition { Id = PowerUpIds.FreezeRay, Name = "Freeze Ray", Weight = Uncommon, Kind = PowerUpKind.Instant, Category = PowerUpCategory.Attack,
                Description = "1st place can't use any power-ups for 3 hours." },

            //catch-up
            new PowerUpDefinition { Id = PowerUpIds.TaxTheRich, Name = "Tax the Rich", Weight = Uncommon, Kind = PowerUpKind.Instant, Category = PowerUpCategory.CatchUp,
                Description = "The top 3 each pay 10% of their net worth, shared evenly between every other active player, you included. Can't be dodged; what they can't pay in cash goes on their debt." },
            new PowerUpDefinition { Id = PowerUpIds.Audit, Name = "Audit", Weight = Uncommon, Kind = PowerUpKind.Instant, Category = PowerUpCategory.CatchUp,
                Description = "1st place is audited for 6 hours: no interest, and half of every bet they win goes to the jackpot. Can't be dodged." },
            new PowerUpDefinition { Id = PowerUpIds.Bailout, Name = "Bailout", Weight = Common, Kind = PowerUpKind.Instant, Category = PowerUpCategory.CatchUp,
                Description = "1st place pays off your debt (up to 20% of their net worth) and hands you 5% of their net worth on top. Can't be dodged; what they can't pay goes on their debt." },
            new PowerUpDefinition { Id = PowerUpIds.Squatters, Name = "Squatters", Weight = Uncommon, Kind = PowerUpKind.Instant, Category = PowerUpCategory.CatchUp,
                Description = "Move into 1st place's best rental: that property's rent comes to you for 12 hours. Can't be dodged." },
            new PowerUpDefinition { Id = PowerUpIds.Leech, Name = "Leech", Weight = Uncommon, Kind = PowerUpKind.Instant, Category = PowerUpCategory.CatchUp,
                Description = "Latch onto 1st place for 6 hours: 15% of every bet they win comes to you. Can't be dodged." },
        };

        /// <summary>Penalties put on a victim. Not drawn; listed so `.pu` can show them.</summary>
        public static readonly IReadOnlyList<PowerUpDefinition> Debuffs = new List<PowerUpDefinition>
        {
            new PowerUpDefinition { Id = PowerUpIds.Audited, Name = "Audited", Kind = PowerUpKind.Timed, Duration = AuditDuration, Category = PowerUpCategory.Debuff,
                Description = "No interest, and half of every bet you win goes to the jackpot." },
            new PowerUpDefinition { Id = PowerUpIds.Frozen, Name = "Frozen", Kind = PowerUpKind.Timed, Duration = FreezeDuration, Category = PowerUpCategory.Debuff,
                Description = "You can't use power-ups." },
            new PowerUpDefinition { Id = PowerUpIds.Squatted, Name = "Squatters", Kind = PowerUpKind.Timed, Duration = SquatDuration, Category = PowerUpCategory.Debuff,
                Description = "One of your properties has squatters; its rent goes to them." },
            new PowerUpDefinition { Id = PowerUpIds.Leeched, Name = "Leech", Kind = PowerUpKind.Timed, Duration = LeechDuration, Category = PowerUpCategory.Debuff,
                Description = "15% of every bet you win goes to the leech." },
        };

        public static string RarityOf(PowerUpDefinition power) =>
            power.Weight >= Common ? "common" : power.Weight >= Uncommon ? "uncommon" : "rare";

        /// <summary>
        /// The draw weight of a power-up for one raffle winner. With no context it is the plain weight. Players in the
        /// top tier only draw boosts. Lower down, attacks and catch-up items get more likely the lower you are, and
        /// catch-up items get much more likely while 1st place is running away.
        /// </summary>
        public static double WeightFor(PowerUpDefinition power, PickContext context)
        {
            if (power.Category == PowerUpCategory.Debuff) return 0;
            if (context == null) return power.Weight;
            if (context.IsTopTier) return power.Category == PowerUpCategory.Boost ? power.Weight : 0;

            double p = Math.Clamp(context.Percentile, 0, 1);
            switch (power.Category)
            {
                case PowerUpCategory.Attack: return power.Weight * (1 + p) * (context.Runaway ? 1.5 : 1);
                case PowerUpCategory.CatchUp: return power.Weight * (1 + 2 * p) * (context.Runaway ? 3 : 1);
                default: return power.Weight;
            }
        }

        public static PowerUpDefinition Find(string id) =>
            All.Concat(Debuffs).FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Picks a power-up in proportion to its plain weight. <paramref name="roll"/> is in [0, 1).</summary>
        public static PowerUpDefinition Pick(double roll) => Pick(roll, null);

        /// <summary>Picks a power-up in proportion to its weight for this winner (see <see cref="WeightFor"/>).</summary>
        public static PowerUpDefinition Pick(double roll, PickContext context)
        {
            var weighted = All.Select(p => (Power: p, Weight: WeightFor(p, context))).Where(w => w.Weight > 0).ToList();
            double total = weighted.Sum(w => w.Weight);
            double target = Math.Max(0, Math.Min(0.999999999, roll)) * total;
            double running = 0;
            foreach (var (power, weight) in weighted)
            {
                running += weight;
                if (target < running) return power;
            }
            return weighted[weighted.Count - 1].Power;
        }
    }

    /// <summary>Where a raffle winner stands, for <see cref="PowerUpCatalog.WeightFor"/>.</summary>
    public class PickContext
    {
        /// <summary>In the top of the leaderboard (<see cref="PowerUpCatalog.TopTier"/>): boosts only.</summary>
        public bool IsTopTier { get; set; }
        /// <summary>0 for 1st place up to 1 for last place.</summary>
        public double Percentile { get; set; }
        /// <summary>1st place is worth more than <see cref="PowerUpCatalog.RunawayMultiple"/> times the median active player.</summary>
        public bool Runaway { get; set; }
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
                    bonus += netGain * (PowerUpCatalog.GoldenTicketMultiplier - 1);
            }
            else if (netGain < 0 && account.TryConsumeCharge(PowerUpIds.SafetyNet, now))
            {
                bonus += -netGain * PowerUpCatalog.SafetyNetRefund;
            }
            return bonus;
        }

        /// <summary>
        /// Puts a debuff on a victim. Another of the same kind extends it; Squatted and Leeched are kept apart per
        /// beneficiary, so two leeches each take their own share.
        /// </summary>
        public static void AddDebuff(this CoinAccount victim, string id, DateTime now, ulong beneficiaryUserId = 0, string propertyId = null)
        {
            var debuff = PowerUpCatalog.Find(id);
            victim.Prune(now);
            var existing = victim.ActivePowerUps.FirstOrDefault(a => a.Id == id && a.BeneficiaryUserId == beneficiaryUserId && a.PropertyId == propertyId);
            if (existing != null)
                existing.ExpiresAtTicks += debuff.Duration.Ticks;
            else
                victim.ActivePowerUps.Add(new ActivePowerUp { Id = id, ExpiresAtTicks = (now + debuff.Duration).Ticks, ChargesLeft = ActivePowerUp.Unlimited, BeneficiaryUserId = beneficiaryUserId, PropertyId = propertyId });
        }

        /// <summary>The running entries of one kind on an account, such as every leech on it.</summary>
        public static List<ActivePowerUp> Running(this CoinAccount account, string id, DateTime now)
        {
            account.Prune(now);
            return account.ActivePowerUps.Where(a => a.Id == id).ToList();
        }

        /// <summary>
        /// Skims a winning bet for the power-ups that take a cut of it: an Audit sends half to the jackpot, each Leech on
        /// the winner takes 15%, and each other player's Toll Booth takes 3%. The skims are worked out on the whole net
        /// gain and never add up to more than it. Pays the recipients and returns what comes off the winner's winnings.
        /// </summary>
        public static double SettleWin(CoinAccounts data, CoinAccount winner, double netGain, DateTime now)
        {
            if (netGain <= 0) return 0;
            var cuts = new List<(CoinAccount To, double Amount)>(); //a null recipient is the jackpot

            if (winner.IsActive(PowerUpIds.Audited, now))
                cuts.Add((null, netGain * PowerUpCatalog.AuditSkim));
            foreach (var leech in winner.Running(PowerUpIds.Leeched, now))
            {
                var to = data.Accounts.FirstOrDefault(a => a.UserId == leech.BeneficiaryUserId);
                if (to != null && to.UserId != winner.UserId)
                    cuts.Add((to, netGain * PowerUpCatalog.LeechSkim));
            }
            foreach (var owner in data.Accounts.Where(a => a.UserId != winner.UserId && a.IsActive(PowerUpIds.TollBooth, now)))
                cuts.Add((owner, netGain * PowerUpCatalog.TollBoothSkim));

            double total = cuts.Sum(c => c.Amount);
            double scale = total > netGain ? netGain / total : 1;
            double taken = 0;
            foreach (var (to, amount) in cuts)
            {
                double paid = Math.Floor(amount * scale * 100) / 100;
                if (paid <= 0) continue;
                if (to == null) data.JackpotPool += paid;
                else to.NetWorth += paid;
                taken += paid;
            }
            return Math.Round(taken, 2);
        }
    }
}
