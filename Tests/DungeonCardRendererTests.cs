using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Services;
using SixLabors.ImageSharp;
using System;
using System.IO;
using Xunit;

namespace Tests
{
    public class DungeonCardRendererTests
    {
        private static DungeonRun RunFor(int seed)
        {
            return new DungeonRun((ulong)seed, $"Player {seed}", 1000, new Random(seed));
        }

        private static void AssertPngOfSize(MemoryStream stream, int width, int height)
        {
            byte[] bytes = stream.ToArray();
            Assert.True(bytes.Length > 8);
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);
            Assert.Equal((byte)'N', bytes[2]);
            Assert.Equal((byte)'G', bytes[3]);

            using Image image = Image.Load(bytes);
            Assert.Equal(width, image.Width);
            Assert.Equal(height, image.Height);
        }

        [Fact]
        public void Render_NewRun_ProducesCardOfFixedSize()
        {
            using MemoryStream card = DungeonCardRenderer.Render(RunFor(1));

            AssertPngOfSize(card, DungeonCardRenderer.Width, DungeonCardRenderer.Height);
        }

        [Fact]
        public void Render_WithFooter_ProducesCardOfFixedSize()
        {
            using MemoryStream card = DungeonCardRenderer.Render(RunFor(2), "Crawl ends in 4 min. 3 still crawling");

            AssertPngOfSize(card, DungeonCardRenderer.Width, DungeonCardRenderer.Height);
        }

        [Fact]
        public void Render_AfterRoomsAndDeath_ProducesCardOfFixedSize()
        {
            var run = RunFor(9);
            for (int i = 0; i < 10 && run.Alive; i++)
                run.Descend();

            using MemoryStream card = DungeonCardRenderer.Render(run);

            AssertPngOfSize(card, DungeonCardRenderer.Width, DungeonCardRenderer.Height);
        }

        [Fact]
        public void Render_ReturnsStreamAtStart()
        {
            using MemoryStream card = DungeonCardRenderer.Render(RunFor(3));

            Assert.Equal(0, card.Position);
        }

        [Theory]
        [InlineData(LootTier.Common)]
        [InlineData(LootTier.Rare)]
        [InlineData(LootTier.Epic)]
        public void LootCard_ProducesCardOfFixedSize(LootTier tier)
        {
            var loot = new LootResult { Tier = tier, Name = "Dragon Scale", AttackGain = 2, DefenseGain = 1, MaxHpGain = 9 };
            using MemoryStream card = LootCardRenderer.Render(loot);

            AssertPngOfSize(card, LootCardRenderer.Width, LootCardRenderer.Height);
        }
    }
}
