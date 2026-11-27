using DiscordBot.Managers;
using Xunit;

namespace Tests
{
    public class ConvoLangTests
    {
        [Fact]
        public void SplitTranslatedLines_KeepsOrderAndDropsBlankLines()
        {
            var lines = CommandManager.SplitTranslatedLines("one fish\n\n two fish \nred fish", 3);
            Assert.Equal(new[] { "one fish", "two fish", "red fish" }, lines);
        }

        [Fact]
        public void SplitTranslatedLines_IsNullWhenTheCountChanged()
        {
            Assert.Null(CommandManager.SplitTranslatedLines("one fish two fish\nred fish", 3));
            Assert.Null(CommandManager.SplitTranslatedLines(null, 1));
        }
    }
}
