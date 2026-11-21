using System;

namespace DiscordBot.Games.Models.Cards
{
    public enum CardRarity
    {
        Common,
        Rare,
        Epic,
        Legendary
    }

    /// <summary>How a pull is rolled: its rarity and whether it's a foil.</summary>
    public static class CardTraits
    {
        public static CardRarity RollRarity(Random random)
        {
            double roll = random.NextDouble();
            if (roll < 0.03) return CardRarity.Legendary;
            if (roll < 0.15) return CardRarity.Epic;
            if (roll < 0.40) return CardRarity.Rare;
            return CardRarity.Common;
        }

        /// <summary>A foil card is a rare cosmetic variant (a holographic picture, no extra power). Roughly 1 in 20 pulls.</summary>
        public const double FoilChance = 0.05;

        public static bool RollFoil(Random random) => random.NextDouble() < FoilChance;

        public static Archetype RollArchetype(Random random) => Archetypes.Pullable[random.Next(Archetypes.Pullable.Length)];
    }
}
