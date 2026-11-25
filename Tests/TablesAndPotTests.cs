using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Managers;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>The jackpot dividend and pot power-ups, all-time records, week-long raffle channels, own-suggestion delete and the roulette table.</summary>
    public class TablesAndPotTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

        private static CoinAccount Account(ulong id, double cash = 10_000) => new CoinAccount
        {
            UserId = id, Name = "user" + id, NetWorth = cash, LastActiveUtcTicks = (Now - TimeSpan.FromMinutes(10)).Ticks,
        };

        private static (RaffleService Service, CoinService Coins) Raffle(double pool, params CoinAccount[] accounts)
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            coins.GetAll().Accounts.AddRange(accounts);
            coins.GetAll().JackpotPool = pool;
            return (new RaffleService(coins, new AppSettings { PrestigeDisabled = true }, a => a.NetWorth), coins);
        }

        //---- jackpot dividend and pot power-ups

        [Fact]
        public void Dividend_PaysAFifthOfTheHundredthOfThePoolToThePoorerHalfOfTheEntrants()
        {
            var accounts = new[] { Account(1, 100), Account(2, 1_000), Account(3, 5_000), Account(4, 9_000) };
            var (service, coins) = Raffle(10_000, accounts);

            var result = service.PayJackpotDividend(Now);

            Assert.Equal(2, result.Recipients);
            Assert.Equal(250, result.Each);   //5% of 10,000 = 500, split two ways
            Assert.Equal(350, accounts[0].NetWorth);
            Assert.Equal(1_250, accounts[1].NetWorth);
            Assert.Equal(5_000, accounts[2].NetWorth);
            Assert.Equal(9_500, coins.JackpotPool, 2);
            Assert.Contains("Jackpot dividend", RaffleService.DividendAnnouncement(result));
        }

        [Fact]
        public void Dividend_DrainsAPoolOverTime_AndSkipsWhenNobodyIsEnteredOrItIsTiny()
        {
            var lone = Account(1, 100);
            var (service, coins) = Raffle(1_000_000, lone);
            for (int hour = 0; hour < 24; hour++)
                service.PayJackpotDividend(Now);
            Assert.True(coins.JackpotPool < 1_000_000 * 0.31, "a day of dividends should take about 70% of an idle pool");

            var (idle, _) = Raffle(1_000_000, new CoinAccount { UserId = 2, Name = "gone", NetWorth = 5 });
            Assert.Null(idle.PayJackpotDividend(Now)); //nobody entered

            var (small, tiny) = Raffle(10, Account(3));
            Assert.Null(small.PayJackpotDividend(Now)); //under $1 each
            Assert.Equal(10, tiny.JackpotPool);
        }

        [Fact]
        public void PotShot_TakesATenthOfThePool_AndAnEmptyPoolIsRefused()
        {
            var player = Account(1, 1_000);
            var (service, coins) = Raffle(5_000, player);
            player.Grant(PowerUpIds.PotShot, Now);
            service.ApplyUse(player, 1, Now, () => 0);
            Assert.Equal(1_500, player.NetWorth);
            Assert.Equal(4_500, coins.JackpotPool, 2);

            var (empty, _) = Raffle(0, player);
            player.Grant(PowerUpIds.PotShot, Now);
            Assert.Throws<BadInputException>(() => empty.ApplyUse(player, player.HeldPowerUps.Count, Now, () => 0));
        }

        [Fact]
        public void PotShare_SplitsFortyPercentBetweenThePoorestHalf_AndIsRefusedAtTheTop()
        {
            var accounts = new[] { Account(1, 100), Account(2, 200), Account(3, 5_000), Account(4, 9_000) };
            var (service, coins) = Raffle(10_000, accounts);
            accounts[0].Grant(PowerUpIds.PotShare, Now);

            service.ApplyUse(accounts[0], 1, Now, () => 0);

            Assert.Equal(2_100, accounts[0].NetWorth); //4,000 shared by the two poorest: 2,000 each
            Assert.Equal(2_200, accounts[1].NetWorth);
            Assert.Equal(5_000, accounts[2].NetWorth);
            Assert.Equal(6_000, coins.JackpotPool, 2);

            accounts[3].Grant(PowerUpIds.PotShare, Now);
            Assert.Throws<BadInputException>(() => service.ApplyUse(accounts[3], 1, Now, () => 0));
        }

        //---- all-time records

        [Fact]
        public void NoteWorth_KeepsOnlyThePeak()
        {
            var data = new CoinAccounts();
            Assert.True(data.NoteWorth(1, "a", 500, Now));
            Assert.False(data.NoteWorth(1, "a", 300, Now.AddHours(1)));
            Assert.True(data.NoteWorth(1, "a-renamed", 900, Now.AddHours(2)));
            Assert.False(data.NoteWorth(1, "a", double.NaN, Now));
            Assert.Equal(900, data.AllTimeBest[1].Worth);
            Assert.Equal("a-renamed", data.AllTimeBest[1].Name);
            Assert.Equal(Now.AddHours(2).Ticks, data.AllTimeBest[1].UtcTicks);
        }

        [Fact]
        public void AllTimeLeaders_FoldInTheCurrentWorthAndThirtyDayHistory_BestFirst()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            coins.GetAll().AllTimeBest.Clear();
            var low = Account(1, 100);
            var peaked = Account(2, 50);
            peaked.NetWorthHistory.AddRange(new[] { 10.0, 5_000.0, 40.0 });
            coins.GetAll().Accounts.AddRange(new[] { low, peaked });

            var leaders = coins.AllTimeLeaders(a => a.NetWorth, Now);

            Assert.Equal(new ulong[] { 2, 1 }, leaders.Select(l => l.UserId));
            Assert.Equal(5_000, leaders[0].Record.Worth);
            Assert.Equal(100, leaders[1].Record.Worth);
        }

        //---- raffle channels

        [Fact]
        public void ActiveChannels_AreEveryChannelUsedThisWeek_NewestFirst_PlusTheRecentFew()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            var data = coins.GetAll();
            data.Accounts.Clear();
            data.ChannelLastActiveUtcTicks.Clear();
            data.RecentActiveChannelIds.Clear();
            data.LastActiveChannelId = 0;
            data.Accounts.Add(Account(1));

            //used on four channels over time, then marked through the real call for the newest
            data.ChannelLastActiveUtcTicks[10] = (Now - TimeSpan.FromDays(6)).Ticks;
            data.ChannelLastActiveUtcTicks[20] = (Now - TimeSpan.FromDays(8)).Ticks; //too old
            data.ChannelLastActiveUtcTicks[30] = (Now - TimeSpan.FromDays(2)).Ticks;
            Assert.Equal(new ulong[] { 30, 10 }, coins.ActiveChannels(TimeSpan.FromDays(7), Now));

            coins.MarkActive(1, 40);
            var channels = coins.ActiveChannels(TimeSpan.FromDays(7), DateTime.UtcNow);
            Assert.Equal(40UL, channels[0]);
            Assert.Contains(40UL, data.RecentActiveChannelIds);
        }

        //---- suggestions

        private static string TempFile() => Path.Combine(Path.GetTempPath(), "sug_own_" + Guid.NewGuid().ToString("N") + ".txt");

        [Fact]
        public void YouCanDeleteYourOwnSuggestions_ButNotSomeoneElses()
        {
            string path = TempFile();
            try
            {
                var list = new SuggestionList(path);
                list.Add("mine one", 1, "me");
                list.Add("theirs", 2, "them");
                list.Add("mine two", 1, "me");

                var ex = Assert.Throws<BadInputException>(() => list.RemoveOwnNumbers(new[] { 1, 2 }, 1));
                Assert.Contains("#2", ex.Message);
                Assert.Equal(3, list.All().Count); //nothing went

                Assert.Throws<BadInputException>(() => list.RemoveOwnNumbers(new[] { 9 }, 1));

                var removed = list.RemoveOwnNumbers(new[] { 3, 1 }, 1);
                Assert.Equal(new[] { "mine one", "mine two" }, removed.Select(r => r.Text));
                Assert.Equal(new[] { "theirs" }, list.Texts());
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void OldSuggestionsWithNoAuthor_CantBeDeletedByAnyone()
        {
            string path = TempFile();
            try
            {
                File.WriteAllLines(path, new[] { "- an old one" });
                var list = new SuggestionList(path);
                Assert.Throws<BadInputException>(() => list.RemoveOwnNumbers(new[] { 1 }, 0));
                Assert.Throws<BadInputException>(() => list.RemoveOwnNumbers(new[] { 1 }, 5));
            }
            finally { File.Delete(path); }
        }

        //---- roulette table

        [Fact]
        public void ParseBets_ReadsPairsAndTheOldDashFormat()
        {
            var pairs = RouletteManager.ParseBets(new[] { "red", "1000", "17", "50" }, 10_000);
            Assert.Equal(new[] { "red", "17" }, pairs.Select(b => b.Choice));
            Assert.Equal(new[] { 1_000.0, 50.0 }, pairs.Select(b => b.Amount));

            var dashed = RouletteManager.ParseBets(new[] { "00-100,36-200", "odd-25%" }, 10_000);
            Assert.Equal(new[] { "00", "36", "odd" }, dashed.Select(b => b.Choice));
            Assert.Equal(2_500, dashed[2].Amount);

            Assert.Throws<BadSyntaxException>(() => RouletteManager.ParseBets(new[] { "red" }, 10_000));
            Assert.Throws<BadSyntaxException>(() => RouletteManager.ParseBets(new[] { "red", "lots" }, 10_000));
            Assert.Throws<BadInputException>(() => RouletteManager.ParseBets(new[] { "purple", "100" }, 10_000));
        }

        [Fact]
        public void ParseBets_RefusesWhatCouldNeverWinOrNeverBePlaced()
        {
            Assert.Throws<BadInputException>(() => RouletteManager.ParseBets(new[] { "37", "100" }, 10_000)); //no such pocket
            Assert.Throws<BadInputException>(() => RouletteManager.ParseBets(new[] { "number", "100" }, 10_000));
            Assert.Throws<BadInputException>(() => RouletteManager.ParseBets(new[] { "red", "100", "black", "100" }, 10_000));
            Assert.Throws<BadInputException>(() => RouletteManager.ParseBets(new[] { "odd", "100", "even", "100" }, 10_000));
            var tooMuch = Assert.Throws<BadInputException>(() => RouletteManager.ParseBets(new[] { "red", "50000" }, 1_000));
            Assert.Contains("only have $1,000", tooMuch.Message);
            Assert.Equal(2, RouletteManager.ParseBets(new[] { "36", "100", "00", "100" }, 10_000).Count);
        }

        [Fact]
        public void Scale_HalvesDoublesAndHoldsToTheCash()
        {
            var bets = new[] { new RouletteChoice { Choice = "red", Amount = 600 }, new RouletteChoice { Choice = "17", Amount = 400 } };
            Assert.Equal(new[] { 300.0, 200.0 }, RouletteView.Scale(bets, "half", 10_000).Select(b => b.Amount));
            Assert.Equal(new[] { 1_200.0, 800.0 }, RouletteView.Scale(bets, "double", 10_000).Select(b => b.Amount));
            Assert.Equal(new[] { 900.0, 600.0 }, RouletteView.Scale(bets, "double", 1_500).Select(b => b.Amount)); //held to the cash
            Assert.Equal(3_000, RouletteView.Scale(bets, "max", 3_000).Sum(b => b.Amount));
            Assert.Empty(RouletteView.Scale(bets, "double", 0));
        }

        [Fact]
        public void Pockets_HaveTheirColours()
        {
            Assert.Equal("🔴", RouletteView.Dot("23"));
            Assert.Equal("⚫", RouletteView.Dot("17"));
            Assert.Equal("🟢", RouletteView.Dot("0"));
            Assert.Equal("🟢", RouletteView.Dot("00"));
            Assert.Equal("00", RouletteView.PocketName("-1"));
            Assert.Equal("1st dozen", RouletteView.Pretty("FirstDozen"));
        }

        private static (RouletteManager Roulette, CoinAccount Account, CoinService Coins) Table(double cash = 10_000)
        {
            var settings = new AppSettings { PrestigeDisabled = true };
            var coins = new CoinService(new LocalFileService(), settings);
            coins.GetAll().Accounts.Clear();
            coins.GetAll().JackpotPool = 0;
            var account = new CoinAccount { UserId = 5, Name = "spinner", NetWorth = cash };
            coins.GetAll().Accounts.Add(account);
            var bets = new BetManager(coins, new StockService(new LocalFileService(), coins), settings);
            return (new RouletteManager(coins, bets, settings, NullLogger<RouletteManager>.Instance), account, coins);
        }

        [Fact]
        public async Task ASpin_SettlesTheMoney_AndTheScreenSaysYourCash()
        {
            var (roulette, account, coins) = Table();
            var session = roulette.Open(5, "spinner");
            session.Bets = RouletteManager.ParseBets(new[] { "red", "1000" }, account.NetWorth);

            var screen = await roulette.Spin(session);

            Assert.Equal(1, session.Spins);
            Assert.Equal(account.NetWorth, screen.Cash, 2);
            Assert.Equal(10_000 + session.SessionNet, account.NetWorth, 2);
            if (session.SessionNet < 0)
            {
                Assert.Equal(-1_000, session.SessionNet, 2);
                Assert.Equal(750, coins.JackpotPool, 2); //a loss feeds the jackpot like any bet
            }
            else
                Assert.True(session.SessionNet >= 1_000);
            Assert.NotNull(screen.Pocket);
            Assert.Contains("Your cash is", RouletteView.Build(screen).Footer.Value.Text);
            Assert.DoesNotContain("net worth", RouletteView.Build(screen).Footer.Value.Text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ASpinRefusesNoBets_AndBetsUnderTheMinimum()
        {
            var (roulette, account, _) = Table();
            var session = roulette.Open(5, "spinner");
            await Assert.ThrowsAsync<BadInputException>(() => roulette.Spin(session));

            session.Bets = RouletteManager.ParseBets(new[] { "red", "10" }, account.NetWorth);
            await Assert.ThrowsAsync<BadInputException>(() => roulette.Spin(session));
            Assert.Equal(10_000, account.NetWorth); //nothing was taken
        }

        [Fact]
        public async Task OldBetsAreHeldToWhatYouStillHave()
        {
            var (roulette, account, _) = Table(cash: 2_000);
            var session = roulette.Open(5, "spinner");
            session.Bets = RouletteManager.ParseBets(new[] { "red", "5000" }, 10_000);

            await roulette.Spin(session);

            Assert.True(session.Bets.Sum(b => b.Amount) <= 2_000);
        }

        [Fact]
        public void TheTable_IsOneEmbedWithOwnerOnlyButtons()
        {
            var screen = new RouletteScreen { Name = "spinner", Cash = 500 };
            var embed = RouletteView.Build(screen);
            Assert.Contains("red 1000", embed.Description); //an empty table says how to bet
            Assert.Equal("🎡 Roulette · spinner", embed.Author.Value.Name);

            var buttons = RouletteView.Buttons(5, 0, 500);
            var ids = buttons.Components.SelectMany(r => ((Discord.ActionRowComponent)r).Components).OfType<Discord.ButtonComponent>().Select(b => b.CustomId).ToList();
            Assert.All(ids, id => Assert.StartsWith("rlt:", id));
            Assert.All(ids, id => Assert.EndsWith(":5", id));
        }
    }
}
