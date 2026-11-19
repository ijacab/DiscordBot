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
    public class MoneyTests
    {
        [Theory]
        [InlineData("1000", 1000)]
        [InlineData("1,000", 1000)]
        [InlineData("$1,000.50", 1000.50)]
        [InlineData("2.5k", 2500)]
        [InlineData("3m", 3_000_000)]
        [InlineData("25%", 2500)]
        [InlineData("max", 10_000)]
        [InlineData("all", 10_000)]
        [InlineData("min", 1_000)]
        [InlineData("half", 5_000)]
        public void TryParse_ReadsEveryWayPeopleTypeMoney(string text, double expected)
        {
            Assert.True(Money.TryParse(text, 10_000, out double amount));
            Assert.Equal(expected, amount, 2);
        }

        [Theory]
        [InlineData("NaN")]
        [InlineData("nan")]
        [InlineData("Infinity")]
        [InlineData("-Infinity")]
        [InlineData("1e400")]
        [InlineData("-5")]
        [InlineData("0")]
        [InlineData("0%")]
        [InlineData("150%")]
        [InlineData("lots")]
        [InlineData("")]
        [InlineData(null)]
        public void TryParse_RefusesJunk(string text)
        {
            Assert.False(Money.TryParse(text, 10_000, out _));
        }

        [Fact]
        public void Credit_PaysDebtFirst_AndDebitRefusesWhatIsntThere()
        {
            var data = new CoinAccounts();
            var account = new CoinAccount { NetWorth = 100, Debt = 300 };
            Assert.Equal(200, Money.Credit(data, account, 500), 2);
            Assert.Equal(0, account.Debt, 2);
            Assert.Equal(300, account.NetWorth, 2);
            Assert.Throws<BadInputException>(() => Money.Debit(account, 301));
            Assert.Throws<BadInputException>(() => Money.Debit(account, double.NaN));
            Assert.Equal(300, account.NetWorth, 2);
        }

        [Fact]
        public void Seize_NeverTakesMoreThanCashPlusWhatAssetsCanBack()
        {
            var victim = new CoinAccount { NetWorth = 100 };
            var thief = new CoinAccount();
            var (taken, toDebt) = Money.Seize(victim, thief, 1_000, debtRoom: 400);
            Assert.Equal(500, taken, 2);
            Assert.Equal(400, toDebt, 2);
            Assert.Equal(500, thief.NetWorth, 2);
            Assert.Equal(0, victim.NetWorth, 2);
        }

        [Fact]
        public void Holds_AreCountedUntilReleased()
        {
            Money.Hold(777_001, 500);
            Money.Hold(777_001, 250);
            Assert.Equal(750, Money.Held(777_001), 2);
            Money.Release(777_001, 500);
            Assert.Equal(250, Money.Held(777_001), 2);
            Money.ReleaseAll(777_001);
            Assert.Equal(0, Money.Held(777_001));
        }

        [Fact]
        public async System.Threading.Tasks.Task DailyHandout_IsPaidOnceADay_OnlyToRecentPlayers()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var data = coins.GetAll();
            data.Accounts.Clear();
            data.TimesInterestPaidForList.Clear();
            data.DateDailyIncrementPaidFor = null;
            var active = new CoinAccount { UserId = 1, NetWorth = 0, LastActiveUtcTicks = DateTime.UtcNow.Ticks };
            var gone = new CoinAccount { UserId = 2, NetWorth = 0, LastActiveUtcTicks = DateTime.UtcNow.AddDays(-30).Ticks };
            data.Accounts.Add(active);
            data.Accounts.Add(gone);

            await coins.AddInterest();
            data.TimesInterestPaidForList.Clear(); //as if the next hour came round
            await coins.AddInterest();

            Assert.Equal(CoinService.DailyIncome, active.NetWorth, 2);
            Assert.Equal(0, gone.NetWorth, 2);
        }
    }
}
