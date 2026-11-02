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
        private const double CritBoostMultiplier = 1.5;
        private const int FocusCap = 20;

        /// <summary>The action each living player picked this round, with the d20 it rolled.</summary>
        internal class SubmittedAction
        {
            public BattleAction Action { get; set; }
            public int DiceRoll { get; set; }
        }

        internal Dictionary<BattleArenaPlayer, SubmittedAction> CurrentActions { get; } = new Dictionary<BattleArenaPlayer, SubmittedAction>();
        public int Round { get; private set; } = 1;

        public IEnumerable<BattleArenaPlayer> LivingPlayers => Players.Where(p => !p.IsDead);

        /// <summary>A round resolves once every living player has picked an action. Dead players don't act.</summary>
        public bool IsReadyToResolve => LivingPlayers.All(p => CurrentActions.ContainsKey(p));

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

        /// <summary>
        /// Locks in a player's action for this round. Every action except Focus rolls one d20 (a card with Reroll rolls twice and keeps the higher).
        /// </summary>
        /// <param name="rollD20">Produces one d20 result. Passed in so rounds can be tested with fixed rolls.</param>
        public void SubmitAction(BattleArenaPlayer player, BattleAction action, Func<int> rollD20)
        {
            if (!Started) throw new BadInputException("The game has not started yet.");
            if (player.IsDead) throw new BadInputException("You are out of the game.");
            if (CurrentActions.ContainsKey(player)) throw new BadInputException("You have already picked an action this round.");

            int diceRoll = 0;
            if (action == BattleAction.Focus)
            {
                player.FocusReady = true;
            }
            else
            {
                diceRoll = rollD20();
                if (player.RerollArmed)
                {
                    player.RerollArmed = false; //used up by this roll
                    diceRoll = Math.Max(diceRoll, rollD20());
                }

                if (player.FocusReady && (action == BattleAction.Attack || action == BattleAction.Gamble))
                {
                    diceRoll = Math.Min(FocusCap, diceRoll + BattleActionInfo.FocusBonus);
                    player.FocusReady = false;
                }
            }

            player.HasAttacked = true;
            CurrentActions[player] = new SubmittedAction { Action = action, DiceRoll = diceRoll };
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
                case CardAbility.Reroll:
                    player.RerollArmed = true;
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

        /// <summary>
        /// Works out every action first, then applies all damage together, so the order players acted in doesn't matter.
        /// </summary>
        public List<RoundOutcome> ResolveRound()
        {
            if (!IsReadyToResolve)
                throw new Exception($"Method {nameof(ResolveRound)} cannot be called unless all living players have picked an action. Something went wrong here in game {GameGuid}");

            var outcomes = new List<RoundOutcome>();
            var pendingHits = new List<(RoundOutcome Outcome, BattleArenaPlayer Target, double RawDamage)>();
            var defendMultipliers = new Dictionary<BattleArenaPlayer, double>();

            foreach (var player in GetPlayerOrder())
            {
                if (player.IsDead)
                    continue;

                SubmittedAction submitted = CurrentActions[player];
                var outcome = new RoundOutcome { Player = player, Action = submitted.Action, DiceRoll = submitted.DiceRoll };
                outcomes.Add(outcome);
                var enemies = LivingPlayers.Where(p => p != player).ToList();

                switch (submitted.Action)
                {
                    case BattleAction.Attack:
                        var (attackResult, attackMultiplier, isCrit) = AttackTier(submitted.DiceRoll);
                        outcome.Result = attackResult;
                        if (attackMultiplier == 0.5) //fumble hurts the attacker
                        {
                            pendingHits.Add((outcome, player, BuildDamage(player, player, attackMultiplier, isCrit)));
                        }
                        else
                        {
                            foreach (var enemy in enemies)
                                pendingHits.Add((outcome, enemy, BuildDamage(player, enemy, attackMultiplier, isCrit)));
                        }
                        break;

                    case BattleAction.Defend:
                        var (defendResult, defendMultiplier) = DefendTier(submitted.DiceRoll);
                        outcome.Result = defendResult;
                        defendMultipliers[player] = defendMultiplier;
                        break;

                    case BattleAction.Focus:
                        outcome.Result = $"Focusing. The next Attack or Gamble roll gets +{BattleActionInfo.FocusBonus}.";
                        break;

                    case BattleAction.Gamble:
                        var (gambleResult, gambleMultiplier, gambleCrit, hitsSelf) = GambleTier(submitted.DiceRoll);
                        outcome.Result = gambleResult;
                        if (gambleMultiplier > 0)
                        {
                            var targets = hitsSelf ? new List<BattleArenaPlayer> { player } : enemies;
                            foreach (var target in targets)
                                pendingHits.Add((outcome, target, BuildDamage(player, target, gambleMultiplier, gambleCrit)));
                        }
                        break;
                }
            }

            foreach (var hit in pendingHits)
            {
                double incoming = (defendMultipliers.TryGetValue(hit.Target, out double defend) ? defend : 1) * (hit.Target.ShieldActive ? 0.5 : 1);
                double damage = hit.RawDamage * incoming;

                hit.Target.HitPoints -= damage;
                hit.Outcome.Hits.Add(new Hit { Target = hit.Target, Damage = damage });
                UpdateDeathStatus(hit.Target);
            }

            foreach (var player in Players)
            {
                player.HasAttacked = false;
                player.ShieldActive = false;
                player.CritBoostActive = false;
            }
            CurrentActions.Clear();
            Round++;

            if (IsOver)
            {
                Ended = true;
                Players.ForEach(p => p.IsFinishedPlaying = true); //lets the game end now instead of waiting for the timer
            }

            return outcomes;
        }

        /// <summary>Damage from one attacker to one target, before the target's defend and shield are applied.</summary>
        private static double BuildDamage(BattleArenaPlayer attacker, BattleArenaPlayer target, double multiplier, bool isCrit)
        {
            var attackerCard = attacker.CoinAccount.BattlePerson;
            var targetCard = target.CoinAccount.BattlePerson;

            double damage = attackerCard.Attack * multiplier * ((100 - targetCard.Defense) / 100);
            if (isCrit)
            {
                damage *= 1 + attackerCard.CritMultiplier;
                if (attacker.CritBoostActive)
                    damage *= CritBoostMultiplier;
            }

            return damage;
        }

        private static (string Result, double Multiplier, bool IsCrit) AttackTier(int diceRoll)
        {
            if (diceRoll == 1) return ("Fumbled and hit themselves", 0.5, false);
            if (diceRoll <= 10) return ("Hit", 1.0, false);
            if (diceRoll < 20) return ("Strong hit", 1.5, false);
            return ("Critical hit", 1.0, true);
        }

        /// <summary>Multiplier applied to all damage this player receives this round. 0 means no damage at all.</summary>
        private static (string Result, double Multiplier) DefendTier(int diceRoll)
        {
            if (diceRoll == 1) return ("Stumbled, no block", 1.0);
            if (diceRoll < 20) return ("Blocked half the damage", 0.5);
            return ("Perfect block", 0.0);
        }

        private static (string Result, double Multiplier, bool IsCrit, bool HitsSelf) GambleTier(int diceRoll)
        {
            if (diceRoll <= 5) return ("Backfired and hit themselves", 1.5, false, true);
            if (diceRoll <= 14) return ("Nothing happened", 0, false, false);
            if (diceRoll < 20) return ("Big hit on everyone", 2.0, false, false);
            return ("Jackpot! Critical hit on everyone", 2.0, true, false);
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
    }
}
