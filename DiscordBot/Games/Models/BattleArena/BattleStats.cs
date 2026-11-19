using System;

namespace DiscordBot.Games.Models.BattleArena
{
    public class BattleStats
    {
        public const double MaxAttack = 100;
        public const double MaxDefense = 75;
        public const double MaxCritMultipler = 2;

        //public setters so the stats are saved with the card and read back unchanged
        public double Attack { get; set; }
        public double AttackBonusPerLevel { get; set; }
        public double Defense { get; set; }
        public double DefenseBonusPerLevel { get; set; }
        public double CritMultiplier { get; set; }
        public double CritMultiplierBonusPerLevel { get; set; }

        /// <param name="growth">Scales the per-level gain, from the card's focus (<see cref="CardLevels.Growth"/>). 1 is the card's own growth.</param>
        public double GetTotalAttack(int level, double growth = 1)
        {
            double totalAttack = Attack + (AttackBonusPerLevel * growth * level);
            return totalAttack > MaxAttack ? MaxAttack : totalAttack;
        }

        public double GetTotalDefense(int level, double growth = 1)
        {
            double totalDefense = Defense + (DefenseBonusPerLevel * growth * level);
            return totalDefense > MaxDefense ? MaxDefense : totalDefense;
        }

        public double GetTotalCritMultiplier(int level, double growth = 1)
        {
            double totalCritMultiplier = CritMultiplier + (CritMultiplierBonusPerLevel * growth * level);
            return totalCritMultiplier > MaxCritMultipler ? MaxCritMultipler : totalCritMultiplier;
        }

        /// <summary>Rolls new starting stats. Rarity scales the starting values only, not the per-level growth.</summary>
        public static BattleStats Generate(Random random, double rarityMultiplier)
        {
            const double maxStartStatsMultiplier = 0.3;
            const double maxPerLevelMultiplier = 0.03; //3% max possible gain per level

            double Start(double max) => maxStartStatsMultiplier * random.NextDouble() * max * rarityMultiplier;
            double PerLevel(double max) => maxPerLevelMultiplier * random.NextDouble() * max;

            return new BattleStats
            {
                Attack = Start(MaxAttack),
                AttackBonusPerLevel = PerLevel(MaxAttack),
                Defense = Start(MaxDefense),
                DefenseBonusPerLevel = PerLevel(MaxDefense),
                CritMultiplier = Start(MaxCritMultipler),
                CritMultiplierBonusPerLevel = PerLevel(MaxCritMultipler)
            };
        }
    }
}
