using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using DiscordBot.Games.Models.BattleArena;
using Common.Helpers;

namespace DiscordBot.Games
{
    public class BattleArena : BaseMultiplayerGame<BattleArenaPlayer>
    {
        private const int MaxDice = 3;
        internal Dictionary<BattleArenaPlayer, IEnumerable<(int DiceRoll, AttackType AttackType)>> CurrentPlayerAttacks;
        public bool IsReadyToResolve => CurrentPlayerAttacks.Count == Players.Count;

        public List<BattleArenaPlayer> DeathOrderList { get; set; } = new List<BattleArenaPlayer>();
        public List<BattleArenaPlayer> PlayerRanking => Players.OrderByDescending(p => p.HitPoints).ToList();




        public BattleArena()
        {
            MinimumRequiredPlayers = 2;
            CurrentPlayerAttacks = new Dictionary<BattleArenaPlayer, IEnumerable<(int DiceRoll, AttackType AttackType)>>();
        }

        public override double GetWinnings(BattleArenaPlayer player)
        {
            int playerCount = Players.Count();
            if (playerCount < MinimumRequiredPlayers) throw new Exception($"Something went wrong here. The game started with {playerCount} players - less than the minimum number of players: {MinimumRequiredPlayers}");

            if (PlayerRanking.IndexOf(player) == 0) //if the player has the highest HP
                return player.BetAmount * 2;
            else
                return 0;
        }

        public IList<BattleArenaPlayer> GetPlayerOrder()
        {
            var randomOrderList = new List<BattleArenaPlayer>(Players);
            randomOrderList.Shuffle();

            return randomOrderList;
        }

        public void RollDice(int numberOfDice, BattleArenaPlayer player)
        {
            if (numberOfDice > MaxDice)
                numberOfDice = MaxDice;

            var diceRolls = DiceRoller.RollDice(numberOfDice, 6);
            var results = new List<(int diceRoll, AttackType attackType)>();
            diceRolls.ForEach((dr) =>
            {
                AttackType atkType = GetAttackType(dr);
                results.Add((dr, atkType));
            });
            player.HasAttacked = true;
            CurrentPlayerAttacks.Add(player, results);
        }

        public IEnumerable<AttackInfo> ResolveRolls()
        {
            if (!IsReadyToResolve)
                throw new Exception($"Method {nameof(ResolveRolls)} cannot be called unless all players have attacked. Something went wrong here in game {GameGuid}");

            var attackInfos = new List<AttackInfo>();
            foreach (var player in GetPlayerOrder())
            {
                if (player.IsDead)
                    continue;

                var diceResults = CurrentPlayerAttacks[player];

                foreach (var diceResult in diceResults)
                {
                    var enemies = Players.Where(p => p != player && !p.IsDead);
                    if (enemies.Count() == 0)
                    {
                        Ended = true;
                        break;
                    }

                    double attackDmg;
                    switch (diceResult.AttackType)
                    {
                        case AttackType.Attack:
                            var attackInfo1 = new AttackInfo()
                            {
                                PlayerAttacking = player,
                                AttackType = diceResult.AttackType,
                                DiceRoll = diceResult.DiceRoll
                            };

                            foreach (var enemy in enemies)
                            {
                                attackDmg = player.CoinAccount.BattlePerson.Attack * ((100 - enemy.CoinAccount.BattlePerson.Defense) / 100);
                                enemy.HitPoints -= attackDmg;
                                attackInfo1.Attacks.Add(new Attack()
                                {
                                    PlayerAttacked = enemy,
                                    AttackDamage = attackDmg
                                });
                                UpdateDeathStatus(enemy);
                            }

                            attackInfos.Add(attackInfo1);
                            break;
                        case AttackType.AttackSelf:
                            attackDmg = player.CoinAccount.BattlePerson.Attack;
                            var attackInfo2 = new AttackInfo()
                            {
                                PlayerAttacking = player,
                                AttackType = diceResult.AttackType,
                                DiceRoll = diceResult.DiceRoll,
                                Attacks = new List<Attack>() { new Attack() { PlayerAttacked = player, AttackDamage = attackDmg } }
                            };
                            player.HitPoints -= attackDmg; //no defense when attacking self
                            attackInfos.Add(attackInfo2);
                            break;
                        case AttackType.CritAttack:
                            var attackInfo3 = new AttackInfo()
                            {
                                PlayerAttacking = player,
                                AttackType = diceResult.AttackType,
                                DiceRoll = diceResult.DiceRoll
                            };

                            foreach (var enemy in enemies)
                            {
                                attackDmg = player.CoinAccount.BattlePerson.Attack * ((100 - enemy.CoinAccount.BattlePerson.Defense) / 100) * (1 + player.CoinAccount.BattlePerson.CritMultiplier);
                                enemy.HitPoints -= attackDmg;
                                attackInfo3.Attacks.Add(new Attack()
                                {
                                    PlayerAttacked = enemy,
                                    AttackDamage = attackDmg
                                });
                                UpdateDeathStatus(enemy);
                            }
                            attackInfos.Add(attackInfo3);
                            break;
                    }

                    UpdateDeathStatus(player);
                    if (player.IsDead)
                        break;
                }
            }

            CurrentPlayerAttacks.Clear();
            Players.ForEach(p => p.HasAttacked = false);
            return attackInfos;
        }

        private void UpdateDeathStatus(BattleArenaPlayer player)
        {
            if (player.IsDead)
            {
                player.IsFinishedPlaying = true;

                if (!DeathOrderList.Contains(player))
                {
                    DeathOrderList.Add(player);
                }
            }
        }

        private AttackType GetAttackType(int diceRoll)
        {
            switch (diceRoll)
            {
                case 1:
                case 2:
                    return AttackType.AttackSelf;
                case 3:
                case 4:
                case 5:
                    return AttackType.Attack;
                case 6:
                    return AttackType.CritAttack;
                default:
                    return AttackType.Attack;
            }
        }

        public enum AttackType
        {
            AttackSelf,
            Attack,
            CritAttack
        }
    }
}
