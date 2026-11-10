using DiscordBot.Models;
using System;
using Xunit;

namespace Tests
{
    public class ComponentIdTests
    {
        [Fact]
        public void BuildThenParse_GivesBackTheParts()
        {
            string id = ComponentId.Build("fo", "pick", "attack", "1234");

            Assert.Equal("fo:pick:attack:1234", id);
            Assert.True(ComponentId.TryParse(id, out string game, out string action, out string[] args));
            Assert.Equal("fo", game);
            Assert.Equal("pick", action);
            Assert.Equal(new[] { "attack", "1234" }, args);
        }

        [Fact]
        public void Parse_WithoutArgs_GivesAnEmptyList()
        {
            Assert.True(ComponentId.TryParse("dng:go", out string game, out string action, out string[] args));
            Assert.Equal("dng", game);
            Assert.Equal("go", action);
            Assert.Empty(args);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("dng")]
        [InlineData(":go")]
        [InlineData("dng:")]
        public void Parse_RejectsJunk(string id)
        {
            Assert.False(ComponentId.TryParse(id, out _, out _, out _));
        }

        [Fact]
        public void Build_RejectsSeparatorsInsideParts()
        {
            Assert.Throws<ArgumentException>(() => ComponentId.Build("fo", "pick", "a:b"));
        }

        [Fact]
        public void Build_RejectsIdsLongerThanDiscordAllows()
        {
            Assert.Throws<ArgumentException>(() => ComponentId.Build("fo", "pick", new string('x', ComponentId.MaxLength)));
        }

        [Fact]
        public void Build_FitsAGuidAndAUserId()
        {
            string id = ComponentId.Build("fo", "back-amount", Guid.NewGuid().ToString("N"), ulong.MaxValue.ToString());
            Assert.True(id.Length <= ComponentId.MaxLength);
        }
    }
}
