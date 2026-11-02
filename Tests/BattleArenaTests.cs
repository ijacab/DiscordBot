using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Models;
using DiscordBot.Games.Models.BattleArena;
using Newtonsoft.Json;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class BattleArenaTests
    {
        private static BattleArenaPlayer Player(ulong id, double bet, double hitPoints = BattleArenaPlayer.MaxHitPoints, CardAbility ability = CardAbility.None)
        {
            return new BattleArenaPlayer
            {
                UserId = id,
                BetAmount = bet,
                HitPoints = hitPoints,
                CoinAccount = new CoinAccount { UserId = id, BattlePerson = new BattlePerson("Test") { Ability = ability } }
            };
        }

        private static BattleArena GameWith(params BattleArenaPlayer[] players)
        {
            var game = new BattleArena();
            game.Join(players);
            return game;
        }

        [Fact]
        public void BattlePerson_StatsSurviveJsonRoundTrip()
        {
            var card = new BattlePerson("Test Person") { Level = 3, Wins = 2, Losses = 1 };

            var loaded = JsonConvert.DeserializeObject<BattlePerson>(JsonConvert.SerializeObject(card));

            Assert.Equal(card.Name, loaded.Name);
            Assert.Equal(card.Rarity, loaded.Rarity);
            Assert.Equal(card.Ability, loaded.Ability);
            Assert.Equal(card.Level, loaded.Level);
            Assert.Equal(card.Wins, loaded.Wins);
            Assert.Equal(card.Losses, loaded.Losses);
            Assert.Equal(card.Attack, loaded.Attack);
            Assert.Equal(card.Defense, loaded.Defense);
            Assert.Equal(card.CritMultiplier, loaded.CritMultiplier);
        }

        [Fact]
        public void Winner_TakesTheWholePot_WithThreePlayers()
        {
            var winner = Player(1, 1000, hitPoints: 40);
            var loserA = Player(2, 1000, hitPoints: 0);
            var loserB = Player(3, 1000, hitPoints: 0);
            var game = GameWith(winner, loserA, loserB);
            game.DeathOrderList.Add(loserA);
            game.DeathOrderList.Add(loserB);

            Assert.Equal(3000d, game.GetWinnings(winner));
            Assert.Equal(0d, game.GetWinnings(loserA));
            Assert.Equal(0d, game.GetWinnings(loserB));
        }

        [Fact]
        public void Ranking_PlayerWhoDiedLaterRanksAboveEarlierDeath()
        {
            var firstOut = Player(1, 100, hitPoints: 0);
            var lastOut = Player(2, 100, hitPoints: 0);
            var game = GameWith(firstOut, lastOut);
            game.DeathOrderList.Add(firstOut);
            game.DeathOrderList.Add(lastOut);

            Assert.Same(lastOut, game.PlayerRanking.First());
        }

        [Fact]
        public void Game_IsOver_OnlyWhenOneOrNoPlayersAreLeft()
        {
            Assert.False(GameWith(Player(1, 100), Player(2, 100)).IsOver);
            Assert.True(GameWith(Player(1, 100), Player(2, 100, hitPoints: 0)).IsOver);
        }

        [Fact]
        public void Heal_RestoresHitPoints_UpToMaximum()
        {
            var player = Player(1, 100, hitPoints: 90, ability: CardAbility.Heal);
            var game = GameWith(player, Player(2, 100));
            game.Start();

            game.UseAbility(player);

            Assert.Equal(BattleArenaPlayer.MaxHitPoints, player.HitPoints);
        }

        [Fact]
        public void Ability_CanOnlyBeUsedOncePerGame()
        {
            var player = Player(1, 100, hitPoints: 50, ability: CardAbility.Heal);
            var game = GameWith(player, Player(2, 100));
            game.Start();
            game.UseAbility(player);

            Assert.Throws<BadInputException>(() => game.UseAbility(player));
        }

        [Fact]
        public void Ability_CannotBeUsedWithoutAnAbility()
        {
            var player = Player(1, 100, ability: CardAbility.None);
            var game = GameWith(player, Player(2, 100));
            game.Start();

            Assert.Throws<BadInputException>(() => game.UseAbility(player));
        }
    }
}
