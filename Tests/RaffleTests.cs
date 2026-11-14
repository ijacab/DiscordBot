using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    public class RaffleTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

        private static CoinAccount Account(ulong id, double cash = 10_000, bool active = true) => new CoinAccount
        {
            UserId = id,
            Name = "user" + id,
            NetWorth = cash,
            LastActiveUtcTicks = active ? (Now - TimeSpan.FromMinutes(10)).Ticks : 0
        };

        private static RaffleService Service(params CoinAccount[] accounts)
        {
            //no Update is called, so the real coin file is never written
            var coinService = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coinService.GetAll().Accounts.Clear();
            coinService.GetAll().Accounts.AddRange(accounts);
            coinService.GetAll().JackpotPool = 0;
            coinService.GetAll().LastRaffleSlot = 0;
            return new RaffleService(coinService, new AppSettings { PrestigeDisabled = true }, a => a.NetWorth);
        }

        private static void Hold(CoinAccount account, string id) => account.Grant(id, Now);

        [Fact]
        public void Catalog_HasBetweenThirtyAndFortyUniquePowerUps()
        {
            Assert.InRange(PowerUpCatalog.All.Count, 30, 40);
            Assert.Equal(PowerUpCatalog.All.Count, PowerUpCatalog.All.Select(p => p.Id).Distinct().Count());
            Assert.All(PowerUpCatalog.All, p => Assert.True(p.Weight > 0 && !string.IsNullOrEmpty(p.Description)));
        }

        [Fact]
        public void Pick_FollowsTheWeights()
        {
            Assert.Equal(PowerUpCatalog.All[0].Id, PowerUpCatalog.Pick(0).Id);
            Assert.Equal(PowerUpCatalog.All.Last().Id, PowerUpCatalog.Pick(0.9999999).Id);
        }

        [Fact]
        public void Slots_AreAnHourLong()
        {
            var start = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
            long slot = RaffleService.SlotOf(start);
            Assert.Equal(slot, RaffleService.SlotOf(start.AddMinutes(59)));
            Assert.Equal(slot + 1, RaffleService.SlotOf(start.AddMinutes(60)));
            Assert.Equal(slot, RaffleService.SlotOf(RaffleService.NextRaffleUtc(start).AddSeconds(-1)));
            Assert.Equal(TimeSpan.FromHours(1), RaffleService.NextRaffleUtc(start.AddMinutes(60)) - RaffleService.NextRaffleUtc(start));
            Assert.True(RaffleService.NextRaffleUtc(Now) > Now);
        }

        [Fact]
        public void Draw_OnlyEntersPlayersActiveInTheSixHoursBefore()
        {
            var stale = Account(1, active: false);
            var old = Account(2); old.LastActiveUtcTicks = (Now - TimeSpan.FromMinutes(6 * 60 + 1)).Ticks;
            var fresh = Account(3);
            var service = Service(stale, old, fresh);

            var result = service.Draw(Now, () => 0);

            Assert.NotNull(result);
            Assert.Equal(3UL, result.Winner.UserId);
            Assert.Equal(1, result.Entrants);
        }

        [Fact]
        public void OneAction_EntersTheNextSixDraws()
        {
            //acted at 11:30: entered in the 12:00 to 17:00 draws, not the 18:00 one
            var player = Account(1); player.LastActiveUtcTicks = Now.AddMinutes(-30).Ticks;
            for (int hour = 0; hour < 6; hour++)
                Assert.True(RaffleService.IsEnteredIn(player, Now.AddHours(hour)));
            Assert.False(RaffleService.IsEnteredIn(player, Now.AddHours(6)));
            Assert.Equal(6, RaffleService.EnteredDrawCount(player, Now.AddMinutes(-30)));
            Assert.Equal(1, RaffleService.EnteredDrawCount(player, Now.AddHours(4).AddMinutes(10)));
            Assert.Equal(0, RaffleService.EnteredDrawCount(new CoinAccount(), Now));
        }

        [Fact]
        public void Draw_IsMeasuredFromTheDrawTimeNotWhenTheLoopRuns()
        {
            //acted 5h59m before the 12:00 draw, which the minute loop only got to at 12:02
            var player = Account(1); player.LastActiveUtcTicks = (Now - TimeSpan.FromMinutes(5 * 60 + 59)).Ticks;
            Assert.NotNull(Service(player).Draw(Now.AddMinutes(2), () => 0));

            //acted at 12:01, after the 12:00 draw ran: entered in the 13:00 one
            var late = Account(2); late.LastActiveUtcTicks = Now.AddMinutes(1).Ticks;
            Assert.True(RaffleService.IsEnteredInNextDraw(late, Now.AddMinutes(1)));
            var service = Service(late);
            service.Draw(Now, () => 0);
            Assert.NotNull(service.Draw(Now.AddHours(1).AddMinutes(1), () => 0));
        }

        [Fact]
        public void Draw_StillHappensAfterShorterSlotsWereSaved()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            coins.GetAll().Accounts.Add(Account(1));
            coins.GetAll().LastRaffleSlot = RaffleService.SlotOf(Now) * 2 + 1; //a 30 minute slot number from the old schedule
            var service = new RaffleService(coins, new AppSettings { PrestigeDisabled = true }, a => a.NetWorth);

            Assert.NotNull(service.Draw(Now, () => 0));
            Assert.Equal(RaffleService.SlotOf(Now), coins.GetAll().LastRaffleSlot);
            Assert.Null(service.Draw(Now.AddMinutes(10), () => 0));
        }

        [Fact]
        public void Draw_PowerUpGoesOffStraightAway()
        {
            var player = Account(1);
            var service = Service(player);

            var result = service.Draw(Now, () => 0);

            Assert.Null(result.Kept);
            Assert.False(string.IsNullOrEmpty(result.Effect));
            Assert.Empty(player.HeldPowerUps);
            Assert.True(player.IsActive(result.Prize.Id, Now) || result.Prize.Kind == PowerUpKind.Instant);
            Assert.Contains("went off", RaffleService.Announcement(result));
        }

        [Fact]
        public void Draw_WinnerWithABetOpenKeepsThePowerUp()
        {
            var player = Account(1);
            var service = Service(player);

            var result = service.Draw(Now, () => 0, new[] { 1UL });

            Assert.NotNull(result.Kept);
            Assert.Single(player.HeldPowerUps);
            Assert.Contains("kept", RaffleService.Announcement(result));
        }

        [Fact]
        public void Draw_NobodyActive_GivesNothing()
        {
            var service = Service(Account(1, active: false));
            Assert.Null(service.Draw(Now, () => 0));
        }

        [Fact]
        public void Draw_HappensOncePerSlot()
        {
            var player = Account(1);
            var service = Service(player);

            Assert.NotNull(service.Draw(Now, () => 0));
            Assert.Null(service.Draw(Now.AddMinutes(10), () => 0));
            Assert.Null(service.Draw(Now.AddMinutes(59), () => 0));
            player.LastActiveUtcTicks = Now.AddMinutes(30).Ticks;
            Assert.NotNull(service.Draw(Now.AddMinutes(60), () => 0));
        }

        [Fact]
        public void Held_ExpiresAfterADayAndIsCapped()
        {
            var player = Account(1);
            Hold(player, PowerUpIds.Stimulus);
            player.Prune(Now.AddHours(23));
            Assert.Single(player.HeldPowerUps);
            player.Prune(Now.AddHours(25));
            Assert.Empty(player.HeldPowerUps);

            for (int i = 0; i < PowerUpCatalog.MaxHeld + 3; i++) Hold(player, PowerUpIds.Stimulus);
            Assert.Equal(PowerUpCatalog.MaxHeld, player.HeldPowerUps.Count);
        }

        [Fact]
        public void TimedBuff_RunsThenExpires_AndStacksTime()
        {
            var player = Account(1);
            var hot = PowerUpCatalog.Find(PowerUpIds.HotStreak);
            player.Start(hot, Now);
            Assert.True(player.IsActive(PowerUpIds.HotStreak, Now.AddHours(2.9)));
            Assert.False(player.IsActive(PowerUpIds.HotStreak, Now.AddHours(3.1)));

            player.Start(hot, Now);
            player.Start(hot, Now);
            Assert.True(player.IsActive(PowerUpIds.HotStreak, Now.AddHours(5)));
        }

        [Fact]
        public void Charges_RunOut()
        {
            var player = Account(1);
            player.Start(PowerUpCatalog.Find(PowerUpIds.SafetyNet), Now);
            for (int i = 0; i < PowerUpCatalog.SafetyNetCharges; i++)
                Assert.True(player.TryConsumeCharge(PowerUpIds.SafetyNet, Now));
            Assert.False(player.TryConsumeCharge(PowerUpIds.SafetyNet, Now));
            Assert.False(player.IsActive(PowerUpIds.SafetyNet, Now));
        }

        [Fact]
        public void BetBonus_HotStreakAddsHalfTheWin()
        {
            var player = Account(1);
            player.Start(PowerUpCatalog.Find(PowerUpIds.HotStreak), Now);
            Assert.Equal(500, player.BetBonus(baseWinnings: 2_000, betAmount: 1_000, Now));
            Assert.Equal(0, player.BetBonus(baseWinnings: 0, betAmount: 1_000, Now));
        }

        [Fact]
        public void BetBonus_GoldenTicketTriplesOneWin_WithNoCap()
        {
            var player = Account(1);
            player.Start(PowerUpCatalog.Find(PowerUpIds.GoldenTicket), Now);
            Assert.Equal(0, player.BetBonus(0, 1_000, Now)); //a loss keeps the ticket
            Assert.Equal(2_000, player.BetBonus(2_000, 1_000, Now));
            Assert.Equal(0, player.BetBonus(2_000, 1_000, Now)); //used up

            player.Start(PowerUpCatalog.Find(PowerUpIds.GoldenTicket), Now);
            Assert.Equal(198_000_000, player.BetBonus(100_000_000, 1_000_000, Now)); //extra is twice the win, however big
        }

        [Fact]
        public void BetBonus_SafetyNetRefundsThreeQuartersOfFiveLosses()
        {
            var player = Account(1);
            player.Start(PowerUpCatalog.Find(PowerUpIds.SafetyNet), Now);
            for (int i = 0; i < 5; i++)
                Assert.Equal(750, player.BetBonus(0, 1_000, Now));
            Assert.Equal(0, player.BetBonus(0, 1_000, Now));
        }

        [Fact]
        public void JackpotFever_RaisesTheChance()
        {
            Assert.False(BetManager.RollsJackpot(0.10));
            Assert.True(BetManager.RollsJackpot(0.10, PowerUpCatalog.JackpotFeverChance));
        }

        [Fact]
        public void Shield_IsActiveForTwelveHours()
        {
            var player = Account(1);
            player.Start(PowerUpCatalog.Find(PowerUpIds.Shield), Now);
            Assert.True(player.IsShielded(Now.AddHours(11)));
            Assert.False(player.IsShielded(Now.AddHours(13)));
        }

        [Fact]
        public void Use_RejectsBadNumbers_AndKeepsTheList()
        {
            var player = Account(1);
            var service = Service(player);
            Assert.Throws<BadInputException>(() => service.ApplyUse(player, 1, Now, () => 0)); //nothing held

            Hold(player, PowerUpIds.Stimulus);
            Assert.Throws<BadInputException>(() => service.ApplyUse(player, 0, Now, () => 0));
            Assert.Throws<BadInputException>(() => service.ApplyUse(player, 2, Now, () => 0));
            Assert.Single(player.HeldPowerUps);
        }

        [Fact]
        public void Use_PicksTheNumberedItem_AndShiftsTheRest()
        {
            var player = Account(1);
            var service = Service(player);
            Hold(player, PowerUpIds.FreeCardPull);
            Hold(player, PowerUpIds.Stimulus);
            Hold(player, PowerUpIds.HotStreak);

            service.ApplyUse(player, 2, Now, () => 0); //the Stimulus

            Assert.Equal(new[] { PowerUpIds.FreeCardPull, PowerUpIds.HotStreak }, player.HeldPowerUps.Select(h => h.Id));
            Assert.Equal(15_000, player.NetWorth); //Stimulus: 10% of $10,000 is under the $5,000 floor
        }

        [Fact]
        public void Use_TimedBuffMovesToActive()
        {
            var player = Account(1);
            var service = Service(player);
            Hold(player, PowerUpIds.TaxHaven);

            service.ApplyUse(player, 1, Now, () => 0);

            Assert.Empty(player.HeldPowerUps);
            Assert.True(player.IsActive(PowerUpIds.TaxHaven, Now));
        }

        [Fact]
        public void Stimulus_HasAFloorButNoCeiling()
        {
            var poor = Account(1, cash: 100);
            var rich = Account(2, cash: 10_000_000);
            var service = Service(poor, rich);
            Hold(poor, PowerUpIds.Stimulus);
            Hold(rich, PowerUpIds.Stimulus);

            service.ApplyUse(poor, 1, Now, () => 0);
            service.ApplyUse(rich, 1, Now, () => 0);

            Assert.Equal(5_100, poor.NetWorth);
            Assert.Equal(11_000_000, rich.NetWorth); //10% of 10M, no ceiling
        }

        [Fact]
        public void BlueShell_StealsTwentyPercentFromFirst()
        {
            var leader = Account(1, cash: 100_000);
            var player = Account(2, cash: 1_000);
            var service = Service(leader, player);
            Hold(player, PowerUpIds.BlueShell);

            service.ApplyUse(player, 1, Now, () => 0);

            Assert.Equal(80_000, leader.NetWorth);
            Assert.Equal(21_000, player.NetWorth);
        }

        [Fact]
        public void BlueShell_IsRefusedForFirstPlace_AndKept()
        {
            var leader = Account(1, cash: 100_000);
            var other = Account(2, cash: 1_000);
            var service = Service(leader, other);
            Hold(leader, PowerUpIds.BlueShell);

            Assert.Throws<BadInputException>(() => service.ApplyUse(leader, 1, Now, () => 0));
            Assert.Single(leader.HeldPowerUps);
            Assert.Equal(100_000, leader.NetWorth);
        }

        [Fact]
        public void BlueShell_BouncesOffAShield()
        {
            var leader = Account(1, cash: 100_000);
            var player = Account(2, cash: 1_000);
            leader.Start(PowerUpCatalog.Find(PowerUpIds.Shield), Now);
            var service = Service(leader, player);
            Hold(player, PowerUpIds.BlueShell);

            Assert.Throws<BadInputException>(() => service.ApplyUse(player, 1, Now, () => 0));
            Assert.Equal(100_000, leader.NetWorth);
            Assert.Single(player.HeldPowerUps);
        }

        [Fact]
        public void Pickpocket_TakesSixPercentFromEachVictim_SkippingShielded()
        {
            var thief = Account(1, cash: 1_000);
            var rich = Account(2, cash: 10_000_000);
            var normal = Account(3, cash: 10_000);
            var shielded = Account(4, cash: 10_000);
            var inactive = Account(5, cash: 10_000, active: false);
            shielded.Start(PowerUpCatalog.Find(PowerUpIds.Shield), Now);
            var service = Service(thief, rich, normal, shielded, inactive);
            Hold(thief, PowerUpIds.Pickpocket);

            service.ApplyUse(thief, 1, Now, () => 0.5);

            Assert.Equal(10_000_000 - 600_000, rich.NetWorth);
            Assert.Equal(10_000 - 600, normal.NetWorth);
            Assert.Equal(10_000, shielded.NetWorth);
            Assert.Equal(10_000, inactive.NetWorth);
            Assert.Equal(1_000 + 600_000 + 600, thief.NetWorth);
        }

        [Fact]
        public void JackpotDip_TakesThirtyPercentOfThePool()
        {
            var player = Account(1);
            var service = Service(player);
            service_Pool(service, 1_000);
            Hold(player, PowerUpIds.JackpotDip);

            service.ApplyUse(player, 1, Now, () => 0);

            Assert.Equal(10_300, player.NetWorth);
        }

        [Fact]
        public void JackpotDip_EmptyPoolIsRefused_AndKept()
        {
            var player = Account(1);
            var service = Service(player);
            Hold(player, PowerUpIds.JackpotDip);

            Assert.Throws<BadInputException>(() => service.ApplyUse(player, 1, Now, () => 0));
            Assert.Single(player.HeldPowerUps);
        }

        [Fact]
        public void FreeCardPull_AndInterestPass_AndLoadedDice_ChangeTheAccount()
        {
            var player = Account(1);
            player.LastFreePullDate = "20261108";
            player.LastGambleDate = "20261108";
            var service = Service(player);
            Hold(player, PowerUpIds.FreeCardPull);
            Hold(player, PowerUpIds.InterestPass);
            Hold(player, PowerUpIds.LoadedDice);

            service.ApplyUse(player, 1, Now, () => 0);
            Assert.Null(player.LastFreePullDate);

            service.ApplyUse(player, 1, Now, () => 0);
            Assert.Equal("20261108", player.MostRecentDateBonusMet);

            service.ApplyUse(player, 1, Now, () => 0);
            Assert.Null(player.LastGambleDate);
            Assert.True(player.TryConsumeCharge(PowerUpIds.LoadedDice, Now));
        }

        private static void service_Pool(RaffleService service, double amount)
        {
            //the service shares the CoinService it was built with, reached through the accounts it holds
            var field = typeof(RaffleService).GetField("_coinService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            ((CoinService)field.GetValue(service)).GetAll().JackpotPool = amount;
        }

        [Fact]
        public void Stock_NoSpreadWithInsiderTrader()
        {
            var stock = new Stock { Symbol = "X", RiskTier = 5, Price = 10, FairPrice = 10, PreviousPrice = 10, BasePrice = 10 };
            Assert.True(stock.PreviewTrade(10, 0).Fill < stock.PreviewTrade(10).Fill);
            Assert.True(stock.PreviewTrade(-10, 0).Fill > stock.PreviewTrade(-10).Fill);
        }
    }
}
