using Common.Services;
using DiscordBot;
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
    public class StatsTests
    {
        private static AppSettings Settings => new AppSettings { PrestigeDisabled = true };

        private static CoinAccount Busy()
        {
            var a = new CoinAccount { UserId = 1, Name = "busy", NetWorth = 12_345, Debt = 500, NetWinningsToday = 300 };
            a.Stats.GamesPlayed = 40; a.Stats.BetsWon = 18; a.Stats.BetsLost = 22; a.Stats.TotalMoneyBet = 90_000;
            a.Stats.Games["Roulette"] = new GameRecord { Played = 20, Won = 8, Lost = 12, MoneyBet = 40_000, Net = -3_000, BestWin = 5_000, WorstLoss = 2_000 };
            a.Stats.Games["Slots"] = new GameRecord { Played = 20, Won = 10, Lost = 10, MoneyBet = 50_000, Net = 4_000, BestWin = 9_000, WorstLoss = 1_000 };
            a.Stats.DonationAmountsToDict[2] = 1_000;
            a.NetWorthHistory.AddRange(Enumerable.Range(0, 200).Select(i => 1_000.0 + i * 10));
            a.Properties.Add(new PropertyHolding { Kind = PropertyKind.House });
            a.Grant(PowerUpIds.Shield, DateTime.UtcNow);
            return a;
        }

        private static StatsContext Context(CoinAccount a) => new StatsContext
        {
            StocksValue = 2_000, PropertyValue = 150_000, HourlyRent = 400, Rank = 2, Players = 5, JackpotPool = 9_999,
            Holdings = new[] { ("ABC", 10, 5.0) }, OpenShorts = 1, OpenOptions = 2, EnteredDraws = 4, NameOf = id => "user" + id,
        };

        [Fact]
        public void EverySection_BuildsForANewAndABusyAccount_AndFitsAnEmbed()
        {
            foreach (var account in new[] { new CoinAccount { UserId = 9, Name = "new" }, Busy() })
                foreach (var section in StatsReport.Sections)
                {
                    string text = StatsReport.Build(section.Key, account, Context(account));
                    Assert.False(string.IsNullOrWhiteSpace(text), section.Key);
                    Assert.True(text.Length < 4000, section.Key);
                }
        }

        [Fact]
        public void Pages_ShowTheNumbers()
        {
            var a = Busy();
            var c = Context(a);
            Assert.Contains("#2 of 5", StatsReport.Build("overview", a, c));
            Assert.Contains("Last 7 days", StatsReport.Build("overview", a, c));
            Assert.Contains("**Roulette**", StatsReport.Build("games", a, c));
            Assert.Contains("Most profitable: **Slots**", StatsReport.Build("games", a, c));
            Assert.Contains("ABC x10", StatsReport.Build("stocks", a, c));
            Assert.Contains("$9,999", StatsReport.Build("jackpot", a, c));
            Assert.Contains("user2: $1,000", StatsReport.Build("donations", a, c));
            Assert.Contains("Shield", StatsReport.Build("powerups", a, c));
        }

        [Theory]
        [InlineData("stock", "stocks")]
        [InlineData("PU", "powerups")]
        [InlineData("debt", "property")]
        [InlineData("games", "games")]
        public void Find_UnderstandsShortWords(string word, string key) => Assert.Equal(key, StatsReport.Find(word).Key);

        [Fact]
        public void Find_RejectsUnknownWords() => Assert.Null(StatsReport.Find("nonsense"));

        [Fact]
        public void GameRecord_CountsWinsLossesAndPushes()
        {
            var record = new GameRecord();
            record.Add(100, 300); //+200
            record.Add(100, 0);   //-100
            record.Add(100, 100); //push
            Assert.Equal((3, 1, 1, 1), (record.Played, record.Won, record.Lost, record.Pushed));
            Assert.Equal(300, record.MoneyBet);
            Assert.Equal(100, record.Net);
            Assert.Equal(200, record.BestWin);
            Assert.Equal(100, record.WorstLoss);
        }

        [Fact]
        public void StatsMenuId_Fits()
        {
            string id = ComponentId.Build(MoneyComponents.Stats, "pick", ulong.MaxValue.ToString());
            Assert.True(id.Length <= ComponentId.MaxLength);
            Assert.True(ComponentId.TryParse(id, out string game, out _, out _));
            Assert.Equal(MoneyComponents.Stats, game);
        }

        [Fact]
        public async Task Bets_RecordTheGameAndWhatFedTheJackpot()
        {
            var coins = new CoinService(new LocalFileService(), Settings);
            coins.GetAll().Accounts.Clear();
            coins.GetAll().JackpotPool = 0;
            var account = new CoinAccount { UserId = 31, Name = "bettor", NetWorth = 10_000 };
            coins.GetAll().Accounts.Add(account);
            var bets = new BetManager(coins, new StockService(new LocalFileService(), coins), Settings);

            await bets.InitiateBet(account, 1_000);
            await bets.ResolveBet(31, "bettor", 1_000, 0, updateRemote: false, game: "Roulette");
            await bets.InitiateBet(account, 1_000);
            await bets.ResolveBet(31, "bettor", 1_000, 0, updateRemote: false);

            Assert.Equal(1, account.Stats.Games["Roulette"].Lost);
            Assert.Equal(1, account.Stats.Games["Other"].Lost);
            Assert.Equal(1_500, account.Stats.MoneyFedToJackpot, 2);
            Assert.Equal(1, account.Stats.DailyRolls); //the daily roll happened once, on the first bet
        }

        [Fact]
        public void StockTrades_AreCounted()
        {
            var coins = new CoinService(new LocalFileService(), Settings);
            var stocks = new StockService(new LocalFileService(), coins);
            var account = new CoinAccount { UserId = 32, Name = "trader", NetWorth = 100_000 };
            coins.GetAll().Accounts.Add(account);
            string symbol = stocks.GetMarket().Stocks.First(s => s.Role == StockRole.Normal).Symbol;

            stocks.Buy(account, symbol, "5");
            stocks.Sell(account, symbol, "all");

            var s = account.Stats;
            Assert.Equal((1, 1, 5, 5), (s.StockBuys, s.StockSells, (int)s.SharesBought, (int)s.SharesSold));
            Assert.True(s.MoneySpentOnStocks > 0 && s.StockSaleProceeds > 0 && s.SellTaxPaid > 0);
            Assert.True(s.StockRealizedProfit < 0); //the spread and the tax cost something
            Assert.Equal(s.StockRealizedProfit, s.WorstStockSale, 2);
        }

        [Fact]
        public async Task Property_IsCounted()
        {
            var coins = new CoinService(new LocalFileService(), Settings);
            var property = new PropertyService(coins);
            var account = new CoinAccount { UserId = 33, Name = "landlord", NetWorth = 10_000_000 };
            coins.GetAll().Accounts.Add(account);

            await property.Buy(account, PropertyKind.House);
            PropertyService.PayRentOneHour(coins.GetAll());
            PropertyService.PayHoldingOneDay(coins.GetAll());
            await property.Sell(account, 1);

            var s = account.Stats;
            Assert.Equal((1, 1), (s.PropertiesBought, s.PropertiesSold));
            Assert.True(s.PropertySpent > 0 && s.PropertySaleProceeds > 0 && s.RentEarned > 0 && s.HoldingCostsPaid > 0);
        }

        [Fact]
        public void Attacks_AreCounted()
        {
            var victim = new CoinAccount { UserId = 1, NetWorth = 1_000 };
            var attacker = new CoinAccount { UserId = 2, NetWorth = 0 };
            RaffleService.Hit(victim, attacker, 400, DateTime.UtcNow);

            Assert.Equal(1, attacker.Stats.AttacksLanded);
            Assert.Equal(1, victim.Stats.AttacksSuffered);
            Assert.Equal(400, attacker.Stats.MoneyTakenByPowerUps);
            Assert.Equal(400, victim.Stats.MoneyLostToPowerUps);

            RaffleService.Hit(victim, attacker, 5_000, DateTime.UtcNow); //more than they have: the rest is debt
            Assert.True(victim.Stats.PeakDebt > 0);
        }

        [Fact]
        public void DebtRepayments_AreCounted()
        {
            var data = new CoinAccounts();
            var account = new CoinAccount { UserId = 1, NetWorth = 300, Debt = 1_000 };
            data.RepayFromCash(account);
            Assert.Equal(300, account.Stats.DebtRepaid, 2);
        }
    }
}
