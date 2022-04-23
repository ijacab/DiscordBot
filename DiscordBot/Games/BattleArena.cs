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
        internal List<BattleArenaPlayer> PlayersWaitingToAttack;

        public List<BattleArenaPlayer> DeathOrderList = new List<BattleArenaPlayer>();
        public List<BattleArenaPlayer> PlayerRanking => Players.OrderByDescending(p => p.HitPoints).ToList();
        

        public BattleArena()
        {
            MinimumRequiredPlayers = 2;
            PlayersWaitingToAttack = new List<BattleArenaPlayer>(Players);
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

        public IEnumerable<(int DiceRoll, AttackType AttackType)> RollDice(int numberOfDice, BattleArenaPlayer player)
        {
            if (numberOfDice > MaxDice)
                numberOfDice = MaxDice;

            var diceRolls = DiceRoller.RollDice(numberOfDice, 6);
            var results = new List<(int diceRoll, AttackType attackType)>();

            foreach (int diceRoll in diceRolls)
            {
                AttackType atkType = GetAttackType(diceRoll);
                results.Add((diceRoll, atkType));

                var enemies = Players.Where(p => p != player && !p.IsDead);
                if (enemies.Count() == 0)
                {
                    Ended = true;
                    break;
                }

                switch (atkType)
                {
                    case AttackType.Attack:
                        foreach (var enemy in enemies)
                        {
                            enemy.HitPoints -= player.BattlePerson.Attack * ((100 - enemy.BattlePerson.Defense) / 100);
                            UpdateDeathStatus(enemy, out _);
                        }
                        break;
                    case AttackType.AttackSelf:
                        player.HitPoints -= player.BattlePerson.Attack; //no defense when attacking self
                        break;
                    case AttackType.CritAttack:
                        foreach (var enemy in enemies)
                        {
                            enemy.HitPoints -= player.BattlePerson.Attack * ((100 - enemy.BattlePerson.Defense) / 100) * (1 + player.BattlePerson.CritMultiplier);
                            UpdateDeathStatus(enemy, out _);
                        }
                        break;
                }

                UpdateDeathStatus(player, out bool wasInserted);
                if (wasInserted)
                    break;
            }

            return results;
        }

        private void UpdateDeathStatus(BattleArenaPlayer player, out bool wasInserted)
        {
            wasInserted = false;
            if(player.IsDead)
            {
                player.IsFinishedPlaying = true;

                if (!DeathOrderList.Contains(player))
                {
                    DeathOrderList.Add(player);
                    wasInserted = true;
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
