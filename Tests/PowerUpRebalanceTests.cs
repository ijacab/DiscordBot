using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>The rubber-banded raffle, the catch-up items and the creative power-ups.</summary>
    public class PowerUpRebalanceTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

        private sealed class World
        {
            public CoinService Coins;
            public RaffleService Raffle;
            /// <summary>Worth held outside cash (stand-in for stocks and property), by user id.</summary>
            public readonly Dictionary<ulong, double> Invested = new Dictionary<ulong, double>();
            public CoinAccounts Data => Coins.GetAll();
            public CoinAccount this[ulong id] => Data.Accounts.First(a => a.UserId == id);
        }

        /// <summary>Active players with the given cash, ranked by worth (cash plus anything in <see cref="World.Invested"/>, less debt).</summary>
        private static World Board(params (ulong Id, double Cash)[] players)
        {
            var world = new World();
            world.Coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var data = world.Coins.GetAll();
            data.Accounts.Clear();
            data.JackpotPool = 0;
            data.LastRaffleSlot = 0;
            data.LastUsedPowerUpId = null;
            data.PropertyIndex = 1.0;
            foreach (var p in players)
                data.Accounts.Add(new CoinAccount { UserId = p.Id, Name = $"player{p.Id}", NetWorth = p.Cash, LastActiveUtcTicks = Now.Ticks });
            world.Raffle = new RaffleService(world.Coins, new AppSettings { PrestigeDisabled = true },
                a => a.NetWorth + (world.Invested.TryGetValue(a.UserId, out double extra) ? extra : 0) - a.Debt);
            return world;
        }

        private static string Use(World w, ulong userId, string powerUpId, Func<double> random = null)
        {
            var user = w[userId];
            user.Grant(powerUpId, Now);
            return w.Raffle.ApplyUse(user, user.HeldPowerUps.Count, Now, random ?? (() => 0.5));
        }

        private static void Running(CoinAccount account, string id) => account.Start(PowerUpCatalog.Find(id), Now);

        // ---------- rubber-banding ----------

        [Fact]
        public void Pick_TopTierOnlyEverDrawsBoosts()
        {
            var context = new PickContext { IsTopTier = true, Runaway = true };
            for (double roll = 0; roll < 1; roll += 0.001)
                Assert.Equal(PowerUpCategory.Boost, PowerUpCatalog.Pick(roll, context).Category);
        }

        [Fact]
        public void Pick_LowerPlayersDrawCatchUpAndAttacksMoreOften()
        {
            var catchUp = PowerUpCatalog.Find(PowerUpIds.TaxTheRich);
            var attack = PowerUpCatalog.Find(PowerUpIds.BlueShell);
            var boost = PowerUpCatalog.Find(PowerUpIds.Stimulus);
            var middle = new PickContext { Percentile = 0.5 };
            var bottom = new PickContext { Percentile = 1 };

            Assert.True(PowerUpCatalog.WeightFor(catchUp, bottom) > PowerUpCatalog.WeightFor(catchUp, middle));
            Assert.True(PowerUpCatalog.WeightFor(attack, bottom) > PowerUpCatalog.WeightFor(attack, middle));
            Assert.Equal(PowerUpCatalog.WeightFor(boost, bottom), PowerUpCatalog.WeightFor(boost, middle));
        }

        [Fact]
        public void Pick_RunawayLeaderTriplesCatchUp()
        {
            var catchUp = PowerUpCatalog.Find(PowerUpIds.Audit);
            var normal = new PickContext { Percentile = 1 };
            var runaway = new PickContext { Percentile = 1, Runaway = true };
            Assert.Equal(3 * PowerUpCatalog.WeightFor(catchUp, normal), PowerUpCatalog.WeightFor(catchUp, runaway), 6);
        }

        [Fact]
        public void Pick_WithNoContextKeepsThePlainWeights()
        {
            Assert.All(PowerUpCatalog.All, p => Assert.Equal(p.Weight, PowerUpCatalog.WeightFor(p, null)));
            Assert.All(PowerUpCatalog.Debuffs, d => Assert.DoesNotContain(PowerUpCatalog.All, p => p.Id == d.Id));
        }

        [Fact]
        public void ContextFor_SpotsTheTopTierAndARunawayLeader()
        {
            var w = Board((1, 100_000), (2, 1_000), (3, 900), (4, 800), (5, 700), (6, 600));
            var active = w.Data.Accounts;

            var leader = w.Raffle.ContextFor(w[1], active);
            var last = w.Raffle.ContextFor(w[6], active);

            Assert.True(leader.IsTopTier);
            Assert.False(last.IsTopTier);
            Assert.Equal(1, last.Percentile, 6);
            Assert.True(last.Runaway);

            var close = Board((1, 1_000), (2, 900), (3, 800), (4, 700));
            Assert.False(close.Raffle.ContextFor(close[4], close.Data.Accounts).Runaway);
        }

        [Fact]
        public void TopTier_IsThreeButNeverMoreThanHalfTheBoard()
        {
            Assert.Equal(1, PowerUpCatalog.TopTier(2));
            Assert.Equal(2, PowerUpCatalog.TopTier(4));
            Assert.Equal(3, PowerUpCatalog.TopTier(10));
        }

        [Fact]
        public void Announcement_IsOneLineAndTagsCatchUp()
        {
            var w = Board((1, 1_000));
            string line = RaffleService.Announcement(w[1], PowerUpCatalog.Find(PowerUpIds.TaxTheRich), 4);
            Assert.DoesNotContain("\n", line);
            Assert.Contains("catch-up", line);
            Assert.Contains("Tax the Rich", line);
        }

        // ---------- catch-up ----------

        [Fact]
        public void TaxTheRich_TopThreePayTenPercentAndEveryoneElseShares()
        {
            var w = Board((1, 1000), (2, 800), (3, 600), (4, 400), (5, 200), (6, 100));

            Use(w, 6, PowerUpIds.TaxTheRich);

            // 100 + 80 + 60 = 240, shared by players 4, 5 and 6 at 80 each
            Assert.Equal(900, w[1].NetWorth, 2);
            Assert.Equal(720, w[2].NetWorth, 2);
            Assert.Equal(540, w[3].NetWorth, 2);
            Assert.Equal(480, w[4].NetWorth, 2);
            Assert.Equal(280, w[5].NetWorth, 2);
            Assert.Equal(180, w[6].NetWorth, 2);
        }

        [Fact]
        public void TaxTheRich_APayerWithNoCashTakesDebt()
        {
            var w = Board((1, 0), (2, 800), (3, 600), (4, 400), (5, 200), (6, 100));
            w.Invested[1] = 10_000; //all in stocks and property

            Use(w, 6, PowerUpIds.TaxTheRich);

            Assert.Equal(0, w[1].NetWorth, 2);
            Assert.Equal(1_000, w[1].Debt, 2);
        }

        [Fact]
        public void TaxTheRich_IgnoresShieldsAndMirrors()
        {
            var w = Board((1, 1000), (2, 800), (3, 600), (4, 400), (5, 200), (6, 100));
            Running(w[1], PowerUpIds.Shield);
            Running(w[1], PowerUpIds.Mirror);

            Use(w, 6, PowerUpIds.TaxTheRich);

            Assert.Equal(900, w[1].NetWorth, 2);
            Assert.True(w[1].IsActive(PowerUpIds.Mirror, Now)); //not used up
        }

        [Fact]
        public void CatchUp_IsRefusedForTheTopTier_AndKept()
        {
            var w = Board((1, 1000), (2, 800), (3, 600), (4, 400), (5, 200), (6, 100));
            Assert.Throws<BadInputException>(() => Use(w, 2, PowerUpIds.TaxTheRich));
            Assert.Single(w[2].HeldPowerUps);
            Assert.Throws<BadInputException>(() => Use(w, 3, PowerUpIds.Audit));
        }

        [Fact]
        public void Audit_StopsAndSkimsTheLeaderForSixHours()
        {
            var w = Board((1, 1000), (2, 100), (3, 50), (4, 10));

            Use(w, 4, PowerUpIds.Audit);

            Assert.True(w[1].IsActive(PowerUpIds.Audited, Now.AddHours(5.9)));
            double skimmed = PowerUps.SettleWin(w.Data, w[1], 1_000, Now);
            Assert.Equal(500, skimmed, 2);
            Assert.Equal(500, w.Data.JackpotPool, 2);
            Assert.False(w[1].IsActive(PowerUpIds.Audited, Now.AddHours(6.1)));
        }

        [Fact]
        public void Bailout_LeaderPaysYourDebtUpToTheCapPlusABonus()
        {
            var w = Board((1, 1000), (2, 500), (3, 300), (4, 0));
            w[4].Debt = 300;

            Use(w, 4, PowerUpIds.Bailout);

            // pays 200 of the debt (20% of 1,000) and a 50 bonus (5%)
            Assert.Equal(750, w[1].NetWorth, 2);
            Assert.Equal(100, w[4].Debt, 2);
            Assert.Equal(50, w[4].NetWorth, 2);
        }

        [Fact]
        public void Bailout_ALeaderShortOnCashTakesDebt()
        {
            var w = Board((1, 0), (2, 500), (3, 300), (4, 0));
            w.Invested[1] = 1_000;
            w[4].Debt = 300;

            Use(w, 4, PowerUpIds.Bailout);

            Assert.Equal(250, w[1].Debt, 2);
            Assert.Equal(100, w[4].Debt, 2);
        }

        [Fact]
        public void Squatters_TakeTheLeadersRent()
        {
            var w = Board((1, 1000), (2, 500), (3, 300), (4, 0));
            w[1].Properties.Add(new PropertyHolding { Kind = PropertyKind.House });

            Use(w, 4, PowerUpIds.Squatters);
            PropertyService.PayRentOneHour(w.Data, Now.AddHours(1));

            double rent = PropertyCatalog.GrossRentPerHour(PropertyKind.House, 1.0) * (1 - PropertyCatalog.AgencyFeeRate);
            Assert.Equal(1000, w[1].NetWorth, 2);
            Assert.Equal(rent, w[4].NetWorth, 2);

            PropertyService.PayRentOneHour(w.Data, Now.AddHours(13)); //the squat is over
            Assert.Equal(1000 + rent, w[1].NetWorth, 2);
        }

        [Fact]
        public void Squatters_AreRefusedWhenTheLeaderOwnsNoProperty()
        {
            var w = Board((1, 1000), (2, 500), (3, 300), (4, 0));
            Assert.Throws<BadInputException>(() => Use(w, 4, PowerUpIds.Squatters));
        }

        [Fact]
        public void SettleWin_LeechAndTollBoothsTakeTheirShare()
        {
            var w = Board((1, 1000), (2, 500), (3, 300), (4, 0));
            Use(w, 4, PowerUpIds.Leech);
            Running(w[2], PowerUpIds.TollBooth);
            Running(w[1], PowerUpIds.TollBooth); //your own toll booth doesn't charge you

            double skimmed = PowerUps.SettleWin(w.Data, w[1], 1_000, Now);

            Assert.Equal(150 + 30, skimmed, 2);
            Assert.Equal(150, w[4].NetWorth, 2);
            Assert.Equal(530, w[2].NetWorth, 2);
            Assert.Equal(0, PowerUps.SettleWin(w.Data, w[1], -500, Now));
        }

        // ---------- attacks and their counters ----------

        [Fact]
        public void Mirror_BouncesAnAttackBack()
        {
            var w = Board((1, 1000), (2, 500));
            Running(w[1], PowerUpIds.Mirror);

            Use(w, 2, PowerUpIds.BlueShell);

            Assert.Equal(1200, w[1].NetWorth, 2);
            Assert.Equal(300, w[2].NetWorth, 2);
            Assert.False(w[1].IsActive(PowerUpIds.Mirror, Now));
        }

        [Fact]
        public void GoldenParachute_CancelsTheDebtPart()
        {
            var w = Board((1, 100), (2, 50));
            w.Invested[1] = 900; //worth 1,000, so a Blue Shell wants 200 but they only have 100 in cash
            Running(w[1], PowerUpIds.GoldenParachute);

            Use(w, 2, PowerUpIds.BlueShell);

            Assert.Equal(0, w[1].NetWorth, 2);
            Assert.Equal(0, w[1].Debt, 2);
            Assert.Equal(150, w[2].NetWorth, 2);
        }

        [Fact]
        public void HostileTakeover_TakesAFifthOfWorthWithDebtWhenCashIsShort()
        {
            var w = Board((1, 100), (2, 50));
            w.Invested[1] = 900;

            Use(w, 2, PowerUpIds.HostileTakeover);

            Assert.Equal(0, w[1].NetWorth, 2);
            Assert.Equal(100, w[1].Debt, 2);
            Assert.Equal(250, w[2].NetWorth, 2);
        }

        [Fact]
        public void Heist_HeadsTakesThirtyPercent()
        {
            var w = Board((1, 1000), (2, 0));
            Use(w, 2, PowerUpIds.Heist, () => 0.1);
            Assert.Equal(700, w[1].NetWorth, 2);
            Assert.Equal(300, w[2].NetWorth, 2);
        }

        [Fact]
        public void Heist_TailsYouPayAndGoIntoDebtIfShort()
        {
            var w = Board((1, 1000), (2, 0));
            Use(w, 2, PowerUpIds.Heist, () => 0.9);
            Assert.Equal(2000, w[1].NetWorth, 2);
            Assert.Equal(PowerUpCatalog.HeistLoseMin, w[2].Debt, 2);
        }

        [Fact]
        public void MusicalChairs_SwapsCash()
        {
            var w = Board((1, 1000), (2, 10));
            Use(w, 2, PowerUpIds.MusicalChairs);
            Assert.Equal(10, w[1].NetWorth, 2);
            Assert.Equal(1000, w[2].NetWorth, 2);
        }

        [Fact]
        public void StickyFingers_StealsAHeldPowerUpFromThePlayerAbove()
        {
            var w = Board((1, 1000), (2, 10));
            w[1].Grant(PowerUpIds.GoldenTicket, Now);

            Use(w, 2, PowerUpIds.StickyFingers);

            Assert.Empty(w[1].HeldPowerUps);
            Assert.Equal(PowerUpIds.GoldenTicket, Assert.Single(w[2].HeldPowerUps).Id);
        }

        [Fact]
        public void FreezeRay_StopsTheLeaderUsingPowerUps()
        {
            var w = Board((1, 1000), (2, 10));
            Use(w, 2, PowerUpIds.FreezeRay);

            Assert.Throws<BadInputException>(() => Use(w, 1, PowerUpIds.Stimulus));
            Assert.Single(w[1].HeldPowerUps);
            Assert.False(w[1].IsActive(PowerUpIds.Frozen, Now.AddHours(3.1)));
        }

        // ---------- boosts ----------

        [Fact]
        public void TimeMachine_RefundsOnlyALossFromTheLastHour()
        {
            var w = Board((1, 1000));
            w[1].LastLossAmount = 400;
            w[1].LastLossUtcTicks = Now.AddMinutes(-30).Ticks;
            Use(w, 1, PowerUpIds.TimeMachine);
            Assert.Equal(1400, w[1].NetWorth, 2);

            w[1].LastLossAmount = 400;
            w[1].LastLossUtcTicks = Now.AddHours(-2).Ticks;
            Assert.Throws<BadInputException>(() => Use(w, 1, PowerUpIds.TimeMachine));
        }

        [Fact]
        public void Echo_FiresTheNextInstantPowerUpTwice()
        {
            var w = Board((1, 10_000));
            Use(w, 1, PowerUpIds.Echo);
            Use(w, 1, PowerUpIds.Stimulus);
            Assert.Equal(20_000, w[1].NetWorth, 2); //two $5,000 stimulus cheques
            Assert.False(w[1].IsActive(PowerUpIds.Echo, Now));
        }

        [Fact]
        public void Copycat_CopiesTheLastPowerUpSomeoneElseUsed()
        {
            var w = Board((1, 1000), (2, 1000));
            Use(w, 2, PowerUpIds.Stimulus);
            Use(w, 1, PowerUpIds.Copycat);
            Assert.Equal(PowerUpIds.Stimulus, Assert.Single(w[1].HeldPowerUps).Id);

            //the last one used is now player 2's again, so player 2 can't copy it
            Assert.Throws<BadInputException>(() => Use(w, 2, PowerUpIds.Copycat));
        }

        [Fact]
        public void Scratchie_PrizesAddUpAndTheTopPrizeIsTheJackpot()
        {
            Assert.Equal(1, PowerUpCatalog.ScratchiePrizes.Sum(p => p.Chance), 6);

            var w = Board((1, 1000));
            w.Data.JackpotPool = 50_000;
            Use(w, 1, PowerUpIds.Scratchie, () => 0.995);
            Assert.Equal(51_000, w[1].NetWorth, 2);
            Assert.Equal(0, w.Data.JackpotPool, 2);

            Use(w, 1, PowerUpIds.Scratchie, () => 0.1);
            Assert.Equal(56_000, w[1].NetWorth, 2);
        }

        [Fact]
        public void RiggedRaffle_WinsTheNextDraw()
        {
            var w = Board((1, 1000), (2, 1000), (3, 1000));
            Running(w[3], PowerUpIds.RiggedRaffle);

            var result = w.Raffle.Draw(Now, () => 0); //an honest draw would pick player 1

            Assert.Equal(3UL, result.Value.Winner.UserId);
            Assert.False(w[3].IsActive(PowerUpIds.RiggedRaffle, Now));
        }
    }
}
