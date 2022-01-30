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

        public int Level { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }

        public double Attack { get; }
        public double AttackBonusPerLevel { get; }
        public double TotalAttack
        {
            get
            {
                double totalAttack = Attack + (AttackBonusPerLevel * Level);
                return totalAttack > MaxAttack ? MaxAttack : totalAttack;
            }
        }

        public double Defense { get; }
        public double DefenseBonusPerLevel { get; }
        public double TotalDefense
        {
            get
            {
                double totalDefense = Defense + (DefenseBonusPerLevel * Level);
                return totalDefense > MaxDefense ? MaxDefense : totalDefense;
            }
        }

        public double CritChancePercent { get; }
        public double CritChancePercentBonusPerLevel { get; }
        public double TotalCritChancePercent
        {
            get
            {
                double totalCritChancePercent = CritChancePercent + (CritChancePercentBonusPerLevel * Level);
                return totalCritChancePercent > MaxCritChancePercent ? MaxCritChancePercent : totalCritChancePercent;
            }
        }

        public double CritMultiplier { get; }
        public double CritMultiplierBonusPerLevel { get; }
        public double TotalCritMultiplier
        {
            get
            {
                double totalCritMultiplier = CritMultiplier + (CritMultiplierBonusPerLevel * Level);
                return totalCritMultiplier > MaxCritMultipler ? MaxCritMultipler : totalCritMultiplier;
            }
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
