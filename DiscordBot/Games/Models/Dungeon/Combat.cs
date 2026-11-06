using System;

namespace DiscordBot.Games.Models.Dungeon
{
    /// <summary>
    /// Everything a fighter brings to a fight. Percentages are whole percent points.
    /// The same stats are used for players, monsters, bosses and PvP fighters.
    /// </summary>
    public class Combatant
    {
        public string Name { get; set; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Attack { get; set; }
        public int Defense { get; set; }
        /// <summary>Added to the base crit chance.</summary>
        public int CritBonus { get; set; }
        /// <summary>Subtracts from the attacker's hit chance.</summary>
        public int Dodge { get; set; }
        /// <summary>Added to the attacker's hit chance.</summary>
        public int Accuracy { get; set; }
        /// <summary>Share of damage dealt that heals the attacker.</summary>
        public int Lifesteal { get; set; }
        /// <summary>Share of damage taken that is reflected back at the attacker.</summary>
        public int Thorns { get; set; }
        /// <summary>HP healed at the start of each round.</summary>
        public int Regen { get; set; }
        /// <summary>HP healed once per fight, when HP falls to a quarter or less.</summary>
        public int SecondWindHeal { get; set; }
        public bool SecondWindUsed { get; set; }

        public bool Alive => Hp > 0;

        public void Heal(int amount)
        {
            Hp = Math.Min(MaxHp, Hp + amount);
        }
    }

    public class AttackOutcome
    {
        public bool Hit { get; set; }
        public bool Crit { get; set; }
        public int Damage { get; set; }
        public int Healed { get; set; }
        public int Reflected { get; set; }
    }

    /// <summary>
    /// One attack at a time. Randomness comes from the hit roll, the d10 damage roll, and the crit roll,
    /// so the same stats can swing a lot from one fight to the next.
    /// </summary>
    public static class Combat
    {
        /// <summary>Round cap for every fight, dungeon and PvP.</summary>
        public const int MaxRounds = 10;
        public const int BaseAccuracy = 85;
        /// <summary>Taken off every damage roll, so fights last a few rounds rather than one. Tuned with the balance sim.</summary>
        public const int DamageOffset = 3;
        public const int BaseCrit = 10;
        public const int MinHitChance = 5;
        public const int MaxHitChance = 95;

        public static AttackOutcome Attack(Combatant attacker, Combatant defender, Random random)
        {
            int hitChance = Math.Clamp(BaseAccuracy + attacker.Accuracy - defender.Dodge, MinHitChance, MaxHitChance);
            if (random.Next(100) >= hitChance)
                return new AttackOutcome { Hit = false };

            int damage = Math.Max(1, attacker.Attack + random.Next(1, 11) - defender.Defense / 2 - DamageOffset);
            bool crit = random.Next(100) < BaseCrit + attacker.CritBonus;
            if (crit)
                damage *= 2;

            defender.Hp -= damage;
            var outcome = new AttackOutcome { Hit = true, Crit = crit, Damage = damage };

            if (attacker.Lifesteal > 0)
            {
                int before = attacker.Hp;
                attacker.Heal((int)Math.Round(damage * attacker.Lifesteal / 100.0));
                outcome.Healed = attacker.Hp - before;
            }
            if (defender.Thorns > 0)
            {
                outcome.Reflected = Math.Max(1, (int)Math.Round(damage * defender.Thorns / 100.0));
                attacker.Hp -= outcome.Reflected;
            }
            return outcome;
        }

        /// <summary>Applies start-of-round regeneration.</summary>
        public static void StartRound(Combatant fighter)
        {
            if (fighter.Regen > 0 && fighter.Alive)
                fighter.Heal(fighter.Regen);
        }

        /// <summary>Second Wind triggers once per fight, when HP falls to a quarter or less.</summary>
        public static bool TrySecondWind(Combatant fighter)
        {
            if (fighter.SecondWindUsed || fighter.SecondWindHeal <= 0 || !fighter.Alive || fighter.Hp > fighter.MaxHp / 4)
                return false;
            fighter.SecondWindUsed = true;
            fighter.Heal(fighter.SecondWindHeal);
            return true;
        }
    }
}
