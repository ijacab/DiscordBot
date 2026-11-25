using Common.Services;
using DiscordBot;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Managers;
using DiscordBot.Games.Models;
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
    /// <summary>The pot power-ups, all-time records, week-long raffle channels, own-suggestion delete and the roulette table.</summary>
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
        public void ParseBets_AcceptsAllYourCash_EvenWithCents()
        {
            var bets = RouletteManager.ParseBets(new[] { "red", "max" }, 166_471_371.37);
            Assert.Equal(166_471_371.37, bets[0].Amount, 2);
            //half and half is the same money, so the float sum of two shares isn't a refusal either
            Assert.Equal(2, RouletteManager.ParseBets(new[] { "red", "50%", "17", "50%" }, 166_471_371.37).Count);
            Assert.Throws<BadInputException>(() => RouletteManager.ParseBets(new[] { "red", "max", "17", "max" }, 1_000));
        }

        [Fact]
        public void Labels_ReadLikeTheOldMessage()
        {
            Assert.Equal("00", RouletteView.PocketName("-1"));
            Assert.Equal("1st dozen", RouletteView.Pretty("FirstDozen"));
            Assert.Equal("17", RouletteView.Label(new RouletteBet(1, "17", 5)));
            Assert.Equal("00", RouletteView.Label(new RouletteBet(1, "00", 5)));
            Assert.Equal("Red", RouletteView.Label(new RouletteBet(1, "red", 5)));
        }

        [Fact]
        public void TheResultText_IsTheOldMessage()
        {
            var lost = new RouletteResult { Name = "uxh", Pocket = "23", Tags = new[] { "Red", "Odd" }, Cash = 9_000,
                Lines = new[] { new RouletteLine { Label = "Black", Amount = 1_000 } } };
            string loss = RouletteView.Text(lost, "<@1>", true);
            Assert.Equal("Winning results were **23**, Red, Odd.\nuxh you did not make any successful bets. Your cash is now $9,000.", loss);

            var won = new RouletteResult { Name = "uxh", Pocket = "00", Cash = 12_000, Bonus = 50, BonusGranted = true, Notes = new[] { "🎲 note" },
                Lines = new[] { new RouletteLine { Label = "00", Amount = 100, Won = true, Back = 3_600 }, new RouletteLine { Label = "Red", Amount = 100 } } };
            string win = RouletteView.Text(won, "<@1>", true);
            Assert.StartsWith("Winning results were **00**.", win);
            Assert.Contains("`Your winning bets were:`\n00: $100 -> $3,600\n", win);
            Assert.DoesNotContain("Red:", win); //only winning bets are listed
            Assert.Contains("(+ bonus of $50)", win);
            Assert.Contains("<@1> `Your cash is now $12,000`", win);
            Assert.Contains("You will get a bonus 2% net worth each hour", win);
            Assert.EndsWith("🎲 note", win);
            Assert.DoesNotContain("net worth is now", win);
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
            return (new RouletteManager(coins, bets, settings), account, coins);
        }

        [Fact]
        public async Task ASpin_SettlesTheMoney_RemembersTheBets_AndSaysYourCash()
        {
            var (roulette, account, coins) = Table();
            var bets = RouletteManager.ParseBets(new[] { "red", "1000" }, account.NetWorth);

            var result = await roulette.Spin(5, "spinner", bets);

            Assert.Equal(account.NetWorth, result.Cash, 2);
            double net = result.TotalPaid - result.Staked;
            Assert.Equal(10_000 + net, account.NetWorth, 2);
            if (net < 0)
            {
                Assert.Equal(-1_000, net, 2);
                Assert.Equal(750, coins.JackpotPool, 2); //a loss feeds the jackpot like any bet
            }
            else
                Assert.True(net >= 1_000);
            Assert.Contains("Your cash is now", RouletteView.Text(result, "<@5>", true));

            Assert.True(roulette.TryLastBets(5, out var last)); //Spin again plays these
            Assert.Equal(new[] { 1_000.0 }, last.Select(b => b.Amount));
            Assert.False(roulette.TryLastBets(99, out _)); //somebody else has none of their own
        }

        [Fact]
        public async Task EveryoneSpinsWithTheirOwnBets_NoSharedTable()
        {
            var settings = new AppSettings { PrestigeDisabled = true };
            var coins = new CoinService(new LocalFileService(), settings);
            coins.GetAll().Accounts.Clear();
            coins.GetAll().JackpotPool = 0;
            coins.GetAll().Accounts.Add(new CoinAccount { UserId = 1, Name = "a", NetWorth = 10_000 });
            coins.GetAll().Accounts.Add(new CoinAccount { UserId = 2, Name = "b", NetWorth = 10_000 });
            var roulette = new RouletteManager(coins, new BetManager(coins, new StockService(new LocalFileService(), coins), settings), settings);

            await roulette.Spin(1, "a", RouletteManager.ParseBets(new[] { "red", "1000" }, 10_000));
            await roulette.Spin(2, "b", RouletteManager.ParseBets(new[] { "17", "2000" }, 10_000));

            roulette.TryLastBets(1, out var first);
            roulette.TryLastBets(2, out var second);
            Assert.Equal("red", first[0].Choice);
            Assert.Equal("17", second[0].Choice);
        }

        [Fact]
        public async Task ASpinRefusesNoBets_AndBetsUnderTheMinimum()
        {
            var (roulette, account, _) = Table();
            await Assert.ThrowsAsync<BadInputException>(() => roulette.Spin(5, "spinner", new System.Collections.Generic.List<RouletteChoice>()));

            var small = RouletteManager.ParseBets(new[] { "red", "10" }, account.NetWorth);
            await Assert.ThrowsAsync<BadInputException>(() => roulette.Spin(5, "spinner", small));
            Assert.Equal(10_000, account.NetWorth); //nothing was taken
        }

        [Fact]
        public async Task OldBetsAreHeldToWhatYouStillHave()
        {
            var (roulette, account, _) = Table(cash: 2_000);
            var big = RouletteManager.ParseBets(new[] { "red", "5000" }, 10_000);

            var result = await roulette.Spin(5, "spinner", big);

            Assert.True(result.Staked <= 2_000);
        }

        [Fact]
        public async Task AllInWithCents_StillSpins()
        {
            var (roulette, account, _) = Table(cash: 123_456.78);
            var bets = RouletteManager.ParseBets(new[] { "red", "max" }, account.NetWorth);
            var result = await roulette.Spin(5, "spinner", bets);
            Assert.Equal(123_456.78, result.Staked, 2);
        }

        [Fact]
        public void TheButtons_CarryNoOwner_SoAnyoneCanPressThem()
        {
            var ids = RouletteView.Buttons().Components.SelectMany(r => ((Discord.ActionRowComponent)r).Components)
                .OfType<Discord.ButtonComponent>().Select(b => b.CustomId).ToList();
            Assert.Equal(new[] { "rlt:spin", "rlt:half", "rlt:double", "rlt:max", "rlt:bet" }, ids);
        }

        [Fact]
        public void RecordPeaks_CatchesARecordWithinTheMinute_AndOnlyReportsRealRises()
        {
            var coins = new CoinService(new LocalFileService(), new AppSettings { PrestigeDisabled = true });
            coins.GetAll().Accounts.Clear();
            coins.GetAll().AllTimeBest.Clear();
            var a = Account(1, 1_000);
            coins.GetAll().Accounts.Add(a);

            Assert.True(coins.RecordPeaks(x => x.NetWorth));   //first record
            Assert.False(coins.RecordPeaks(x => x.NetWorth));  //nothing higher
            a.NetWorth = 5_000;
            Assert.True(coins.RecordPeaks(x => x.NetWorth));
            a.NetWorth = 200;
            Assert.False(coins.RecordPeaks(x => x.NetWorth));  //a drop keeps the peak
            Assert.Equal(5_000, coins.GetAll().AllTimeBest[1].Worth);
        }

        [Fact]
        public void ConvoNames_ShowDisplayNamesButKeepTheUsernameAsTheKey()
        {
            var lines = new[] { new ConvoLine { Speaker = "kris.k", Text = "hello" } };
            Assert.Equal("**kris\\.k**: hello", ConvoModel.Format(lines, boldSpeakers: true));
            Assert.Equal("**Kris**: hello", ConvoModel.Format(lines, boldSpeakers: true, displayName: u => u == "kris.k" ? "Kris" : null));
            Assert.Equal("**kris\\.k**: hello", ConvoModel.Format(lines, boldSpeakers: true, displayName: _ => null)); //no display name known

            var member = new Moq.Mock<Discord.IGuildUser>();
            member.Setup(m => m.Username).Returns("kris.k");
            member.Setup(m => m.GlobalName).Returns("Kristoff");
            member.Setup(m => m.Nickname).Returns("Kris");
            Assert.Equal("Kris", ConvoCorpusService.DisplayNameOf(member.Object));
            member.Setup(m => m.Nickname).Returns((string)null);
            Assert.Equal("Kristoff", ConvoCorpusService.DisplayNameOf(member.Object));
            member.Setup(m => m.GlobalName).Returns((string)null);
            Assert.Equal("kris.k", ConvoCorpusService.DisplayNameOf(member.Object));
        }
    }
}
