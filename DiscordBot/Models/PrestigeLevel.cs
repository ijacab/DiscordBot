using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Models
{
    public class PrestigeLevel
    {
        public readonly double RequiredMoneyToReachNextLevel;
        private const int _baseAmount = 1000000;
        public PrestigeLevel(int level = 0)
        {
            if(level == 0)
            {
                RequiredMoneyToReachNextLevel = _baseAmount;
            }
            else
            {
                RequiredMoneyToReachNextLevel = _baseAmount * Math.Pow(10, level);
            }
        }
    }
}
