using DiscordBot.Games.Models.BattleArena;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Games.Models
{
    public class BattleArenaPlayer : BasePlayer
    {
        public double HitPoints { get; set; } = 100;
        public bool IsDead => HitPoints <= 0;
        public BattlePerson BattlePerson { get; set; }

        public string GetFormattedStanding()
        {
            if (HitPoints < 0)
                return $"0";
            else
                return HitPoints.ToString();
        }
    }
}
