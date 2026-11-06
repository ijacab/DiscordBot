using System;

namespace DiscordBot.Games.Models.Dungeon
{
    public enum LootTier { Common, Rare, Epic }

    public class LootResult
    {
        public LootTier Tier { get; set; }
        public string Name { get; set; }
        public int AttackGain { get; set; }
        public int DefenseGain { get; set; }
        public int MaxHpGain { get; set; }
    }

    /// <summary>Loot boxes bought during the crawl. Tier odds and boost ranges are first guesses to tune with the balance sim.</summary>
    public static class LootBox
    {
        /// <summary>Box price as a fraction of the buy-in. Paid from cash, not the haul, and not refunded.</summary>
        public const double CostFraction = 0.10;

        private static readonly string[] CommonNames = { "Bent Nail", "Frayed Gloves", "Chipped Buckle" };
        private static readonly string[] RareNames = { "Silver Edge", "Runed Brace", "Lucky Coin" };
        private static readonly string[] EpicNames = { "Dragon Scale", "Crown of Margins", "Hedge Fund Charm" };

        public static double Cost(double buyIn) => Math.Round(buyIn * CostFraction, 2);

        /// <summary>Rolls a tier: Common 60%, Rare 30%, Epic 10%. Higher tiers give bigger boosts.</summary>
        public static LootResult Roll(Random random)
        {
            double roll = random.NextDouble();
            if (roll < 0.10)
                return new LootResult
                {
                    Tier = LootTier.Epic,
                    Name = EpicNames[random.Next(EpicNames.Length)],
                    AttackGain = random.Next(2, 4),
                    DefenseGain = random.Next(2, 4),
                    MaxHpGain = random.Next(8, 15)
                };
            if (roll < 0.40)
                return new LootResult
                {
                    Tier = LootTier.Rare,
                    Name = RareNames[random.Next(RareNames.Length)],
                    AttackGain = random.Next(1, 3),
                    DefenseGain = random.Next(1, 3),
                    MaxHpGain = random.Next(4, 9)
                };
            return new LootResult
            {
                Tier = LootTier.Common,
                Name = CommonNames[random.Next(CommonNames.Length)],
                AttackGain = 1,
                DefenseGain = random.Next(0, 2),
                MaxHpGain = random.Next(2, 5)
            };
        }
    }
}
