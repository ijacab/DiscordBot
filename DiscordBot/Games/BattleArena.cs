using DiscordBot.Exceptions;
using DiscordBot.Games.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using DiscordBot.Games.Models.BattleArena;
using Common.Helpers;

namespace DiscordBot.Games
{
    public class BattleArena : BaseMultiplayerGame<BattleArenaPlayer>
    {
        private const int MaxDice = 3;
        private const double CritBoostMultiplier = 1.5;

        internal Dictionary<BattleArenaPlayer, IEnumerable<(int DiceRoll, AttackType AttackType)>> CurrentPlayerAttacks;
        public int Round { get; private set; } = 1;

        public IEnumerable<BattleArenaPlayer> LivingPlayers => Players.Where(p => !p.IsDead);

        /// <summary>A round resolves once every living player has rolled. Dead players don't roll.</summary>
        public bool IsReadyToResolve => LivingPlayers.All(p => CurrentPlayerAttacks.ContainsKey(p));

        /// <summary>The game is over once one player (or nobody) is left alive.</summary>
        public bool IsOver => LivingPlayers.Count() <= 1;

        public List<BattleArenaPlayer> DeathOrderList { get; set; } = new List<BattleArenaPlayer>();

        /// <summary>Best first: survivors ranked by HP, then players who died later ranked above players who died earlier.</summary>
        public List<BattleArenaPlayer> PlayerRanking => Players
            .OrderByDescending(p => p.IsDead ? 0 : 1)
            .ThenByDescending(p => p.IsDead ? DeathOrderList.IndexOf(p) : 0)
            .ThenByDescending(p => p.HitPoints)
            .ToList();

        public BattleArena()
        {
            MinimumRequiredPlayers = 2;
            CurrentPlayerAttacks = new Dictionary<BattleArenaPlayer, IEnumerable<(int DiceRoll, AttackType AttackType)>>();
        }

        /// <summary>The winner takes every bet placed in the game.</summary>
        public override double GetWinnings(BattleArenaPlayer player)
        {
            int playerCount = Players.Count();
            if (playerCount < MinimumRequiredPlayers) throw new Exception($"Something went wrong here. The game started with {playerCount} players - less than the minimum number of players: {MinimumRequiredPlayers}");

            return PlayerRanking.First() == player ? Players.Sum(p => p.BetAmount) : 0;
        }

        public IList<BattleArenaPlayer> GetPlayerOrder()
        {
            var randomOrderList = new List<BattleArenaPlayer>(Players);
            randomOrderList.Shuffle();

            return randomOrderList;
        }

        public void RollDice(int numberOfDice, BattleArenaPlayer player)
        {
            int maxDice = player.ExtraDieArmed ? MaxDice + 1 : MaxDice;
            player.ExtraDieArmed = false; //the extra die is used up by this roll
            numberOfDice = Math.Clamp(numberOfDice, 1, maxDice);

            var diceRolls = DiceRoller.RollDice(numberOfDice, 6);
            var results = new List<(int diceRoll, AttackType attackType)>();
            diceRolls.ForEach((dr) =>
            {
                AttackType atkType = GetAttackType(dr);
                results.Add((dr, atkType));
            });
            player.HasAttacked = true;
            CurrentPlayerAttacks[player] = results;
        }

        /// <summary>Uses the player's card ability. Each player can use their ability once per game.</summary>
        /// <returns>What the ability did.</returns>
        public string UseAbility(BattleArenaPlayer player)
        {
            if (!Started) throw new BadInputException("The game has not started yet.");
            if (player.AbilityUsed) throw new BadInputException("You have already used your ability in this game.");

            CardAbility ability = player.CoinAccount?.BattlePerson?.Ability ?? CardAbility.None;
            switch (ability)
            {
                case CardAbility.Heal:
                    player.HitPoints = Math.Min(BattleArenaPlayer.MaxHitPoints, player.HitPoints + BattleArenaPlayer.HealAmount);
                    break;
                case CardAbility.Shield:
                    player.ShieldActive = true;
                    break;
                case CardAbility.ExtraDie:
                    player.ExtraDieArmed = true;
                    break;
                case CardAbility.CritBoost:
                    player.CritBoostActive = true;
                    break;
                default:
                    throw new BadInputException("Your card does not have an ability.");
            }

            player.AbilityUsed = true;
            return ability.Describe();
        }

        public IEnumerable<AttackInfo> ResolveRolls()
        {
            if (!IsReadyToResolve)
                throw new Exception($"Method {nameof(ResolveRolls)} cannot be called unless all living players have attacked. Something went wrong here in game {GameGuid}");

            var attackInfos = new List<AttackInfo>();
            foreach (var player in GetPlayerOrder())
            {
                if (player.IsDead)
                    continue;

                var diceResults = CurrentPlayerAttacks[player];

                foreach (var diceResult in diceResults)
                {
                    var enemies = LivingPlayers.Where(p => p != player).ToList();
                    if (enemies.Count == 0)
                        break;

                    var attackInfo = new AttackInfo()
                    {
                        PlayerAttacking = player,
                        AttackType = diceResult.AttackType,
                        DiceRoll = diceResult.DiceRoll
                    };

                    switch (diceResult.AttackType)
                    {
                        case AttackType.AttackSelf:
                            double selfDamage = ReduceForShield(player, player.CoinAccount.BattlePerson.Attack);
                            player.HitPoints -= selfDamage;
                            attackInfo.Attacks.Add(new Attack() { PlayerAttacked = player, AttackDamage = selfDamage });
                            break;
                        case AttackType.Attack:
                        case AttackType.CritAttack:
                            bool isCrit = diceResult.AttackType == AttackType.CritAttack;
                            foreach (var enemy in enemies)
                            {
                                double damage = DamageAgainst(player, enemy, isCrit);
                                enemy.HitPoints -= damage;
                                attackInfo.Attacks.Add(new Attack() { PlayerAttacked = enemy, AttackDamage = damage });
                                UpdateDeathStatus(enemy);
                            }
                            break;
                    }

                    attackInfos.Add(attackInfo);
                    UpdateDeathStatus(player);
                    if (player.IsDead)
                        break;
                }
            }

            foreach (var player in Players)
            {
                player.HasAttacked = false;
                player.ShieldActive = false;
                player.CritBoostActive = false;
            }
            CurrentPlayerAttacks.Clear();
            Round++;

            if (IsOver)
            {
                Ended = true;
                Players.ForEach(p => p.IsFinishedPlaying = true); //lets the game end now instead of waiting for the timer
            }

            return attackInfos;
        }

        private static double DamageAgainst(BattleArenaPlayer attacker, BattleArenaPlayer target, bool isCrit)
        {
            var attackerCard = attacker.CoinAccount.BattlePerson;
            var targetCard = target.CoinAccount.BattlePerson;

            double damage = attackerCard.Attack * ((100 - targetCard.Defense) / 100);
            if (isCrit)
            {
                damage *= 1 + attackerCard.CritMultiplier;
                if (attacker.CritBoostActive)
                    damage *= CritBoostMultiplier;
            }

            return ReduceForShield(target, damage);
        }

        private static double ReduceForShield(BattleArenaPlayer target, double damage)
        {
            return target.ShieldActive ? damage / 2 : damage;
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
