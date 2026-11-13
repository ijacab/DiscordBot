using DiscordBot.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    /// <summary>The optional time window on `.networth`, which works like the one on `.stocks`.</summary>
    public class NetWorthWindowTests
    {
        private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
        private static List<double> Hours(int count) => Enumerable.Range(0, count).Select(i => (double)i).ToList();

        [Fact]
        public void NoWindow_KeepsEverything()
        {
            var points = CommandManager.TakeWindow(Hours(100), null, Hour, out string note);
            Assert.Equal(100, points.Count);
            Assert.Equal("", note);
        }

        [Fact]
        public void Window_KeepsTheNewestPointsCoveringIt()
        {
            var points = CommandManager.TakeWindow(Hours(100), TimeSpan.FromHours(48), Hour, out string note);
            Assert.Equal(49, points.Count); //48 hours spans 49 hourly points
            Assert.Equal(99, points.Last());
            Assert.Equal("", note);
        }

        [Fact]
        public void ShortWindow_StillDrawsALine()
        {
            Assert.Equal(2, CommandManager.TakeWindow(Hours(100), TimeSpan.FromMinutes(30), Hour, out _).Count);
        }

        [Fact]
        public void WindowLongerThanTheHistory_SaysSo()
        {
            var points = CommandManager.TakeWindow(Hours(10), TimeSpan.FromDays(7), Hour, out string note);
            Assert.Equal(10, points.Count);
            Assert.Contains("of history so far", note);
        }
    }
}
