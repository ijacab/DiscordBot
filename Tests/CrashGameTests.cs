using DiscordBot.Games;
using DiscordBot.Games.Managers;
using System;
using System.Linq;
using Xunit;

namespace Tests
{
    public class CrashGameTests
    {
        [Theory]
        [InlineData(1.5)]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(20)]
        public void EveryTarget_PaysBackTheSameOnAverage(double target)
        {
            var random = new Random(4_242);
            const int launches = 400_000;
            double paid = 0;
            for (int i = 0; i < launches; i++)
                if (CrashGame.CrashPoint(random.NextDouble()) >= target) paid += target;
            Assert.InRange(paid / launches, 0.95, 0.99); //97%, give or take the sampling
        }

        [Fact]
        public void AFewBlowUpOnThePad_AndNoneGoPastTheCap()
        {
            var random = new Random(9);
            var points = Enumerable.Range(0, 200_000).Select(_ => CrashGame.CrashPoint(random.NextDouble())).ToList();
            Assert.InRange(points.Count(p => p <= 1) / 200_000.0, 0.025, 0.035);
            Assert.All(points, p => Assert.InRange(p, 1, CrashGame.MaxMultiplier));
        }

        [Fact]
        public void Multiplier_ClimbsFromOne()
        {
            Assert.Equal(1, CrashGame.MultiplierAt(TimeSpan.Zero));
            Assert.InRange(CrashGame.MultiplierAt(TimeSpan.FromSeconds(6)), 2.0, 2.1);
            Assert.Equal(CrashGame.MaxMultiplier, CrashGame.MultiplierAt(TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void CashedAt_PaysTheAutoTargetTheClockOrNothing()
        {
            var auto = new CrashRound { Bet = 100, CrashAt = 3, AutoCashOut = 2 };
            Assert.Equal(2, CrashManager.CashedAt(auto, 2.3));            //the loop noticed late: still the target
            var tooGreedy = new CrashRound { Bet = 100, CrashAt = 1.8, AutoCashOut = 2 };
            Assert.Null(CrashManager.CashedAt(tooGreedy, 1.9));            //blew before the target
            var manual = new CrashRound { Bet = 100, CrashAt = 3 };
            Assert.Equal(1.5, CrashManager.CashedAt(manual, 1.5));
            Assert.Null(CrashManager.CashedAt(manual, 3));
            var moon = new CrashRound { Bet = 100, CrashAt = CrashGame.MaxMultiplier };
            Assert.Equal(CrashGame.MaxMultiplier, CrashManager.CashedAt(moon, CrashGame.MaxMultiplier));
        }

        [Theory]
        [InlineData("2x", 2)]
        [InlineData("1.5", 1.5)]
        public void Targets_Parse(string text, double expected)
        {
            Assert.True(CrashGame.TryParseTarget(text, out double t));
            Assert.Equal(expected, t);
        }

        [Theory]
        [InlineData("1x")]
        [InlineData("0.5x")]
        [InlineData("NaNx")]
        [InlineData("500x")]
        public void BadTargets_DontParse(string text) => Assert.False(CrashGame.TryParseTarget(text, out _));
    }
}
