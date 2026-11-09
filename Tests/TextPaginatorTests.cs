using Common.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Tests
{
    public class TextPaginatorTests
    {
        [Fact]
        public void Paginate_KeepsEveryPageUnderDiscordLimits()
        {
            var lines = Enumerable.Range(0, 400).Select(i => $"**.command{i}** \t Desc: something fairly long to describe command {i} Syntax: `.command{i} 100`");

            var pages = TextPaginator.Paginate(lines);

            Assert.True(pages.Count > 1);
            foreach (var page in pages)
            {
                Assert.True(page.Count <= TextPaginator.FieldsPerEmbed);
                Assert.True(page.Sum(f => f.Length) <= TextPaginator.EmbedTextLimit);
                Assert.All(page, f => Assert.True(f.Length <= TextPaginator.FieldLimit));
            }
        }

        [Fact]
        public void Paginate_KeepsEveryLineIntact()
        {
            var lines = new List<string> { "first line", "second line", "third line" };

            var pages = TextPaginator.Paginate(lines);

            Assert.Single(pages);
            Assert.Equal("first line\nsecond line\nthird line", pages[0][0]);
        }

        [Fact]
        public void Paginate_BreaksAnOverlongLineAtSpaces()
        {
            string longLine = string.Join(" ", Enumerable.Repeat("word", 800));

            var pages = TextPaginator.Paginate(new[] { longLine });

            var fields = pages.SelectMany(p => p).ToList();
            Assert.True(fields.Count > 1);
            Assert.All(fields, f => Assert.True(f.Length <= TextPaginator.FieldLimit));
            int words = fields.Sum(f => f.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal(800, words);
        }

        [Fact]
        public void Paginate_WithNoLines_ReturnsNothing()
        {
            Assert.Empty(TextPaginator.Paginate(Array.Empty<string>()));
        }
    }
}
