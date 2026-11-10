using DiscordBot.Games.Models.Dungeon;
using System;
using System.Linq;
using Xunit;

namespace Tests
{
    public class DungeonSessionTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        private const ulong Host = 1, Guest = 2;

        private static DungeonSession Lobby(int seed = 1) => new DungeonSession(Host, "Host", 1000, T0, new Random(seed));

        private static DungeonSession Started(int seed = 1)
        {
            DungeonSession session = Lobby(seed);
            Assert.Null(session.Start(Host, T0));
            return session;
        }

        /// <summary>Ticks a second apart until the predicate holds, up to a limit.</summary>
        private static DateTime TickUntil(DungeonSession session, DateTime now, Func<bool> done, int limit = 40)
        {
            for (int i = 0; i < limit && !done(); i++)
            {
                now = now.AddSeconds(1);
                session.Tick(now);
            }
            return now;
        }

        /// <summary>Finds a seed whose host gets to the first checkpoint by fighting both bosses.</summary>
        private static DungeonSession AtCheckpoint(out DateTime now)
        {
            for (int seed = 1; seed < 300; seed++)
            {
                DungeonSession session = Started(seed);
                now = T0;
                session.Go(Host, now);
                while (!session.Ended && session.RunOf(Host)?.Phase != RunPhase.AtCheckpoint)
                {
                    now = TickUntil(session, now, () => session.Ended || session.RunOf(Host)?.Phase != RunPhase.Crawling);
                    if (session.RunOf(Host)?.Phase == RunPhase.AtBossDoor)
                        session.Boss(Host, BossChoice.Fight, now);
                }
                if (!session.Ended)
                    return session;
            }
            throw new InvalidOperationException("no seed reached a checkpoint");
        }

        [Fact]
        public void Lobby_StartsOnItsOwnAfterTheWait()
        {
            DungeonSession session = Lobby();

            session.Tick(T0 + DungeonSession.LobbyWait - TimeSpan.FromSeconds(1));
            Assert.False(session.Started);

            session.Tick(T0 + DungeonSession.LobbyWait);
            Assert.True(session.Started);
        }

        [Fact]
        public void Join_IsRefusedWhenFullBrokeOrAlreadyIn()
        {
            DungeonSession session = Lobby();

            Assert.NotNull(session.Join(Host, "Host", 5000, T0));
            Assert.NotNull(session.Join(Guest, "Guest", 999, T0));
            for (ulong id = 2; id <= DungeonSession.MaxPlayers; id++)
                Assert.Null(session.Join(id, $"P{id}", 5000, T0));
            Assert.NotNull(session.Join(99, "Late", 5000, T0));
        }

        [Fact]
        public void Leave_RefundsTheBuyInAndPassesTheHost()
        {
            DungeonSession session = Lobby();
            session.Join(Guest, "Guest", 5000, T0);

            Assert.Null(session.Leave(Host, T0));

            DungeonPayout refund = session.TakePayouts().Single();
            Assert.Equal(Host, refund.UserId);
            Assert.Equal(1000, refund.Amount);
            Assert.Equal(Guest, session.HostId);
            Assert.False(session.Ended);
        }

        [Fact]
        public void Leave_ByTheLastPlayer_ClosesTheLobby()
        {
            DungeonSession session = Lobby();

            session.Leave(Host, T0);

            Assert.True(session.Ended);
        }

        [Fact]
        public void Leave_IsRefusedOnceTheCrawlStarts()
        {
            Assert.NotNull(Started().Leave(Host, T0));
        }

        [Fact]
        public void OnlyTheHostCanStartEarly()
        {
            DungeonSession session = Lobby();
            session.Join(Guest, "Guest", 5000, T0);

            Assert.NotNull(session.Start(Guest, T0));
            Assert.Null(session.Start(Host, T0));
        }

