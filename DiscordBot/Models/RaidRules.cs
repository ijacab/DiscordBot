using DiscordBot.Games.Models.Cards;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    /// <summary>Someone who raided this player, who can be raided back once with revenge's bonus until it expires.</summary>
    public class RevengeToken
    {
        public ulong UserId { get; set; }
        public string Name { get; set; }
        public long ExpiresUtcTicks { get; set; }
        /// <summary>What they staked on the raid. Kept from older saves; raid stakes are now set by the loot.</summary>
        public double Stake { get; set; }
    }

    /// <summary>
    /// The numbers behind raids (`.raid`, <see cref="Services.RaidService"/>). A raid sends a crew (the raider's best rested cards) to rob one of
    /// the target's properties. The chance comes from the crew's total power against the property's security plus the target's best cards
    /// (more with guards, and more when the raider is richer). The loot is a few hours of that property's profit, so property earns less only
    /// as much as it gets robbed, and stocks and cash can't be raided at all. The raider stakes half the loot: a win takes back the stake plus
    /// the loot (from the owner's cash, then debt), a loss hands the stake to the owner.
    /// </summary>
    public static class RaidRules
    {
        /// <summary>How strongly a power lead turns into odds.</summary>
        public const double OddsExponent = 1.2;
        public const double MinChance = 0.05;
        public const double MaxChance = 0.95;

        public const double GuardsMultiplier = 1.5;
        public const double RevengeBonus = 0.25;
        /// <summary>The defence rises this much for each doubling of how much richer the raider is than the target...</summary>
        public const double PunchDownPerDoubling = 0.20;
        /// <summary>...up to this much, so the rich can't farm the poor.</summary>
        public const double MaxPunchDown = 1.0;

        /// <summary>A raid that gets through takes this many hours of the property's profit (rent or takings after the agency and upkeep).</summary>
        public const double LootHours = 3;
        /// <summary>The raider puts up this share of the loot as a stake, which the owner keeps if the raid fails.</summary>
        public const double StakeShare = 0.5;
        /// <summary>A raid never takes more than this share of the target's worth.</summary>
        public const double MaxLootShare = 0.20;
        /// <summary>This share of the loot is set aside as a bounty on the raider, paid to whoever beats them next.</summary>
        public const double BountyShare = 0.10;

        public const int RaidsPerHour = 3;
        public static readonly TimeSpan PairCooldown = TimeSpan.FromHours(6);
        /// <summary>A property robbed successfully can't be robbed again for this long (revenge ignores it).</summary>
        public static readonly TimeSpan PropertyProtection = TimeSpan.FromHours(8);
        public static readonly TimeSpan RevengeWindow = TimeSpan.FromHours(24);
        public static readonly TimeSpan GuardsDuration = TimeSpan.FromHours(6);
        /// <summary>Guards can be hired ahead up to this far.</summary>
        public static readonly TimeSpan MaxGuardsAhead = TimeSpan.FromHours(24);
        /// <summary>Hiring guards costs this share of your worth, into the jackpot.</summary>
        public const double GuardsCostShare = 0.02;

        /// <summary>
        /// The most of a property's profit raids can take: one full loot every protection window, with every raid getting through.
        /// <c>RaidTests</c> checks every kind still beats a bond even then.
        /// </summary>
        public static double WorstCaseDrainShare => LootHours / PropertyProtection.TotalHours;

        public static double TotalPower(IEnumerable<BattlePerson> cards) => (cards ?? Enumerable.Empty<BattlePerson>()).Sum(c => c.Power);

        /// <summary>The raiding crew's power: their cards added up, and revenge's bonus.</summary>
        public static double AttackPower(IEnumerable<BattlePerson> crew, bool revenge) => TotalPower(crew) * (revenge ? 1 + RevengeBonus : 1);

        /// <summary>The defence: the property's security plus the owner's best cards, then guards and the punching-down bonus.</summary>
        public static double DefencePower(IEnumerable<BattlePerson> defenders, double security, bool guarded, double punchDown) =>
            (TotalPower(defenders) + security) * (guarded ? GuardsMultiplier : 1) * punchDown;

        /// <summary>How much harder the defence is because the raider is richer than the target. 1 when they aren't.</summary>
        public static double PunchDownMultiplier(double attackerWorth, double targetWorth)
        {
            if (attackerWorth <= 0 || attackerWorth <= targetWorth) return 1;
            if (targetWorth <= 0) return 1 + MaxPunchDown;
            return 1 + Math.Min(MaxPunchDown, PunchDownPerDoubling * Math.Log2(attackerWorth / targetWorth));
        }

        public static double WinChance(double attackPower, double defencePower)
        {
            double a = Math.Pow(Math.Max(1, attackPower), OddsExponent);
            double d = Math.Pow(Math.Max(1, defencePower), OddsExponent);
            return Math.Clamp(a / (a + d), MinChance, MaxChance);
        }

        /// <summary>What robbing this property takes: <see cref="LootHours"/> of its profit at today's prices, at most a fifth of the owner's worth.</summary>
        public static double Loot(PropertyKind kind, double index, double targetWorth) =>
            Math.Floor(Math.Max(0, Math.Min(PropertyCatalog.ProfitPerHour(kind, index) * LootHours, Math.Max(0, targetWorth) * MaxLootShare)));

        /// <summary>The stake for a raid on loot this size: half of it, at least $1.</summary>
        public static double Stake(double loot) => Math.Max(1, Math.Ceiling(loot * StakeShare));
    }
}
