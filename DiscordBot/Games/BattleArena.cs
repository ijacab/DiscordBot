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
        /// <summary>After this many rounds the fight ends, and the highest HP wins. Ties split the pot.</summary>
        public const int MaxRounds = 12;
        public const int MaxPlayers = 6;

        /// <summary>The action each living player picked this round, with the d20 it rolled and who it's aimed at.</summary>
        internal class SubmittedAction
        {
            public BattleAction Action { get; set; }
            public int DiceRoll { get; set; }
            /// <summary>The opponent an Attack or Gamble hits. Null for Defend and Focus.</summary>
            public BattleArenaPlayer Target { get; set; }
        }

        /// <summary>Picks random targets for players who didn't choose one. Replace it in tests for fixed picks.</summary>
        public Random Random { get; set; } = new Random();

        /// <summary>The round each player went down in, for ranking players who fell together.</summary>
        private readonly Dictionary<BattleArenaPlayer, int> _deathRound = new Dictionary<BattleArenaPlayer, int>();

        internal Dictionary<BattleArenaPlayer, SubmittedAction> CurrentActions { get; } = new Dictionary<BattleArenaPlayer, SubmittedAction>();
        public int Round { get; private set; } = 1;

        public IEnumerable<BattleArenaPlayer> LivingPlayers => Players.Where(p => !p.IsDead);

        /// <summary>A round resolves once every living player has picked an action. Dead players don't act.</summary>
        public bool IsReadyToResolve => LivingPlayers.All(p => CurrentActions.ContainsKey(p));

        /// <summary>The game is over once one player (or nobody) is left alive, or the round cap is reached.</summary>
        public bool IsOver => LivingPlayers.Count() <= 1 || Round > MaxRounds;

        /// <summary>True once the player has locked in an action this round.</summary>
        public bool HasPicked(BattleArenaPlayer player) => CurrentActions.ContainsKey(player);

        /// <summary>
        /// Who takes the pot: the one player left, or at the round cap everyone tied on the highest HP.
        /// If everyone left went down in the same round, they share it. Empty until the game is over.
        /// </summary>
        public List<BattleArenaPlayer> Winners
        {
            get
            {
                if (!IsOver || Players.Count == 0)
                    return new List<BattleArenaPlayer>();
                var living = LivingPlayers.ToList();
                if (living.Count > 0)
                {
                    double best = living.Max(p => p.HitPoints);
                    return living.Where(p => p.HitPoints == best).ToList();
                }
                int lastRound = _deathRound.Count == 0 ? 0 : _deathRound.Values.Max();
                var lastOut = Players.Where(p => _deathRound.TryGetValue(p, out int r) && r == lastRound).ToList();
                return lastOut.Count > 0 ? lastOut : PlayerRanking.Take(1).ToList();
            }
        }

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

        /// <summary>The winner takes every bet placed in the game. Tied winners split it evenly.</summary>
        public override double GetWinnings(BattleArenaPlayer player)
        {
            int playerCount = Players.Count();
            if (playerCount < MinimumRequiredPlayers) throw new Exception($"Something went wrong here. The game started with {playerCount} players - less than the minimum number of players: {MinimumRequiredPlayers}");

            double pot = Players.Sum(p => p.BetAmount);
            var winners = IsOver ? Winners : PlayerRanking.Take(1).ToList();
            int index = winners.IndexOf(player);
            if (index < 0)
                return 0;
            double share = Math.Floor(pot / winners.Count * 100) / 100;
            return index == 0 ? Math.Round(pot - share * (winners.Count - 1), 2) : share;
        }

        /// <summary>Removes a player from a lobby that hasn't started. Returns false if they weren't in it.</summary>
        public bool Leave(ulong userId)
        {
            if (Started)
                throw new BadInputException("The fight has started, so you can't leave.");
            return Players.RemoveAll(p => p.UserId == userId) > 0;
        }

        public IList<BattleArenaPlayer> GetPlayerOrder()
        {
            var randomOrderList = new List<BattleArenaPlayer>(Players);
            randomOrderList.Shuffle();

            return randomOrderList;
        }

        /// <summary>
        /// Locks in a player's action for this round. Every action except Focus rolls one d20 (a card with Reroll rolls twice and keeps the higher).
        /// Attack and Gamble hit one opponent: the target given, or a random living opponent if none is.
        /// </summary>
        /// <param name="rollD20">Produces one d20 result. Passed in so rounds can be tested with fixed rolls.</param>
        public void SubmitAction(BattleArenaPlayer player, BattleAction action, Func<int> rollD20, BattleArenaPlayer target = null)
        {
            if (!Started) throw new BadInputException("The game has not started yet.");
            if (Ended) throw new BadInputException("The game is over.");
            if (player.IsDead) throw new BadInputException("You are out of the game.");
            if (CurrentActions.ContainsKey(player)) throw new BadInputException("You have already picked an action this round.");

            if (action == BattleAction.Attack || action == BattleAction.Gamble)
            {
                var opponents = LivingPlayers.Where(p => p != player).ToList();
                if (target != null && (target == player || target.IsDead || !Players.Contains(target)))
                    throw new BadInputException("Pick an opponent who is still in the fight.");
                target ??= opponents.Count == 0 ? null : opponents[Random.Next(opponents.Count)];
            }
            else
            {
                target = null;
            }

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
            CurrentActions[player] = new SubmittedAction { Action = action, DiceRoll = diceRoll, Target = target };
        }

        /// <summary>Uses the player's card ability. Each player can use their ability once per game.</summary>
        /// <returns>What the ability did.</returns>
        public string UseAbility(BattleArenaPlayer player)
        {
            if (!Started) throw new BadInputException("The game has not started yet.");
            if (player.IsDead) throw new BadInputException("You are out of the game.");
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
            var pendingHits = new List<(RoundOutcome Outcome, BattleArenaPlayer Target, double RawDamage, string Note)>();
            var defendMultipliers = new Dictionary<BattleArenaPlayer, double>();

            foreach (var player in GetPlayerOrder())
            {
                if (player.IsDead)
                    continue;

                SubmittedAction submitted = CurrentActions[player];
                var outcome = new RoundOutcome { Player = player, Action = submitted.Action, DiceRoll = submitted.DiceRoll, Target = submitted.Target };
                outcomes.Add(outcome);
                var enemies = submitted.Target == null ? new List<BattleArenaPlayer>() : new List<BattleArenaPlayer> { submitted.Target };

                switch (submitted.Action)
                {
                    case BattleAction.Attack:
                        var (attackResult, attackMultiplier, isCrit) = AttackTier(submitted.DiceRoll);
                        outcome.Result = attackResult;
                        if (attackMultiplier == 0.5) //fumble hurts the attacker
                        {
                            pendingHits.Add(MatchupHit(outcome, player, player, submitted.Action, attackMultiplier, isCrit));
                        }
                        else
                        {
                            foreach (var enemy in enemies)
                                pendingHits.Add(MatchupHit(outcome, player, enemy, submitted.Action, attackMultiplier, isCrit));
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
                                pendingHits.Add(MatchupHit(outcome, player, target, submitted.Action, gambleMultiplier, gambleCrit));
                        }
                        break;
                }
            }

            int round = Round;
            foreach (var hit in pendingHits)
            {
                double incoming = (defendMultipliers.TryGetValue(hit.Target, out double defend) ? defend : 1) * (hit.Target.ShieldActive ? 0.5 : 1);
                double exact = hit.RawDamage * incoming;
                //whole numbers keep HP readable; any real hit does at least 1
                double damage = exact > 0 ? Math.Max(1, Math.Round(exact)) : 0;

                hit.Target.HitPoints -= damage;
                if (hit.Target.IsDead && !_deathRound.ContainsKey(hit.Target))
                    _deathRound[hit.Target] = round;
                hit.Outcome.Hits.Add(new Hit { Target = hit.Target, Damage = damage, Note = hit.Note });
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

        /// <summary>
        /// Counter loop: every action beats exactly one other action (Attack beats Focus, Focus beats Gamble,
        /// Gamble beats Defend, Defend beats Attack). Picks are hidden until the round resolves. A hit on a target whose pick it beats does 1.5x,
        /// and a hit on a target whose pick beats it does 0.5x. Hitting yourself is never countered.
        /// </summary>
        public static double MatchupMultiplier(BattleAction attackerAction, BattleAction targetAction)
        {
            if (Beats(attackerAction, targetAction)) return 1.5;
            if (Beats(targetAction, attackerAction)) return 0.5;
            return 1;
        }

        public static bool Beats(BattleAction action, BattleAction other)
        {
            return (action == BattleAction.Attack && other == BattleAction.Focus)
                || (action == BattleAction.Focus && other == BattleAction.Gamble)
                || (action == BattleAction.Gamble && other == BattleAction.Defend)
                || (action == BattleAction.Defend && other == BattleAction.Attack);
        }

        private (RoundOutcome Outcome, BattleArenaPlayer Target, double RawDamage, string Note) MatchupHit(
            RoundOutcome outcome, BattleArenaPlayer attacker, BattleArenaPlayer target, BattleAction attackerAction, double multiplier, bool isCrit)
        {
            if (target == attacker)
                return (outcome, target, BuildDamage(attacker, target, multiplier, isCrit), null);

            BattleAction targetAction = CurrentActions[target].Action;
            double matchup = MatchupMultiplier(attackerAction, targetAction);
            string note = matchup > 1 ? $"{attackerAction} beats {targetAction}"
                        : matchup < 1 ? $"{attackerAction} was countered by {targetAction}"
                        : null;

            return (outcome, target, BuildDamage(attacker, target, multiplier, isCrit) * matchup, note);
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

        /// <summary>A natural 20 is always the best result: double damage, then the card's crit on top.</summary>
        private static (string Result, double Multiplier, bool IsCrit) AttackTier(int diceRoll)
        {
            if (diceRoll == 1) return ("Fumbled and hit themselves", 0.5, false);
            if (diceRoll <= 10) return ("Hit", 1.0, false);
            if (diceRoll < 20) return ("Strong hit", 1.5, false);
            return ("Critical hit", 2.0, true);
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
            if (diceRoll < 20) return ("Big hit", 2.0, false, false);
            return ("Jackpot! Critical hit", 2.5, true, false);
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
