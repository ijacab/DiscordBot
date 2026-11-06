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
        private static DungeonRun PartyOf(int size, int seed = 1)
        {
            var run = new DungeonRun(1000, new Random(seed));
            for (int i = 1; i <= size; i++)
                run.AddMember((ulong)i, $"Player {i}");
            return run;
        }

        private static void AssertPngOfCardSize(MemoryStream stream)
        {
            byte[] bytes = stream.ToArray();
            Assert.True(bytes.Length > 8);
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);
            Assert.Equal((byte)'N', bytes[2]);
            Assert.Equal((byte)'G', bytes[3]);

            using Image image = Image.Load(bytes);
            Assert.Equal(DungeonCardRenderer.Width, image.Width);
            Assert.Equal(DungeonCardRenderer.Height, image.Height);
        }

        [Fact]
        public void Render_SoloParty_ProducesCardOfFixedSize()
        {
            using MemoryStream card = DungeonCardRenderer.Render(PartyOf(1));

            AssertPngOfCardSize(card);
        }

        [Fact]
        public void Render_FullParty_ProducesCardOfFixedSize()
        {
            using MemoryStream card = DungeonCardRenderer.Render(PartyOf(DungeonRun.MaxPartySize));

            AssertPngOfCardSize(card);
        }

        [Fact]
        public void Render_AfterRoomsWithADeadMember_ProducesCardOfFixedSize()
        {
            var run = PartyOf(3, seed: 9);
            run.Member(2).Alive = false;
            for (int i = 0; i < 3 && !run.IsOver; i++)
                run.Descend();

            using MemoryStream card = DungeonCardRenderer.Render(run);

            AssertPngOfCardSize(card);
        }

        [Fact]
        public void Render_ReturnsStreamAtStart()
        {
            using MemoryStream card = DungeonCardRenderer.Render(PartyOf(2));

            Assert.Equal(0, card.Position);
        }
    }
}
