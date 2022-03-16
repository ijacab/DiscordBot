using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models.BattleArena
{
    public class BattleStats
    {
        public const double MaxAttack = 100;
        public const double MaxDefense = 100;
        public const double MaxCritChancePercent = 100;
        public const double MaxCritMultipler = 10;

        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Draws { get; set; }

        [JsonProperty]
        private double Attack { get; }
        [JsonProperty]
        private double AttackBonusPerLevel { get; }
        public double GetTotalAttack(int level)
        {
            double totalAttack = Attack + (AttackBonusPerLevel * level);
            return totalAttack > MaxAttack ? MaxAttack : totalAttack;
        }

        [JsonProperty]
        private double Defense { get; }
        [JsonProperty]
        private double DefenseBonusPerLevel { get; }
        public double GetTotalDefense(int level)
        {
            double totalDefense = Defense + (DefenseBonusPerLevel * level);
            return totalDefense > MaxDefense ? MaxDefense : totalDefense;
        }

        [JsonProperty]
        private double CritChancePercent { get; }
        [JsonProperty]
        private double CritChancePercentBonusPerLevel { get; }
        public double GetTotalCritChancePercent(int level)
        {
            double totalCritChancePercent = CritChancePercent + (CritChancePercentBonusPerLevel * level);
            return totalCritChancePercent > MaxCritChancePercent ? MaxCritChancePercent : totalCritChancePercent;
        }

        [JsonProperty]
        private double CritMultiplier { get; }
        [JsonProperty]
        private double CritMultiplierBonusPerLevel { get; }
        public double GetTotalCritMultiplier(int level)
        {
            double totalCritMultiplier = CritMultiplier + (CritMultiplierBonusPerLevel * level);
            return totalCritMultiplier > MaxCritMultipler ? MaxCritMultipler : totalCritMultiplier;

        }

        public BattleStats(double attack, double attackPerLevel,
            double defense, double defensePerLevel,
            double critChancePercent, double critChancePercentPerLevel,
            double critMultiplier, double critMultiplierPerLevel)
        {
            Attack = attack;
            AttackBonusPerLevel = attackPerLevel;
            Defense = defense;
            DefenseBonusPerLevel = defensePerLevel;
            CritChancePercent = critChancePercent;
            CritChancePercentBonusPerLevel = critChancePercentPerLevel;
            CritMultiplier = critMultiplier;
            CritMultiplierBonusPerLevel = critMultiplierPerLevel;
        }

    }
}
