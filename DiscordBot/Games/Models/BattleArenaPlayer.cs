using DiscordBot.Games.Models.BattleArena;
using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class BattleArenaPlayer : BasePlayer
    {
        public const double MaxHitPoints = 100;
        public const double HealAmount = 25;

        public double HitPoints { get; set; } = MaxHitPoints;
        public bool IsDead => HitPoints <= 0;
        public bool HasAttacked { get; set; }

        //card ability state, the ability can be used once per game
        public bool AbilityUsed { get; set; }
        public bool ShieldActive { get; set; }    //halves damage taken until the round resolves
        public bool ExtraDieArmed { get; set; }   //the next roll gets one extra die
        public bool CritBoostActive { get; set; } //crits deal more damage until the round resolves

        public string GetFormattedStanding()
        {
            if (HitPoints < 0)
                return $"0";
            else
                return HitPoints.ToString();
        }
    }
}
