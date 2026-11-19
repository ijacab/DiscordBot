using Common.Services;
using DiscordBot;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class AttackHistoryTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Record_KeepsTheNewestFirst_AndNoMoreThanTheLimit()
        {
            var victim = new CoinAccount { UserId = 1, Name = "victim" };
            for (int i = 0; i < AttackHistory.MaxKept + 10; i++)
                AttackHistory.Record(victim, 2, "attacker", $"hit {i}", i, null, Now.AddMinutes(i));
            Assert.Equal(AttackHistory.MaxKept, victim.RecentAttacks.Count);
            Assert.Equal($"hit {AttackHistory.MaxKept + 9}", victim.RecentAttacks[0].Source);
        }

        [Fact]
        public void APowerUpHit_IsLoggedOnTheVictim_WithWhatItCost()
        {
            var victim = new CoinAccount { UserId = 1, Name = "victim", NetWorth = 1_000 };
            var attacker = new CoinAccount { UserId = 2, Name = "attacker" };
            RaffleService.Hit(victim, attacker, 400, Now, source: "Blue Shell");
            var record = Assert.Single(victim.RecentAttacks);
            Assert.Equal("Blue Shell", record.Source);
            Assert.Equal("attacker", record.AttackerName);
            Assert.Equal(400, record.Lost, 2);
            Assert.Empty(attacker.RecentAttacks);
        }

        [Fact]
        public void AMirroredHit_IsLoggedOnBothSides()
        {
            var victim = new CoinAccount { UserId = 1, Name = "victim", NetWorth = 1_000 };
            var attacker = new CoinAccount { UserId = 2, Name = "attacker", NetWorth = 1_000 };
            victim.Start(PowerUpCatalog.Find(PowerUpIds.Mirror), Now);
            RaffleService.Hit(victim, attacker, 300, Now, source: "Red Shell");
            Assert.Equal(0, Assert.Single(victim.RecentAttacks).Lost);
            Assert.Contains("Mirror", victim.RecentAttacks[0].Detail);
            Assert.Equal(300, Assert.Single(attacker.RecentAttacks).Lost, 2);
        }

        [Fact]
        public void Raids_AreLoggedOnTheDefender_WonOrLost()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            var a = new CoinAccount { UserId = 1, Name = "raider", NetWorth = 100_000, BattlePerson = RaidTests.AverageCard(0, DiscordBot.Games.Models.BattleArena.CardAbility.CritBoost) };
            var b = new CoinAccount { UserId = 2, Name = "target", NetWorth = 100_000, BattlePerson = RaidTests.AverageCard(0, DiscordBot.Games.Models.BattleArena.CardAbility.CritBoost) };
            coins.GetAll().Accounts.Add(a);
            coins.GetAll().Accounts.Add(b);

            new RaidService(coins, x => x.NetWorth, new FixedRolls(0.0)).Raid(a, b, 1_000, Now);   //a d100 of 100: the raid works
            Assert.Equal(2_000, b.RecentAttacks[0].Lost, 2);
            Assert.Equal("Raid", b.RecentAttacks[0].Source);

            new RaidService(coins, x => x.NetWorth, new FixedRolls(0.99)).Raid(a, b, 5_000, Now.AddHours(7)); //a d100 of 1: it fails
            Assert.True(b.RecentAttacks[0].Lost < 0);   //the defender came out ahead
            Assert.Contains("you kept", b.RecentAttacks[0].Detail);
            Assert.Equal(2, b.RecentAttacks.Count);
        }

        [Fact]
        public void Describe_ReadsLikeAnAttackLine_AndTheDayTotalAddsUp()
        {
            var victim = new CoinAccount { UserId = 1, Name = "victim" };
            AttackHistory.Record(victim, 2, "Alice", "Blue Shell", 1_200, "$100 of it went on your debt", Now.AddMinutes(-5));
            AttackHistory.Record(victim, 3, "Bob", "Raid", -500, "it failed, you kept their stake", Now.AddHours(-2));
            AttackHistory.Record(victim, 4, "Old", "Raid", 9_999, null, Now.AddDays(-3));

            string line = AttackHistory.Describe(victim.RecentAttacks[2], Now);
            Assert.Contains("5m ago", line);
            Assert.Contains("**Alice**", line);
            Assert.Contains("Blue Shell", line);
            Assert.Contains("$1,200", line);

            var (count, net) = AttackHistory.Last24Hours(victim, Now);
            Assert.Equal(2, count);
            Assert.Equal(700, net, 2);
        }

        private sealed class FixedRolls : Random
        {
            private readonly double _value;
            public FixedRolls(double value) { _value = value; }
            public override double NextDouble() => _value;
        }
    }
}
