using DiscordBot.Games.Models.BattleArena;
using System;

namespace DiscordBot.Models
{
    /// <summary>How a card defends against raids while its player is away.</summary>
    public enum RaidStance
    {
        /// <summary>Stronger defence (<see cref="RaidRules.GuardStance"/>). A win keeps the raider's stake.</summary>
        Guard,
        /// <summary>Normal defence, but a win also hits the raider back for <see cref="RaidRules.CounterHitMultiple"/> times the stake.</summary>
        Counter,
        /// <summary>Weaker defence, but a raider who gets through takes half the usual loot.</summary>
        Hoard
    }

    /// <summary>Someone who raided this player, who can be raided back once with revenge's bonus until it expires.</summary>
    public class RevengeToken
    {
        public ulong UserId { get; set; }
        public string Name { get; set; }
        public long ExpiresUtcTicks { get; set; }
    }

    /// <summary>
    /// The numbers behind raids (`.raid`, <see cref="Services.RaidService"/>). Each side's card has a power; the defender's is
    /// raised by their stance, hired guards and how much richer they are than the raider; the chance to win comes from the two.
    /// The raider stakes cash: a win takes back the stake plus twice it in loot from the target (cash first, then debt), a loss
    /// hands the stake to the defender.
    /// </summary>
    public static class RaidRules
    {
        /// <summary>Every card's power starts here, and a player with no card defends with just this.</summary>
        public const double BasePower = 25;
        public const double CritWeight = 10;
        /// <summary>How strongly a power lead turns into odds. 1.2 makes an average level 30 card beat an average new one about 75% of the time.</summary>
        public const double OddsExponent = 1.2;
        public const double MinChance = 0.05;
        public const double MaxChance = 0.95;

        public const double GuardStance = 1.3;
        public const double CounterStance = 1.0;
        public const double HoardStance = 0.8;
        public const double GuardsMultiplier = 1.5;
        /// <summary>CritBoost raises attack power, Shield defence power, by this share.</summary>
        public const double AbilityBonus = 0.10;
        public const double RevengeBonus = 0.25;
        /// <summary>The defender's power rises this much for each doubling of how much richer they are than the raider...</summary>
        public const double UnderdogPerDoubling = 0.20;
        /// <summary>...up to this much.</summary>
        public const double MaxUnderdogBonus = 1.0;

        /// <summary>The smallest stake is this share of the raider's own worth, so a poke always costs something.</summary>
        public const double MinStakeShare = 0.01;
        /// <summary>The biggest stake is this share of the target's worth.</summary>
        public const double MaxStakeShare = 0.10;
        public const double LootMultiple = 2;
        public const double HoardLootShare = 0.5;
        /// <summary>A raid never takes more than this share of the target's worth.</summary>
        public const double MaxLootShare = 0.20;
        /// <summary>This share of the loot is set aside as a bounty on the raider, paid to whoever beats them next.</summary>
        public const double BountyShare = 0.10;
        public const double CounterHitMultiple = 1;

        public const int RaidsPerHour = 3;
        public static readonly TimeSpan PairCooldown = TimeSpan.FromHours(6);
        public static readonly TimeSpan Protection = TimeSpan.FromHours(1);
        /// <summary>Protection after a lost defence for a card with the Heal ability.</summary>
        public static readonly TimeSpan HealProtection = TimeSpan.FromHours(2);
        public static readonly TimeSpan RevengeWindow = TimeSpan.FromHours(24);
        public static readonly TimeSpan GuardsDuration = TimeSpan.FromHours(6);
        /// <summary>Guards can be hired ahead up to this far.</summary>
        public static readonly TimeSpan MaxGuardsAhead = TimeSpan.FromHours(24);
        /// <summary>Hiring guards costs this share of your worth, into the jackpot.</summary>
        public const double GuardsCostShare = 0.02;

        /// <summary>A card's raw power at its level and focus.</summary>
        public static double Power(BattlePerson card) =>
            card == null ? BasePower : BasePower + card.Attack + card.Defense + CritWeight * card.CritMultiplier;

        public static double AttackPower(BattlePerson card, bool revenge)
        {
            double power = Power(card);
            if (card?.Ability == CardAbility.CritBoost) power *= 1 + AbilityBonus;
            if (revenge) power *= 1 + RevengeBonus;
            return power;
        }

        public static double DefencePower(BattlePerson card, RaidStance stance, bool guarded, double underdog)
        {
            double power = Power(card) * StanceMultiplier(stance) * underdog;
            if (guarded) power *= GuardsMultiplier;
            if (card?.Ability == CardAbility.Shield) power *= 1 + AbilityBonus;
            return power;
        }

        public static double StanceMultiplier(RaidStance stance) =>
            stance == RaidStance.Guard ? GuardStance : stance == RaidStance.Hoard ? HoardStance : CounterStance;

        /// <summary>How much the defender's power rises because they are richer than the raider. 1 when they aren't.</summary>
        public static double UnderdogMultiplier(double attackerWorth, double targetWorth)
        {
            if (targetWorth <= 0 || targetWorth <= attackerWorth) return 1;
            if (attackerWorth <= 0) return 1 + MaxUnderdogBonus;
            return 1 + Math.Min(MaxUnderdogBonus, UnderdogPerDoubling * Math.Log2(targetWorth / attackerWorth));
        }

        public static double WinChance(double attackPower, double defencePower)
        {
            double a = Math.Pow(Math.Max(1, attackPower), OddsExponent);
            double d = Math.Pow(Math.Max(1, defencePower), OddsExponent);
            return Math.Clamp(a / (a + d), MinChance, MaxChance);
        }

        /// <summary>The smallest and biggest stake allowed. The smallest is more than the biggest when the target is too poor to raid.</summary>
        public static (double Min, double Max) StakeLimits(double attackerWorth, double targetWorth) =>
            (Math.Max(1, Math.Ceiling(Math.Max(0, attackerWorth) * MinStakeShare)), Math.Floor(Math.Max(0, targetWorth) * MaxStakeShare));

        /// <summary>What a winning raid takes: twice the stake, halved by Hoard, never more than a fifth of the target's worth.</summary>
        public static double Loot(double stake, RaidStance stance, double targetWorth)
        {
            double loot = stake * LootMultiple * (stance == RaidStance.Hoard ? HoardLootShare : 1);
            return Math.Floor(Math.Max(0, Math.Min(loot, targetWorth * MaxLootShare)));
        }

        public static string Describe(RaidStance stance)
        {
            switch (stance)
            {
                case RaidStance.Counter: return "Counter: normal defence, and a beaten raider pays you their stake twice over";
                case RaidStance.Hoard: return "Hoard: weaker defence, but a raider who gets through takes half as much";
                default: return "Guard: 30% stronger defence, and a beaten raider loses their stake to you";
            }
        }

        public static bool TryParseStance(string text, out RaidStance stance)
        {
            stance = RaidStance.Guard;
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "guard": case "g": stance = RaidStance.Guard; return true;
                case "counter": case "c": stance = RaidStance.Counter; return true;
                case "hoard": case "h": stance = RaidStance.Hoard; return true;
                default: return false;
            }
        }
    }
}
