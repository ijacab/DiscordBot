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
                Assert.Equal(1, list.Add("make raids less boring", 1, "kris"));
                Assert.Equal(2, list.Add("  - a  command\n that recaps   attacks  ", 2, "john123"));
                Assert.Equal(new[] { "make raids less boring", "a command that recaps attacks" }, list.Texts());
                var lines = File.ReadAllLines(path);
                Assert.Equal("- make raids less boring\t1\tkris", lines[0]);
                Assert.Equal("- a command that recaps attacks\t2\tjohn123", lines[1]);
                var again = new SuggestionList(path).All();
                Assert.Equal(2, again.Count);
                Assert.Equal(2UL, again[1].AuthorId);
                Assert.Equal("john123", again[1].Author);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LinesSavedBeforeAuthorsWereKept_StillLoad()
        {
            string path = TempFile();
            try
            {
                File.WriteAllLines(path, new[] { "- old idea", "", "- another old one" });
                var list = new SuggestionList(path);
                Assert.Equal(new[] { "old idea", "another old one" }, list.Texts());
                Assert.All(list.All(), s => { Assert.Equal(0UL, s.AuthorId); Assert.Equal("unknown", s.Author); });
                list.Add("new one", 5, "kris");
                Assert.Equal(3, list.All().Count);
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
        public void RemoveNumbers_TakesOutOnlyThoseSuggestions_AndRefusesAnyOffTheList()
        {
            string path = TempFile();
            try
            {
                var list = new SuggestionList(path);
                foreach (var word in new[] { "a", "b", "c", "d", "e" }) list.Add(word, 1, "kris");
                var removed = list.RemoveNumbers(new[] { 4, 2, 2 });
                Assert.Equal(new[] { "b", "d" }, removed.Select(s => s.Text));
                Assert.Equal(new[] { "a", "c", "e" }, list.Texts());
                //one bad number removes nothing
                Assert.Throws<BadInputException>(() => list.RemoveNumbers(new[] { 1, 9 }));
                Assert.Throws<BadInputException>(() => list.RemoveNumbers(new[] { 0 }));
                Assert.Equal(new[] { "a", "c", "e" }, list.Texts());
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RemoveBy_TakesOutOnePersonsSuggestions_ByIdOrByName()
        {
            string path = TempFile();
            try
            {
                var list = new SuggestionList(path);
                list.Add("k1", 1, "kris");
                list.Add("j1", 2, "John123");
                list.Add("k2", 1, "kris");
                list.Add("old", 0, "unknown");

                Assert.Equal(new[] { "k1", "k2" }, list.RemoveBy(1, "kris").Select(s => s.Text));
                Assert.Equal(new[] { "j1", "old" }, list.Texts());
                //by name alone, ignoring case and a leading @, for someone whose id isn't known
                Assert.Equal(new[] { "j1" }, list.RemoveBy(0, "@john123").Select(s => s.Text));
                Assert.Empty(list.RemoveBy(99, "nobody"));
                Assert.Equal(new[] { "old" }, list.Texts());
            }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData("3", "3")]
        [InlineData("2 5", "2,5")]
        [InlineData("2-4", "2,3,4")]
        [InlineData("#7", "7")]
        public void ParseNumbers_ReadsNumbersAndRanges(string words, string expected)
        {
            Assert.Equal(expected, string.Join(",", SuggestionList.ParseNumbers(words.Split(' '))));
        }

        [Theory]
        [InlineData("kris")]
        [InlineData("3 kris")]
        [InlineData("4-2")]
        [InlineData("0")]
        public void ParseNumbers_IsNullForAnythingElse(string words)
        {
            Assert.Null(SuggestionList.ParseNumbers(words.Split(' ')));
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
        public void Chunks_NeverCutALineInHalf_AndStayUnderTheLimit()
        {
            var lines = Enumerable.Range(0, 60).Select(i => $"- suggestion number {i} " + new string('y', 80)).ToList();
            var chunks = SuggestionList.Chunks(lines, 500);
            Assert.True(chunks.Count > 1);
            Assert.All(chunks, c => Assert.True(c.Length <= 500));
            Assert.Equal(lines, chunks.SelectMany(c => c.Split('\n')));
            Assert.Empty(SuggestionList.Chunks(Array.Empty<string>(), 500));
        }
    }
}
