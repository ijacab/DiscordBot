using DiscordBot.Games.Models.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    public class CombatTests
    {
        private static Combatant Fighter(int hp = 100, int attack = 5, int defense = 0)
        {
            return new Combatant { Name = "F", Hp = hp, MaxHp = hp, Attack = attack, Defense = defense };
        }

        [Fact]
        public void RoundCap_IsTen()
        {
            Assert.Equal(10, Combat.MaxRounds);
        }

        [Fact]
        public void Miss_DoesNoDamage()
        {
            var attacker = Fighter(attack: 50);
            var defender = Fighter(hp: 100);
            defender.Dodge = 100; //hit chance is clamped to 5% at the bottom, so most attacks miss
            var random = new Random(1);

            int misses = 0;
            for (int i = 0; i < 200; i++)
            {
                AttackOutcome outcome = Combat.Attack(attacker, defender, random);
                if (!outcome.Hit)
                {
                    misses++;
                    Assert.Equal(0, outcome.Damage);
                }
            }

            Assert.True(misses > 150, $"expected most attacks to miss, got {misses} misses");
        }

        [Fact]
        public void Damage_VariesFromRollToRoll()
        {
            var random = new Random(2);
            var damages = new HashSet<int>();
            for (int i = 0; i < 300; i++)
            {
                var attacker = Fighter(attack: 5);
                var defender = Fighter(hp: 1000);
                AttackOutcome outcome = Combat.Attack(attacker, defender, random);
                if (outcome.Hit && !outcome.Crit)
                    damages.Add(outcome.Damage);
            }

            Assert.True(damages.Count >= 8, $"damage barely varies: only {damages.Count} different values");
        }

        [Fact]
        public void Crit_DoublesDamage()
        {
            var random = new Random(3);
            for (int i = 0; i < 200; i++)
            {
                var attacker = Fighter(attack: 10);
                attacker.CritBonus = 100; //crit chance is 110%, so every hit crits
                var defender = Fighter(hp: 1000);
                AttackOutcome outcome = Combat.Attack(attacker, defender, random);
                if (outcome.Hit)
                {
                    Assert.True(outcome.Crit);
                    Assert.True(outcome.Damage >= 2 * (10 + 1 - Combat.DamageOffset), $"crit damage {outcome.Damage} is below the doubled minimum");
                }
            }
        }

        [Fact]
        public void Lifesteal_HealsTheAttackerByTheDamageShare()
        {
            var random = new Random(4);
            for (int i = 0; i < 100; i++)
            {
                var attacker = Fighter(hp: 10, attack: 10);
                attacker.MaxHp = 1000;
                attacker.Lifesteal = 100;
                var defender = Fighter(hp: 1000);
                AttackOutcome outcome = Combat.Attack(attacker, defender, random);
                if (outcome.Hit)
                {
                    Assert.Equal(outcome.Damage, outcome.Healed);
                    Assert.Equal(10 + outcome.Damage, attacker.Hp);
                    return;
                }
            }
            Assert.True(false, "no hit happened, so lifesteal was not checked");
        }

        [Fact]
        public void Thorns_ReflectsDamageBackAtTheAttacker()
        {
            var random = new Random(5);
            for (int i = 0; i < 100; i++)
            {
                var attacker = Fighter(hp: 1000, attack: 10);
                var defender = Fighter(hp: 1000);
                defender.Thorns = 100;
                AttackOutcome outcome = Combat.Attack(attacker, defender, random);
                if (outcome.Hit)
                {
                    Assert.Equal(outcome.Damage, outcome.Reflected);
                    Assert.Equal(1000 - outcome.Reflected, attacker.Hp);
                    return;
                }
            }
            Assert.True(false, "no hit happened, so thorns were not checked");
        }

        [Fact]
        public void SecondWind_TriggersOnceAtAQuarterHp()
        {
            var fighter = Fighter(hp: 100);
            fighter.SecondWindHeal = 20;
            fighter.Hp = 25;

            Assert.True(Combat.TrySecondWind(fighter));
            Assert.Equal(45, fighter.Hp);
            fighter.Hp = 10;
            Assert.False(Combat.TrySecondWind(fighter));
        }

        [Fact]
        public void SecondWind_DoesNothingAboveAQuarterHp()
        {
            var fighter = Fighter(hp: 100);
            fighter.SecondWindHeal = 20;
            fighter.Hp = 26;

            Assert.False(Combat.TrySecondWind(fighter));
        }

        [Fact]
        public void Regen_HealsAtTheStartOfTheRound()
        {
            var fighter = Fighter(hp: 50);
            fighter.Hp = 40;
            fighter.Regen = 3;

            Combat.StartRound(fighter);

            Assert.Equal(43, fighter.Hp);
        }
    }
}
