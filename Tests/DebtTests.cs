using Common.Services;
using DiscordBot;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Linq;
using Xunit;

namespace Tests
{
    public class DebtTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void PayDebtFrom_SendsIncomingMoneyToDebtFirst()
        {
            var account = new CoinAccount { Debt = 100 };
            Assert.Equal(150, account.PayDebtFrom(250));
            Assert.Equal(0, account.Debt);

            account.Debt = 100;
            Assert.Equal(0, account.PayDebtFrom(50));
            Assert.Equal(50, account.Debt);
        }

        [Fact]
        public void PayDebtFrom_WithNoDebt_KeepsEverything()
        {
            var account = new CoinAccount();
            Assert.Equal(300, account.PayDebtFrom(300));
            Assert.Equal(0, account.Debt);
        }

        [Fact]
        public void Debt_GrowsAboutOnePercentADayWhenLeftAlone()
        {
            double debt = 1_000;
            for (int hour = 0; hour < 24; hour++)
                debt = CoinAccount.GrowDebtOneHour(debt);
            Assert.InRange(debt, 1_009.5, 1_010.5);
        }

        [Fact]
        public void Seize_TakesFromCashWhenThereIsEnough()
        {
            var victim = new CoinAccount { NetWorth = 1_000 };
            var thief = new CoinAccount { NetWorth = 0 };

            var (fromCash, toDebt) = RaffleService.Seize(victim, thief, 400);

            Assert.Equal(400, fromCash);
            Assert.Equal(0, toDebt);
            Assert.Equal(600, victim.NetWorth);
            Assert.Equal(0, victim.Debt);
            Assert.Equal(400, thief.NetWorth);
        }

        [Fact]
        public void Seize_PutsTheShortfallOnTheVictimsDebt()
        {
            var victim = new CoinAccount { NetWorth = 100 };
            var thief = new CoinAccount();

            var (fromCash, toDebt) = RaffleService.Seize(victim, thief, 400);

            Assert.Equal(100, fromCash);
            Assert.Equal(300, toDebt);
            Assert.Equal(0, victim.NetWorth);
            Assert.Equal(300, victim.Debt);
            Assert.Equal(400, thief.NetWorth);
        }

        [Fact]
        public void Seize_WithNegativeCash_PutsAllOnDebt()
        {
            var victim = new CoinAccount { NetWorth = -50 };
            var thief = new CoinAccount();

            RaffleService.Seize(victim, thief, 400);

            Assert.Equal(-50, victim.NetWorth);
            Assert.Equal(400, victim.Debt);
            Assert.Equal(400, thief.NetWorth);
        }

        [Fact]
        public void BlueShell_OnAPropertyOnlyLeader_PutsTheAmountOnTheirDebt()
        {
            //the leader has no cash, only a house: 10% of its worth comes off as debt, and their worth falls by the same amount
            var coinService = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coinService.GetAll().Accounts.Clear();
            coinService.GetAll().PropertyIndex = 1.0;

            var leader = new CoinAccount { UserId = 1, Name = "leader", NetWorth = 0 };
            leader.Properties.Add(new PropertyHolding { Kind = PropertyKind.House });
            var thief = new CoinAccount { UserId = 2, Name = "thief", NetWorth = 0 };
            coinService.GetAll().Accounts.Add(leader);
            coinService.GetAll().Accounts.Add(thief);

            var raffle = new RaffleService(coinService, new AppSettings { PrestigeDisabled = true },
                a => a.NetWorth + coinService.PropertyValue(a) - a.Debt);
            thief.Grant(PowerUpIds.BlueShell, Now);

            double houseValue = PropertyCatalog.SaleValue(PropertyKind.House, 1.0);
            double amount = Math.Floor(houseValue * PowerUpCatalog.BlueShellPercent);
            raffle.ApplyUse(thief, 1, Now, () => 0.5);

            Assert.Equal(amount, thief.NetWorth, 2);
            Assert.Equal(amount, leader.Debt, 2);
            Assert.Equal(0, leader.NetWorth, 2);
            Assert.Equal(houseValue - amount, leader.NetWorth + coinService.PropertyValue(leader) - leader.Debt, 2);
            Assert.Empty(thief.HeldPowerUps);
        }
    }
}
