using System;

namespace DiscordBot.Games.Models.Cards
{
    public enum CardRarity
    {
        Common,
        Rare,
        Epic,
        Legendary,
        /// <summary>A face the site messed up, voted up by players (<see cref="GangRules.VoteGlitch"/>). Never rolled or fused.</summary>
        Glitched
    }

    /// <summary>How a card is rolled: its rarity and whether it's a foil.</summary>
    public static class CardTraits
    {
        public const double LegendaryChance = 0.03;
        public const double EpicChance = 0.12;
        public const double RareChance = 0.25;

        /// <summary>Common 60%, Rare 25%, Epic 12%, Legendary 3% (times <paramref name="legendaryBoost"/>, taken from the commons).</summary>
        public static CardRarity RollRarity(Random random, double legendaryBoost = 1)
        {
            double roll = random.NextDouble();
            double legendary = LegendaryChance * legendaryBoost;
            if (roll < legendary) return CardRarity.Legendary;
            if (roll < legendary + EpicChance) return CardRarity.Epic;
            if (roll < legendary + EpicChance + RareChance) return CardRarity.Rare;
            return CardRarity.Common;
        }

        /// <summary>A foil has the holographic picture and <see cref="GangRules.FoilBonus"/> more power. Roughly 1 in 20 cards.</summary>
        public const double FoilChance = 0.05;

        public static bool RollFoil(Random random) => random.NextDouble() < FoilChance;
    }
}
