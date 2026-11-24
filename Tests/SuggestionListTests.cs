using DiscordBot.Exceptions;
using DiscordBot.Services;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tests
{
    /// <summary>The master suggestions list behind `.sug`.</summary>
    public class SuggestionListTests
    {
        private static string TempFile() => Path.Combine(Path.GetTempPath(), "sug_test_" + Guid.NewGuid().ToString("N") + ".txt");

        [Fact]
        public void AddedSuggestions_AreSavedAsDotPointsInOrder_AndSurviveANewInstance()
        {
            string path = TempFile();
            try
            {
                var list = new SuggestionList(path);
                Assert.Empty(list.All());
                Assert.Equal(1, list.Add("make raids less boring"));
                Assert.Equal(2, list.Add("  - a  command\n that recaps   attacks  "));
                Assert.Equal(new[] { "make raids less boring", "a command that recaps attacks" }, list.All());
                Assert.Equal("- make raids less boring" + Environment.NewLine + "- a command that recaps attacks" + Environment.NewLine, File.ReadAllText(path));
                Assert.Equal(2, new SuggestionList(path).All().Count);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Clear_EmptiesTheListAndSaysHowManyWentAway()
        {
            string path = TempFile();
            try
            {
                var list = new SuggestionList(path);
                list.Add("one");
                list.Add("two");
                Assert.Equal(2, list.Clear());
                Assert.Empty(list.All());
                Assert.Equal(0, list.Clear());
                Assert.Equal(1, list.Add("three"));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void EmptyTooLongAndFullListsAreRefused()
        {
            string path = TempFile();
            try
            {
                var list = new SuggestionList(path);
                Assert.Throws<BadInputException>(() => list.Add("   - \n "));
                Assert.Throws<BadInputException>(() => list.Add(new string('x', SuggestionList.MaxLength + 1)));
                for (int i = 0; i < SuggestionList.MaxItems; i++) list.Add("idea " + i);
                Assert.Throws<BadInputException>(() => list.Add("one too many"));
                Assert.Equal(SuggestionList.MaxItems, list.All().Count);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Chunks_NeverCutASuggestionInHalf_AndStayUnderTheLimit()
        {
            var items = Enumerable.Range(0, 60).Select(i => $"suggestion number {i} " + new string('y', 80)).ToList();
            var chunks = SuggestionList.Chunks(items, 500);
            Assert.True(chunks.Count > 1);
            Assert.All(chunks, c => Assert.True(c.Length <= 500));
            Assert.Equal(items.Select(i => "- " + i), chunks.SelectMany(c => c.Split('\n')));
            Assert.Empty(SuggestionList.Chunks(Array.Empty<string>(), 500));
        }
    }
}
