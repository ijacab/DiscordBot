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
        public void RepayFromCash_PaysAsMuchAsSpareCashCovers()
        {
            var account = new CoinAccount { Debt = 1_000, NetWorth = 300.456 };
            Assert.Equal(300.45, account.RepayFromCash());
            Assert.Equal(699.55, account.Debt, 2);
            Assert.InRange(account.NetWorth, -0.01, 0.01);
        }

        [Fact]
        public void RepayFromCash_ClearsDebtWhenCashIsEnough()
        {
            var account = new CoinAccount { Debt = 200, NetWorth = 5_000 };
            Assert.Equal(200, account.RepayFromCash());
            Assert.Equal(0, account.Debt);
            Assert.Equal(4_800, account.NetWorth, 2);
        }

        [Fact]
        public void RepayFromCash_DoesNothingWithoutCashOrDebt()
        {
            var broke = new CoinAccount { Debt = 500, NetWorth = 0 };
            Assert.Equal(0, broke.RepayFromCash());
            Assert.Equal(500, broke.Debt);

            var clear = new CoinAccount { Debt = 0, NetWorth = 900 };
            Assert.Equal(0, clear.RepayFromCash());
            Assert.Equal(900, clear.NetWorth);
        }

        [Fact]
        public void Repayments_GoIntoTheJackpot()
        {
            var data = new CoinAccounts { JackpotPool = 1_000 };
            var sale = new CoinAccount { Debt = 100 };
            Assert.Equal(150, data.PayDebtFrom(sale, 250));
            Assert.Equal(1_100, data.JackpotPool, 2);

            var cash = new CoinAccount { Debt = 500, NetWorth = 200 };
            Assert.Equal(200, data.RepayFromCash(cash));
            Assert.Equal(1_300, data.JackpotPool, 2);

            var clear = new CoinAccount { NetWorth = 50 };
            Assert.Equal(50, data.PayDebtFrom(clear, 50));
            Assert.Equal(0, data.RepayFromCash(clear));
            Assert.Equal(1_300, data.JackpotPool, 2);
        }

        [Fact]
        public void PayDebtFrom_WithNoDebt_KeepsEverything()
        {
            var account = new CoinAccount();
            Assert.Equal(300, account.PayDebtFrom(300));
            Assert.Equal(0, account.Debt);
        }

        [Fact]
        public void Debt_GrowsOnePercentAnHour()
        {
            Assert.Equal(1_010, CoinAccount.GrowDebtOneHour(1_000), 2);

            double debt = 1_000;
            for (int hour = 0; hour < 24; hour++)
                debt = CoinAccount.GrowDebtOneHour(debt);
            Assert.InRange(debt, 1_269, 1_270.5); //about 27% a day left alone
        }

        [Fact]
        public void NetRentPerHour_IsWhatTheLandlordIsPaid()
        {
            var data = new CoinAccounts { PropertyIndex = 1.0 };
            var landlord = new CoinAccount { UserId = 1 };
            landlord.Properties.Add(new PropertyHolding { Kind = PropertyKind.House });
            data.Accounts.Add(landlord);

            PropertyService.PayRentOneHour(data, new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(PropertyCatalog.NetRentPerHour(PropertyKind.House, 1.0), landlord.NetWorth, 2);
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
