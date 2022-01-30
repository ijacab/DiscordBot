
using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models.BattleArena
{
    public class BattlePerson
    {
        private static readonly Random _random = new Random();

        public string Name { get; }
        public BattleStats BattleStats { get; set; }
        public OtherStats OtherStats { get; set; }
        
        public BattlePerson(string name)
        {
            Name = name;
            BattleStats = GetRandomBattleStats();
        }

        private BattleStats GetRandomBattleStats()
        {
            const double maxPerLevelMultiplier = 0.03; //3% max possible gain per level
            const double maxStartStatsMultiplier = 0.3;

            double attack = maxStartStatsMultiplier * _random.NextDouble() * BattleStats.MaxAttack;
            double attackPerLevel = maxPerLevelMultiplier * _random.NextDouble() * BattleStats.MaxAttack;

            double defense = maxStartStatsMultiplier * _random.NextDouble() * BattleStats.MaxDefense;
            double defensePerLevel = maxPerLevelMultiplier * _random.NextDouble() * BattleStats.MaxDefense;

            double critChancePercent = maxStartStatsMultiplier * _random.NextDouble() * BattleStats.MaxCritChancePercent;
            double critChancePercentPerLevel = maxPerLevelMultiplier * _random.NextDouble() * BattleStats.MaxCritChancePercent;

            double critMultiplier = maxStartStatsMultiplier * _random.NextDouble() * BattleStats.MaxCritMultipler;
            double critMultiplierPerLevel = maxPerLevelMultiplier * _random.NextDouble() * BattleStats.MaxCritMultipler;

            return new BattleStats(attack, attackPerLevel, defense, defensePerLevel, critChancePercent, critChancePercentPerLevel, critMultiplier, critMultiplierPerLevel);
        }
    }


}
