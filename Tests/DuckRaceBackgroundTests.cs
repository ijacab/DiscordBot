using Common.Services;
using Discord;
using Discord.WebSocket;
using DiscordBot;
using DiscordBot.Games;
using DiscordBot.Games.Managers;
using DiscordBot.Games.Models;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static DiscordBot.Models.CoinAccounts;

namespace Tests
{
    /// <summary>
    /// The duck race replay takes seconds. A typed command runs on the gateway task, so awaiting the replay there froze every other
    /// command (and the heartbeats) until the race ended, and the game lock was held for all of it.
    /// </summary>
    public class DuckRaceBackgroundTests
    {
        /// <summary>A duck race whose replay lasts until the test lets it go.</summary>
        private sealed class GatedDuckRaceManager : DuckRaceManager
        {
            public readonly TaskCompletionSource ReleaseReplay = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource ResultsPosted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool ReplayFails;

            public GatedDuckRaceManager(DiscordSocketClient client, BetManager bets, CoinService coins) : base(client, bets, coins) { }

            protected override async Task AnimateGame(DuckRace game, IEnumerable<DuckRacePlayer> players)
            {
                await ReleaseReplay.Task;
                if (ReplayFails)
                    throw new InvalidOperationException("the message was deleted");
            }

            protected override Task AnnounceStart(DuckRace game, IEnumerable<DuckRacePlayer> players, DuckRacePlayer starter) => Task.CompletedTask;

            protected override Task AnnounceResults(DuckRace game, IReadOnlyList<PlayerResult> results)
            {
                ResultsPosted.TrySetResult();
                return Task.CompletedTask;
            }
        }

        private static (GatedDuckRaceManager Duck, BetManager Bets, CoinService Coins) Setup()
        {
            var settings = new AppSettings { PrestigeDisabled = true };
            var coins = new CoinService(new LocalFileService(), settings);
            coins.GetAll().Accounts.Clear();
            foreach (ulong id in new ulong[] { 1, 2, 3 })
                coins.GetAll().Accounts.Add(new CoinAccount { UserId = id, Name = $"p{id}", NetWorth = 10_000 });
            var bets = new BetManager(coins, new StockService(new LocalFileService(), coins), settings);
            return (new GatedDuckRaceManager(new DiscordSocketClient(), bets, coins), bets, coins);
        }

        private static GameContext Context(ulong userId)
        {
            var channel = new Mock<IMessageChannel>();
            channel.SetupGet(c => c.Id).Returns(77);
            return new GameContext { UserId = userId, Username = $"p{userId}", Channel = channel.Object, GuildId = 99 };
        }

        private static async Task WithinAsync(Task task, string what)
        {
            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(finished == task, what);
            await task;
        }

        [Fact]
        public async Task StartingARace_ReturnsWhileTheReplayIsStillRunning_AndLeavesTheGameFreeForOthers()
        {
            var (duck, bets, _) = Setup();
            await duck.CreateOrJoin(1, 1_000, Context(1));
            await duck.CreateOrJoin(2, 1_000, Context(2));

            await WithinAsync(duck.Start(1, Context(1)), "Start waited for the replay, which would freeze the gateway");
            Assert.False(duck.ResultsPosted.Task.IsCompleted); //still replaying

            //someone else can join or open a race now, instead of waiting behind the lock for the whole replay
            await WithinAsync(duck.CreateOrJoin(3, 1_000, Context(3)), "the game lock was held during the replay");
        }

        [Fact]
        public async Task TheRace_IsPaidOutOnceTheReplayEnds()
        {
            var (duck, bets, _) = Setup();
            await duck.CreateOrJoin(1, 1_000, Context(1));
            await duck.CreateOrJoin(2, 1_000, Context(2));
            await WithinAsync(duck.Start(1, Context(1)), "Start blocked");

            duck.ReleaseReplay.SetResult();
            await WithinAsync(duck.ResultsPosted.Task, "the race never finished");

            Assert.Empty(duck.Games);
            Assert.DoesNotContain(1UL, bets.InitiatedBetUserIds);
            Assert.DoesNotContain(2UL, bets.InitiatedBetUserIds);
        }

        [Fact]
        public async Task AReplayThatFails_StillPaysTheRaceSoNoBetIsStranded()
        {
            var (duck, bets, _) = Setup();
            duck.ReplayFails = true;
            await duck.CreateOrJoin(1, 1_000, Context(1));
            await duck.CreateOrJoin(2, 1_000, Context(2));
            await WithinAsync(duck.Start(1, Context(1)), "Start blocked");

            duck.ReleaseReplay.SetResult();
            await WithinAsync(duck.ResultsPosted.Task, "a failed replay left the race unpaid");

            Assert.Empty(duck.Games);
            Assert.Empty(bets.InitiatedBetUserIds);
        }
    }
}
