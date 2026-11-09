using DiscordBot.Models;
using System;
using System.Linq;
using Xunit;

namespace Tests
{
    public class PatchNotesTests
    {
        [Fact]
        public void PatchNotes_AreNewestFirstAndOneLineEach()
        {
            var notes = PatchNotes.All;
            Assert.NotEmpty(notes);

            var dates = notes.Select(n => DateTime.ParseExact(n.Date, "yyyy-MM-dd", null)).ToList();
            Assert.Equal(dates.OrderByDescending(d => d).ToList(), dates);
            Assert.All(notes, n => Assert.DoesNotContain("\n", n.Text));
            Assert.All(notes, n => Assert.True(n.Text.Length <= 200));
        }
    }
}