        [Fact]
        public void Go_MovesTheRunOneFloorPerTick()
        {
            DungeonSession session = Started();
            DungeonRun run = session.RunOf(Host);

            Assert.Null(session.Go(Host, T0));
            Assert.True(session.IsDescending(Host));
            Assert.Equal(0, run.Floor);

            session.Tick(T0.AddSeconds(1));
            Assert.Equal(1, run.Floor);
            session.Tick(T0.AddSeconds(2));
            Assert.True(run.Floor == 2 || !run.Alive);
        }

        [Fact]
        public void Crawl_StopsAtTheBossDoorBeforeFloorFive()
        {
            for (int seed = 1; seed < 100; seed++)
            {
                DungeonSession session = Started(seed);
                session.Go(Host, T0);
                DateTime now = TickUntil(session, T0, () => session.Ended || session.RunOf(Host).Phase != RunPhase.Crawling);
                if (session.Ended)
                    continue;

                DungeonRun run = session.RunOf(Host);
                Assert.Equal(RunPhase.AtBossDoor, run.Phase);
                Assert.Equal(4, run.Floor);
                Assert.NotNull(run.PendingBossName);
                Assert.Equal(now + DungeonSession.BossDoorWait, session.BossDoorDeadline(Host));
                Assert.NotNull(session.Go(Host, now));
                return;
            }
            throw new InvalidOperationException("no seed reached the boss door");
        }

        [Fact]
        public void BossDoor_LeftAlone_IsFoughtAfterTheWait()
        {
            for (int seed = 1; seed < 100; seed++)
            {
                DungeonSession session = Started(seed);
                session.Go(Host, T0);
                DateTime now = TickUntil(session, T0, () => session.Ended || session.RunOf(Host).Phase != RunPhase.Crawling);
                if (session.Ended)
                    continue;

                session.Tick(now + DungeonSession.BossDoorWait);

                DungeonRun run = session.RunOf(Host);
                Assert.True(session.Ended || run.Floor == 5);
                Assert.Null(session.BossDoorDeadline(Host));
                return;
            }
            throw new InvalidOperationException("no seed reached the boss door");
        }

        [Fact]
        public void Boss_IsRefusedAwayFromADoor()
        {
            DungeonSession session = Started();

            Assert.NotNull(session.Boss(Host, BossChoice.Fight, T0));
            Assert.NotNull(session.Potion(Host, T0));
        }

        [Fact]
        public void Checkpoint_StopsTheCrawlAndCashOutPaysTheHaul()
        {
            DungeonSession session = AtCheckpoint(out DateTime now);
            DungeonRun run = session.RunOf(Host);
            double haul = run.Haul;

            Assert.False(session.IsDescending(Host));
            session.TakePayouts();
            Assert.Null(session.Cash(Host, now));

            Assert.Equal(haul, session.TakePayouts().Sum(p => p.Amount));
            Assert.True(session.Ended);
            Assert.Equal("CASHED OUT", session.Departed.Single().Status);
        }

        [Fact]
        public void SoloPool_FightsTheShadowRightAway()
        {
            DungeonSession session = AtCheckpoint(out DateTime now);

            Assert.Null(session.EnterPool(Host, now));

            DungeonRun run = session.RunOf(Host);
            Assert.True(session.Ended || run.Phase == RunPhase.Crawling);
        }

        [Fact]
        public void IdleRuns_AreCashedOutByTheTick()
        {
            DungeonSession session = AtCheckpoint(out DateTime now);

            session.Tick(now + DungeonSession.IdleLimit);

            Assert.Equal("IDLE CASH OUT", session.Departed.Single().Status);
            Assert.True(session.Ended);
        }

        [Fact]
        public void DescendingRuns_AreNeverIdle()
        {
            DungeonSession session = Started();
            session.Go(Host, T0);

            session.Tick(T0 + DungeonSession.IdleLimit);

            Assert.DoesNotContain(session.Departed, p => p.Status == "IDLE CASH OUT");
        }

        [Fact]
        public void Changed_IsClearedByPublishingAndSetByActions()
        {
            DungeonSession session = Started();
            session.MarkPublished();
            Assert.False(session.Changed);

            session.Go(Host, T0);
            Assert.True(session.Changed);
        }
    }
}
