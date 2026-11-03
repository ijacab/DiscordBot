using System;

namespace DiscordBot.Games.Models.BattleArena
{
    public enum CardRarity
    {
        Common,
        Rare,
        Epic,
        Legendary
    }

    public enum CardAbility
    {
        None,
        Heal,
        Shield,
        Reroll, //same slot as the old ExtraDie so saved cards keep their number
        CritBoost
    }

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

        /// <summary>Scales the starting stats. Higher rarity gives stronger cards from the first pull.</summary>
        public static double StatMultiplier(this CardRarity rarity)
        {
            switch (rarity)
            {
                case CardRarity.Rare: return 1.2;
                case CardRarity.Epic: return 1.4;
                case CardRarity.Legendary: return 1.7;
                default: return 1.0;
            }
        }

        /// <summary>A foil card is a rare cosmetic variant. Roughly 1 in 20 pulls.</summary>
        public const double FoilChance = 0.05;

        public static bool RollFoil(Random random)
        {
            return random.NextDouble() < FoilChance;
        }

        public static CardAbility RollAbility(Random random)
        {
            //every ability except None
            return (CardAbility)random.Next(1, Enum.GetValues(typeof(CardAbility)).Length);
        }

        public static string Describe(this CardAbility ability)
        {
            switch (ability)
            {
                case CardAbility.Heal: return "Restore 25 HP, once per game.";
                case CardAbility.Shield: return "Halve the damage you take in your next round, once per game.";
                case CardAbility.Reroll: return "Roll your d20 twice on your next roll and keep the higher, once per game.";
                case CardAbility.CritBoost: return "Your crits deal 50% more damage in your next round, once per game.";
                default: return "No ability.";
            }
        }
    }
}
