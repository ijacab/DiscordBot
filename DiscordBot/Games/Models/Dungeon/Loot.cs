using System;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Games.Models.Dungeon
{
    public enum LootTier { Common, Rare, Epic }

    public enum LootEffect { Attack, Defense, MaxHp, CritBonus, Dodge, Accuracy, Lifesteal, Thorns, Regen, GoldFinder, PotionPower, SecondWind }

    public enum ItemSprite { Sword, Shield, Amulet, Boots, Ring, Potion }

    public class LootEffectRoll
    {
        public LootEffect Effect { get; set; }
        public int Amount { get; set; }

        public string Describe()
        {
            switch (Effect)
            {
                case LootEffect.Attack: return $"Attack +{Amount}";
                case LootEffect.Defense: return $"Defense +{Amount}";
                case LootEffect.MaxHp: return $"Max HP +{Amount}";
                case LootEffect.CritBonus: return $"Crit chance +{Amount}%";
                case LootEffect.Dodge: return $"Dodge +{Amount}%";
                case LootEffect.Accuracy: return $"Accuracy +{Amount}%";
                case LootEffect.Lifesteal: return $"Lifesteal {Amount}% of damage";
                case LootEffect.Thorns: return $"Thorns {Amount}% of damage taken";
                case LootEffect.Regen: return $"Regenerates {Amount} HP each round";
                case LootEffect.GoldFinder: return $"+{Amount}% gold from rooms";
                case LootEffect.PotionPower: return $"Potions heal {Amount} more";
                case LootEffect.SecondWind: return $"Second Wind: heals {Amount} HP once per fight at low HP";
                default: return Effect.ToString();
            }
        }
    }

    public class LootItem
    {
        public LootTier Tier { get; set; }
        public string Name { get; set; }
        public ItemSprite Sprite { get; set; }
        public List<LootEffectRoll> Effects { get; } = new List<LootEffectRoll>();
    }

    /// <summary>
    /// Random loot that drops automatically. Higher tiers get more effects and bigger numbers.
    /// The tier odds and amounts are first guesses to tune with the balance sim.
    /// </summary>
    public static class Loot
    {
        /// <summary>Chance a monster drops loot when it's beaten.</summary>
        public const double MonsterDropChance = 0.35;
        /// <summary>Chance a treasure room drops loot.</summary>
        public const double TreasureDropChance = 0.60;

        private static readonly LootEffect[] AllEffects = (LootEffect[])Enum.GetValues(typeof(LootEffect));

        public static LootItem Roll(Random random, bool bossDrop = false)
        {
            double roll = random.NextDouble();
            LootTier tier;
            if (bossDrop)
                tier = roll < 0.75 ? LootTier.Rare : LootTier.Epic;
            else
                tier = roll < 0.10 ? LootTier.Epic : roll < 0.40 ? LootTier.Rare : LootTier.Common;

            int effectCount = tier == LootTier.Common ? 1 : tier == LootTier.Rare ? 2 : 3;
            var pool = AllEffects.Where(e => MinTier(e) <= tier).OrderBy(_ => random.Next()).ToList();

            var item = new LootItem
            {
                Tier = tier,
                Sprite = (ItemSprite)random.Next(Enum.GetValues(typeof(ItemSprite)).Length)
            };
            item.Name = $"{Adjective(tier, random)} {Noun(item.Sprite)}";
            foreach (LootEffect effect in pool.Take(effectCount))
                item.Effects.Add(new LootEffectRoll { Effect = effect, Amount = AmountFor(effect, tier, random) });
            return item;
        }

        /// <summary>The lowest tier that can roll this effect.</summary>
        public static LootTier MinTier(LootEffect effect)
        {
            switch (effect)
            {
                case LootEffect.Lifesteal:
                case LootEffect.Thorns:
                case LootEffect.SecondWind:
                    return LootTier.Rare;
                default:
                    return LootTier.Common;
            }
        }

        public static int AmountFor(LootEffect effect, LootTier tier, Random random)
        {
            (int low, int high) = Range(effect, tier);
            return random.Next(low, high + 1);
        }

        private static (int low, int high) Range(LootEffect effect, LootTier tier)
        {
            switch (effect)
            {
                case LootEffect.Attack: return tier == LootTier.Common ? (1, 1) : tier == LootTier.Rare ? (1, 2) : (2, 3);
                case LootEffect.Defense: return tier == LootTier.Common ? (0, 1) : tier == LootTier.Rare ? (1, 2) : (2, 3);
                case LootEffect.MaxHp: return tier == LootTier.Common ? (2, 4) : tier == LootTier.Rare ? (4, 8) : (8, 14);
                case LootEffect.CritBonus: return tier == LootTier.Common ? (3, 5) : tier == LootTier.Rare ? (6, 9) : (10, 15);
                case LootEffect.Dodge: return tier == LootTier.Common ? (3, 5) : tier == LootTier.Rare ? (6, 9) : (10, 14);
                case LootEffect.Accuracy: return tier == LootTier.Common ? (2, 4) : tier == LootTier.Rare ? (5, 7) : (8, 10);
                case LootEffect.Lifesteal: return tier == LootTier.Rare ? (10, 15) : (18, 25);
                case LootEffect.Thorns: return tier == LootTier.Rare ? (10, 15) : (18, 25);
                case LootEffect.Regen: return tier == LootTier.Common ? (1, 1) : tier == LootTier.Rare ? (2, 3) : (4, 6);
                case LootEffect.GoldFinder: return tier == LootTier.Common ? (10, 15) : tier == LootTier.Rare ? (20, 30) : (40, 60);
                case LootEffect.PotionPower: return tier == LootTier.Common ? (3, 5) : tier == LootTier.Rare ? (6, 10) : (14, 20);
                case LootEffect.SecondWind: return tier == LootTier.Rare ? (12, 15) : (22, 28);
                default: return (1, 1);
            }
        }

        private static string Adjective(LootTier tier, Random random)
        {
            string[] options;
            switch (tier)
            {
                case LootTier.Epic: options = new[] { "Dragon", "Gilded", "Margin", "Hedge Fund" }; break;
                case LootTier.Rare: options = new[] { "Silver", "Runed", "Lucky", "Warded" }; break;
                default: options = new[] { "Chipped", "Dented", "Frayed", "Rusty" }; break;
            }
            return options[random.Next(options.Length)];
        }

        private static string Noun(ItemSprite sprite)
        {
            switch (sprite)
            {
                case ItemSprite.Sword: return "Blade";
                case ItemSprite.Shield: return "Shield";
                case ItemSprite.Amulet: return "Amulet";
                case ItemSprite.Boots: return "Boots";
                case ItemSprite.Ring: return "Ring";
                default: return "Draught";
            }
        }
    }
}
