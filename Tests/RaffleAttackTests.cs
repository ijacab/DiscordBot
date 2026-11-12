using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class RaffleAttackTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>Builds a board of active players with the given net worths (cash only), ranked by net worth.</summary>
        private static (CoinService Coins, RaffleService Raffle) Board(params (ulong Id, double Worth)[] players)
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var accounts = coins.GetAll().Accounts;
            accounts.Clear();
            foreach (var p in players)
                accounts.Add(new CoinAccount { UserId = p.Id, Name = $"player{p.Id}", NetWorth = p.Worth, LastActiveUtcTicks = Now.Ticks });
            var raffle = new RaffleService(coins, new AppSettings { PrestigeDisabled = true }, a => a.NetWorth - a.Debt);
            return (coins, raffle);
        }

        private static CoinAccount Player(CoinService coins, ulong id) => coins.GetAll().Accounts.First(a => a.UserId == id);

        [Fact]
        public void RedShell_HitsOnlyThePlayerDirectlyAbove()
        {
            var (coins, raffle) = Board((1, 1000), (2, 500), (3, 100));
            var user = Player(coins, 3);
            user.Grant(PowerUpIds.RedShell, Now);

            raffle.ApplyUse(user, 1, Now, () => 0.5);

            Assert.Equal(450, Player(coins, 2).NetWorth, 2);
            Assert.Equal(1000, Player(coins, 1).NetWorth, 2);
            Assert.Equal(150, user.NetWorth, 2);
            Assert.Empty(user.HeldPowerUps);
        }

        [Fact]
        public void RedShell_CannotBeUsedFromFirstPlace()
        {
            var (coins, raffle) = Board((1, 1000), (2, 500));
            var leader = Player(coins, 1);
            leader.Grant(PowerUpIds.RedShell, Now);

            Assert.Throws<BadInputException>(() => raffle.ApplyUse(leader, 1, Now, () => 0.5));
            Assert.Single(leader.HeldPowerUps);
        }

        [Fact]
        public void LightningBolt_StrikesTheFiveNearestPlayersAboveYou()
        {
            var (coins, raffle) = Board((1, 700), (2, 600), (3, 500), (4, 400), (5, 300), (6, 200), (7, 100), (8, 50));
            var user = Player(coins, 8);
            user.Grant(PowerUpIds.LightningBolt, Now);

            raffle.ApplyUse(user, 1, Now, () => 0.5);

            // 4% of each of the five nearest above: 20 + 16 + 12 + 8 + 4 = 60
            Assert.Equal(700, Player(coins, 1).NetWorth, 2);
            Assert.Equal(600, Player(coins, 2).NetWorth, 2);
            Assert.Equal(480, Player(coins, 3).NetWorth, 2);
            Assert.Equal(384, Player(coins, 4).NetWorth, 2);
            Assert.Equal(288, Player(coins, 5).NetWorth, 2);
            Assert.Equal(192, Player(coins, 6).NetWorth, 2);
            Assert.Equal(96, Player(coins, 7).NetWorth, 2);
            Assert.Equal(110, user.NetWorth, 2);
        }

        [Fact]
        public void RobinHood_TakesFromTheTopThreeAndSharesWithThePoorestHalf()
        {
            var (coins, raffle) = Board((1, 1000), (2, 800), (3, 600), (4, 400), (5, 200), (6, 100), (7, 50));
            var user = Player(coins, 7);
            user.Grant(PowerUpIds.RobinHood, Now);

            raffle.ApplyUse(user, 1, Now, () => 0.5);

            // 5% of the top three (50 + 40 + 30 = 120), split three ways between the poorest half (you, 6 and 5) at 40 each
            Assert.Equal(950, Player(coins, 1).NetWorth, 2);
            Assert.Equal(760, Player(coins, 2).NetWorth, 2);
            Assert.Equal(570, Player(coins, 3).NetWorth, 2);
            Assert.Equal(400, Player(coins, 4).NetWorth, 2);
            Assert.Equal(240, Player(coins, 5).NetWorth, 2);
            Assert.Equal(140, Player(coins, 6).NetWorth, 2);
            Assert.Equal(90, user.NetWorth, 2);
        }

        [Fact]
        public void HostileTakeover_SeizesHalfTheLeadersCash_WhenThatIsMoreThanAFifthOfTheirWorth()
        {
            var (coins, raffle) = Board((1, 1000), (2, 0));
            var user = Player(coins, 2);
            user.Grant(PowerUpIds.HostileTakeover, Now);

            raffle.ApplyUse(user, 1, Now, () => 0.5);

            var leader = Player(coins, 1);
            Assert.Equal(500, leader.NetWorth, 2);
            Assert.Equal(0, leader.Debt, 2);
            Assert.Equal(500, user.NetWorth, 2);
        }

        [Fact]
        public void HostileTakeover_CannotBeUsedFromFirstPlace()
        {
            var (coins, raffle) = Board((1, 1000), (2, 500));
            var leader = Player(coins, 1);
            leader.Grant(PowerUpIds.HostileTakeover, Now);

            Assert.Throws<BadInputException>(() => raffle.ApplyUse(leader, 1, Now, () => 0.5));
        }

        [Fact]
        public void IsEnteredInNextDraw_TrueWhenActiveWithinTheHourBeforeTheDraw()
        {
            //the next draw is at 12:00
            var account = new CoinAccount { LastActiveUtcTicks = Now.AddMinutes(-50).Ticks };
            Assert.True(RaffleService.IsEnteredInNextDraw(account, Now.AddMinutes(-5)));
        }

        [Fact]
        public void IsEnteredInNextDraw_FalseWhenLastActivityIsTooOld()
        {
            var account = new CoinAccount { LastActiveUtcTicks = Now.AddMinutes(-61).Ticks };
            Assert.False(RaffleService.IsEnteredInNextDraw(account, Now.AddMinutes(-5)));
        }

        [Fact]
        public void IsEnteredInNextDraw_FalseWhenNeverActive()
        {
            Assert.False(RaffleService.IsEnteredInNextDraw(new CoinAccount(), Now));
        }
    }
}
