using Common.Services;
using DiscordBot;
using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>75% of lost bets go into the jackpot, and each player gets 10 jackpot rolls an hour.</summary>
    public class JackpotLossTests
    {
        private static (CoinService Coins, BetManager Bets, CoinAccount Account) Setup(double cash = 10_000, double pool = 0)
        {
            var settings = new AppSettings { PrestigeDisabled = true };
            var coins = new CoinService(new LocalFileService(), settings);
            coins.GetAll().Accounts.Clear();
            coins.GetAll().JackpotPool = pool;
            var account = new CoinAccount { UserId = 77, Name = "gambler", NetWorth = cash };
            coins.GetAll().Accounts.Add(account);
            var bets = new BetManager(coins, new StockService(new LocalFileService(), coins), settings);
            return (coins, bets, account);
        }

        private static async Task Play(BetManager bets, CoinAccount account, double bet, double baseWinnings, bool feedJackpot = true)
        {
            await bets.InitiateBet(account, bet);
            await bets.ResolveBet(account.UserId, account.Name, bet, baseWinnings, updateRemote: false, feedJackpot: feedJackpot);
        }

        [Fact]
        public async Task LostBet_PutsThreeQuartersInTheJackpot()
        {
            var (coins, bets, account) = Setup();
            await Play(bets, account, 1_000, 0);
            Assert.Equal(750, coins.JackpotPool, 2);
        }

        [Fact]
        public async Task PartialPayout_OnlyCountsTheShortfall()
        {
            var (coins, bets, account) = Setup();
            await Play(bets, account, 1_000, 400);
            Assert.Equal(450, coins.JackpotPool, 2); //600 lost
        }

        [Fact]
        public async Task WinsAndBetsBack_PutNothingIn()
        {
            var (coins, bets, account) = Setup();
            await Play(bets, account, 1_000, 2_000);
            await Play(bets, account, 1_000, 1_000);
            Assert.Equal(0, coins.JackpotPool, 2);
        }

        [Fact]
        public async Task FaceOff_StyleBets_DoNotFeedTheJackpot()
        {
            var (coins, bets, account) = Setup();
            await Play(bets, account, 1_000, 0, feedJackpot: false);
            Assert.Equal(0, coins.JackpotPool, 2);
        }

        [Fact]
        public async Task SafetyNetRefund_ShrinksWhatIsLost()
        {
            var (coins, bets, account) = Setup();
            account.Start(PowerUpCatalog.Find(PowerUpIds.SafetyNet), DateTime.UtcNow);
            await Play(bets, account, 1_000, 0); //75% comes back, so 250 is lost
            Assert.Equal(187.5, coins.JackpotPool, 2);
        }

        [Theory]
        [InlineData(1_000, 750)]
        [InlineData(0.99, 0.74)] //whole cents, rounded down
        [InlineData(0, 0)]
        [InlineData(-5, 0)]
        public void ShareOfLoss_IsThreeQuartersInWholeCents(double lost, double expected)
        {
            Assert.Equal(expected, BetManager.JackpotShareOfLoss(lost), 4);
        }

        [Fact]
        public void JackpotRolls_AreTenAnHourAndResetEachHour()
        {
            var account = new CoinAccount { UserId = 1 };
            var noon = new DateTime(2026, 11, 15, 12, 5, 0, DateTimeKind.Utc);

            for (int i = 0; i < 10; i++)
                Assert.True(BetManager.TryUseJackpotRoll(account, noon.AddMinutes(i * 5)), $"roll {i + 1}");
            Assert.False(BetManager.TryUseJackpotRoll(account, noon.AddMinutes(50)));
            Assert.True(BetManager.TryUseJackpotRoll(account, noon.AddHours(1)));
            Assert.Equal(10, BetManager.JackpotRollsPerHour);
        }

        [Theory]
        [InlineData(10_000, 10_000, true)]   //up to $10,000: only all your cash
        [InlineData(9_000, 10_000, false)]
        [InlineData(5_000, 5_000, true)]
        [InlineData(5_000, 6_000, false)]
        [InlineData(10_000, 20_000, false)]  //exactly $10,000 still counts as a small bet
        [InlineData(10_001, 20_000, true)]   //over $10,000: half your cash or more
        [InlineData(10_001, 30_000, false)]
        [InlineData(15_000, 30_000, true)]
        [InlineData(20_000, 30_000, true)]
        [InlineData(14_000, 30_000, false)]
        [InlineData(11_000, 100_000, false)]
        public void WhichBetsCanRollForTheJackpot(double bet, double cash, bool qualifies) =>
            Assert.Equal(qualifies, BetManager.QualifiesForJackpot(bet, cash));

        [Fact]
        public async Task ABigBetOfHalfYourCash_RollsTheJackpotWithoutGoingAllIn()
        {
            var (coins, bets, account) = Setup(cash: 40_000, pool: 1_000);
            bets.NextJackpotRoll = () => 0.0; //a roll wins

            await Play(bets, account, 20_000, 20_000); //half the cash, bet back
            Assert.Contains("JACKPOT", bets.TakeJackpotWin(account.UserId));
        }

        [Fact]
        public async Task ABigBetUnderHalfYourCash_DoesNotRoll()
        {
            var (coins, bets, account) = Setup(cash: 40_000, pool: 1_000);
            bets.NextJackpotRoll = () => 0.0;

            await Play(bets, account, 15_000, 15_000);
            Assert.Equal(0, account.JackpotRollsThisHour);
            Assert.Null(bets.TakeJackpotWin(account.UserId));
        }

        [Fact]
        public async Task QualifyingBets_GetTenRollsAnHour_ThenAreToldTheyAreOut()
        {
            var (coins, bets, account) = Setup(cash: 10_000, pool: 1_000);
            bets.NextJackpotRoll = () => 1.0; //no roll wins, so the cash stays and the rolls are counted

            for (int i = 0; i < BetManager.JackpotRollsPerHour; i++)
            {
                await Play(bets, account, account.NetWorth, account.NetWorth); //all-in, bet back
                Assert.Null(bets.TakeJackpotWin(account.UserId));
            }
            Assert.Equal(BetManager.JackpotRollsPerHour, account.JackpotRollsThisHour);

            await Play(bets, account, account.NetWorth, account.NetWorth);
            Assert.Contains("No jackpot rolls left", bets.TakeJackpotWin(account.UserId));

            bets.NextJackpotRoll = () => 0.0;
            account.JackpotRollsThisHour = 0;
            await Play(bets, account, account.NetWorth, 0);
            Assert.Contains("JACKPOT", bets.TakeJackpotWin(account.UserId));
        }

        [Fact]
        public async Task SmallerBets_DoNotSpendJackpotRolls()
        {
            var (coins, bets, account) = Setup(cash: 10_000, pool: 1_000);
            bets.NextJackpotRoll = () => 0.0;

            for (int i = 0; i < 4; i++)
                await Play(bets, account, 100, 100); //bets back, never all-in

            Assert.Equal(0, account.JackpotRollsThisHour);
            Assert.Null(bets.TakeJackpotWin(account.UserId));
        }

        [Fact]
        public async Task EmptyJackpot_DoesNotSpendARoll()
        {
            var (coins, bets, account) = Setup(cash: 10_000, pool: 0);
            bets.NextJackpotRoll = () => 0.0;
            await Play(bets, account, account.NetWorth, 0);
            Assert.Equal(0, account.JackpotRollsThisHour);
        }

        private static DungeonSession Lobby(int seed) => new DungeonSession(1, "Host", 1_000, new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc), new Random(seed));

        private static DateTime TickUntil(DungeonSession session, DateTime now, Func<bool> done)
        {
            for (int i = 0; i < 40 && !done(); i++)
            {
                now = now.AddSeconds(1);
                session.Tick(now);
            }
            return now;
        }

        [Fact]
        public void Dungeon_AGoneDownRunForfeitsItsBuyIn_AndACashOutOnlyTheShortfall()
        {
            bool sawDeath = false, sawCashOut = false;
            for (int seed = 1; seed < 300 && !(sawDeath && sawCashOut); seed++)
            {
                DungeonSession session = Lobby(seed);
                DateTime now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
                Assert.Null(session.Start(1, now));
                session.Go(1, now);
                while (!session.Ended && session.RunOf(1)?.Phase != RunPhase.AtCheckpoint)
                {
                    now = TickUntil(session, now, () => session.Ended || session.RunOf(1)?.Phase != RunPhase.Crawling);
                    if (session.RunOf(1)?.Phase == RunPhase.AtBossDoor)
                        session.Boss(1, BossChoice.Fight, now);
                }

                if (session.Ended)
                {
                    var forfeit = session.TakeForfeits().Single();
                    Assert.Equal(1_000, forfeit.Amount);
                    Assert.Empty(session.TakeForfeits()); //taken once
                    sawDeath = true;
                }
                else
                {
                    double haul = session.RunOf(1).Haul;
                    session.TakePayouts();
                    Assert.Empty(session.TakeForfeits());
                    Assert.Null(session.Cash(1, now));
                    double expected = Math.Max(0, Math.Round(1_000 - haul, 2));
                    Assert.Equal(expected, session.TakeForfeits().Sum(f => f.Amount), 2);
                    sawCashOut = true;
                }
            }
            Assert.True(sawDeath && sawCashOut);
        }
    }
}
