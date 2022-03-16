
using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models.BattleArena
{
    public class BattlePerson
    {
        private static readonly Random _random = new Random(); //best practice to use static object for random, not an instance for each time you want to use it
        private readonly BattleStats _battleStats;
        private readonly OtherStats _otherStats;

        public string Name { get; }
        public int Level { get; set; }
        public string FilePath { get; set; } 

        //battle stats
        public double Attack => _battleStats.GetTotalAttack(Level);
        public double Defense => _battleStats.GetTotalDefense(Level);
        public double CritMultiplier => _battleStats.GetTotalCritMultiplier(Level);
        public double CritChancePercent => _battleStats.GetTotalCritChancePercent(Level);
        public int Wins => _battleStats.Wins;
        public int Losses => _battleStats.Losses;
        public int Draws => _battleStats.Draws;

        //other stats
        public double BonusBetRewardPercent => _otherStats.GetTotalBonusBetRewardPercent(Level);
        public double BonusInterestPercent => _otherStats.GetTotalBonusInterestPercent(Level);

        public BattlePerson(string name)
        {
            Name = name;
            _battleStats = GetRandomBattleStats();
            _otherStats = GetRandomOtherStats();
        }

        private BattleStats GetRandomBattleStats()
        {
            const double maxStartStatsMultiplier = 0.3;
            const double maxPerLevelMultiplier = 0.03; //3% max possible gain per level

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

        private OtherStats GetRandomOtherStats()
        {
            const double maxStartStatsMultiplier = 0.3;
            const double maxPerLevelMultiplier = 0.03; //3% max possible gain per level

            double betRewardBonus = maxStartStatsMultiplier * _random.NextDouble() * OtherStats.MaxBonusBetRewardPercent;
            double betRewardBonusPerLevel = maxPerLevelMultiplier * _random.NextDouble() * OtherStats.MaxBonusBetRewardPercent;

            double interestBonus = maxStartStatsMultiplier * _random.NextDouble() * OtherStats.MaxBonusInterestPercent;
            double interestBonusPerLevel = maxPerLevelMultiplier * _random.NextDouble() * OtherStats.MaxBonusInterestPercent;

            return new OtherStats(betRewardBonus, betRewardBonusPerLevel, interestBonus, interestBonusPerLevel);
        }
    }


}
