using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models.BattleArena
{
    public class OtherStats
    {
        public const double MaxBonusBetRewardPercent = 100;
        public const double MaxBonusInterestPercent = 500;
        
        [JsonProperty]
        private double BonusBetRewardPercent { get; }
        [JsonProperty]
        private double BonusBetRewardPercentPerLevel { get; }
        public double GetTotalBonusBetRewardPercent(int level)
        {
            double totalBonusBetRewardPercent = BonusBetRewardPercent + (BonusBetRewardPercentPerLevel * level);
            return totalBonusBetRewardPercent > MaxBonusBetRewardPercent ? MaxBonusBetRewardPercent : totalBonusBetRewardPercent;
        }

        [JsonProperty]
        private double BonusInterestPercent { get; }
        [JsonProperty]
        private double BonusInterestPercentPerLevel { get; }
        public double GetTotalBonusInterestPercent(int level)
        {
            double totalBonusInterestPercent = BonusInterestPercent + (BonusInterestPercentPerLevel * level);
            return totalBonusInterestPercent > MaxBonusInterestPercent ? MaxBonusInterestPercent : totalBonusInterestPercent;
        }

        public OtherStats(double bonusBetRewardPercent, double bonusBetRewardPercentPerLevel,
            double bonusInterestPercent, double bonusInterestPercentPerLevel)
        {
            BonusBetRewardPercent = bonusBetRewardPercent;
            BonusBetRewardPercentPerLevel = bonusBetRewardPercentPerLevel;
            BonusInterestPercent = bonusInterestPercent;
            BonusInterestPercentPerLevel = bonusInterestPercentPerLevel;
        }
    }
}
