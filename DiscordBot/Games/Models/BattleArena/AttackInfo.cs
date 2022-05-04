using System;
using System.Collections.Generic;
using System.Text;
using static DiscordBot.Games.BattleArena;

namespace DiscordBot.Games.Models.BattleArena
{
    public class AttackInfo
    {
        public BattleArenaPlayer PlayerAttacking { get; set; }
        public int DiceRoll { get; set; }
        public AttackType AttackType { get; set; }
        public List<Attack> Attacks { get; set; } = new List<Attack>();
    }

    public class Attack
    {
        public BattleArenaPlayer PlayerAttacked { get; set; }
        public double AttackDamage { get; set; }
    }
}
