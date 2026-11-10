using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Models;
using DiscordBot.Games.Models.BattleArena;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>Face Off rules: one target per hit, a natural 20 is best, whole-number damage, the round cap, and shared wins.</summary>
    public class FaceOffRulesTests
    {
        /// <summary>A fighter with fixed stats: attack 20, defense 0 and crit 0.5, so damage is easy to work out.</summary>
        private static BattleArenaPlayer Fighter(ulong id, double bet = 1000, double hp = BattleArenaPlayer.MaxHitPoints, double attack = 20, double defense = 0, double crit = 0.5)
        {
            return new BattleArenaPlayer
            {
                UserId = id,
                Username = $"P{id}",
                BetAmount = bet,
                HitPoints = hp,
                CoinAccount = new CoinAccount
                {
                    UserId = id,
                    BattlePerson = new BattlePerson { Name = $"Card {id}", Stats = new BattleStats { Attack = attack, Defense = defense, CritMultiplier = crit } }
                }
            };
        }

        private static BattleArena Started(params BattleArenaPlayer[] players)
        {
            var game = new BattleArena { Random = new Random(1) };
            game.Join(players);
            game.Start();
            return game;
        }

        /// <summary>Damage the defender takes from one Attack with this roll, while defending with a 1 (no block, but Defend still counters Attack).</summary>
        private static double DamageFrom(int roll)
        {
            var attacker = Fighter(1);
            var defender = Fighter(2);
            var game = Started(attacker, defender);
            game.SubmitAction(attacker, BattleAction.Attack, () => roll);
            game.SubmitAction(defender, BattleAction.Defend, () => 1);
            game.ResolveRound();
            return BattleArenaPlayer.MaxHitPoints - defender.HitPoints;
        }

        [Fact]
        public void Attack_HitsOnlyTheChosenTarget()
        {
            var attacker = Fighter(1);
            var target = Fighter(2);
            var bystander = Fighter(3);
            var game = Started(attacker, target, bystander);

            game.SubmitAction(attacker, BattleAction.Attack, () => 5, target);
            game.SubmitAction(target, BattleAction.Focus, () => 0);
            game.SubmitAction(bystander, BattleAction.Focus, () => 0);
            RoundOutcome outcome = game.ResolveRound().First(o => o.Player == attacker);

            Assert.Same(target, outcome.Target);
            Assert.True(target.HitPoints < BattleArenaPlayer.MaxHitPoints);
            Assert.Equal(BattleArenaPlayer.MaxHitPoints, bystander.HitPoints);
        }

        [Fact]
        public void Attack_WithoutATarget_HitsOneRandomOpponent()
        {
            var attacker = Fighter(1);
            var a = Fighter(2);
            var b = Fighter(3);
            var game = Started(attacker, a, b);

            game.SubmitAction(attacker, BattleAction.Attack, () => 5);
            game.SubmitAction(a, BattleAction.Focus, () => 0);
            game.SubmitAction(b, BattleAction.Focus, () => 0);
            game.ResolveRound();

            int hurt = new[] { a, b }.Count(p => p.HitPoints < BattleArenaPlayer.MaxHitPoints);
            Assert.Equal(1, hurt);
        }

        [Fact]
        public void Attack_CannotTargetYourselfOrSomeoneOut()
        {
            var attacker = Fighter(1);
            var gone = Fighter(2, hp: 0);
            var game = Started(attacker, gone, Fighter(3));

            Assert.Throws<BadInputException>(() => game.SubmitAction(attacker, BattleAction.Attack, () => 5, attacker));
            Assert.Throws<BadInputException>(() => game.SubmitAction(attacker, BattleAction.Attack, () => 5, gone));
        }

        [Fact]
        public void Defend_And_Focus_HaveNoTarget()
        {
            var a = Fighter(1);
            var b = Fighter(2);
            var game = Started(a, b);

            game.SubmitAction(a, BattleAction.Defend, () => 5, b);
            game.SubmitAction(b, BattleAction.Focus, () => 0, a);
            Assert.All(game.ResolveRound(), o => Assert.Null(o.Target));
        }

        [Fact]
        public void NaturalTwenty_IsTheBestAttack()
        {
            double strong = DamageFrom(19);
            double crit = DamageFrom(20);

            Assert.True(crit > strong, $"a 20 did {crit} and a 19 did {strong}");
            //attack 20 x2 for the natural 20, x1.5 for the card's crit, x0.5 because Defend counters Attack
            Assert.Equal(Math.Round(20 * 2.0 * 1.5 * 0.5), crit);
        }

        [Fact]
        public void Damage_IsAlwaysAWholeNumber()
        {
            var attacker = Fighter(1, attack: 13.37);
            var defender = Fighter(2, defense: 11.1);
            var game = Started(attacker, defender);

            for (int round = 0; round < 3 && !game.IsOver; round++)
            {
                game.SubmitAction(attacker, BattleAction.Attack, () => 7);
                game.SubmitAction(defender, BattleAction.Defend, () => 9);
                foreach (RoundOutcome outcome in game.ResolveRound())
                    Assert.All(outcome.Hits, h => Assert.Equal(Math.Round(h.Damage), h.Damage));
            }
            Assert.Equal(Math.Round(defender.HitPoints), defender.HitPoints);
        }

        [Fact]
        public void AnyRealHit_DoesAtLeastOne()
        {
            var attacker = Fighter(1, attack: 0.2);
            var defender = Fighter(2);
            var game = Started(attacker, defender);

            game.SubmitAction(attacker, BattleAction.Attack, () => 5);
            game.SubmitAction(defender, BattleAction.Focus, () => 0);
            game.ResolveRound();

            Assert.Equal(BattleArenaPlayer.MaxHitPoints - 1, defender.HitPoints);
        }

        [Fact]
        public void RoundCap_EndsTheFight_AndTheHighestHpWins()
        {
            var a = Fighter(1, attack: 1);
            var b = Fighter(2, attack: 1);
            var game = Started(a, b);

            for (int round = 1; round <= BattleArena.MaxRounds; round++)
            {
                Assert.False(game.IsOver);
                game.SubmitAction(a, BattleAction.Attack, () => 5);
                game.SubmitAction(b, round == 1 ? BattleAction.Focus : BattleAction.Defend, () => 20);
                game.ResolveRound();
            }

            Assert.True(game.IsOver);
            Assert.True(game.Ended);
            Assert.Equal(new[] { a }, game.Winners);
            Assert.Equal(2000, game.GetWinnings(a));
            Assert.Equal(0, game.GetWinnings(b));
        }

        [Fact]
        public void TiedOnHpAtTheCap_SplitThePot()
        {
            var a = Fighter(1, bet: 1000);
            var b = Fighter(2, bet: 501);
            var game = Started(a, b);

            for (int round = 1; round <= BattleArena.MaxRounds; round++)
            {
                game.SubmitAction(a, BattleAction.Defend, () => 20);
                game.SubmitAction(b, BattleAction.Defend, () => 20);
                game.ResolveRound();
            }

            Assert.Equal(2, game.Winners.Count);
            Assert.Equal(1501, game.GetWinnings(a) + game.GetWinnings(b), 2);
            Assert.InRange(Math.Abs(game.GetWinnings(a) - game.GetWinnings(b)), 0, 0.011);
        }

        [Fact]
        public void PlayersWhoGoDownTogether_SplitThePot()
        {
            var a = Fighter(1, hp: 5);
            var b = Fighter(2, hp: 5);
            var game = Started(a, b);

            game.SubmitAction(a, BattleAction.Attack, () => 5);
            game.SubmitAction(b, BattleAction.Attack, () => 5);
            game.ResolveRound();

            Assert.True(game.IsOver);
            Assert.Equal(2, game.Winners.Count);
            Assert.Equal(1000, game.GetWinnings(a));
            Assert.Equal(1000, game.GetWinnings(b));
        }

        [Fact]
        public void TheLastOneStanding_TakesTheWholePot()
        {
            var a = Fighter(1, attack: 60);
            var b = Fighter(2, hp: 10);
            var c = Fighter(3, hp: 0);
            var game = Started(a, b, c);

            game.SubmitAction(a, BattleAction.Attack, () => 5, b);
            game.SubmitAction(b, BattleAction.Focus, () => 0);
            game.ResolveRound();

            Assert.Equal(new[] { a }, game.Winners);
            Assert.Equal(3000, game.GetWinnings(a));
        }

        [Fact]
        public void Leave_RemovesALobbyPlayer_ButNotOnceStarted()
        {
            var game = new BattleArena();
            game.Join(Fighter(1), Fighter(2));

            Assert.True(game.Leave(1));
            Assert.Single(game.Players);

            game.Join(Fighter(3));
            game.Start();
            Assert.Throws<BadInputException>(() => game.Leave(2));
        }

        [Fact]
        public void HasPicked_TracksTheRound()
        {
            var a = Fighter(1);
            var b = Fighter(2);
            var game = Started(a, b);

            game.SubmitAction(a, BattleAction.Defend, () => 5);
            Assert.True(game.HasPicked(a));
            Assert.False(game.HasPicked(b));

            game.SubmitAction(b, BattleAction.Defend, () => 5);
            game.ResolveRound();
            Assert.False(game.HasPicked(a));
        }
    }
}
