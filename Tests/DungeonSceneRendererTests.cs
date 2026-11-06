using DiscordBot.Games.Models.Dungeon;
using DiscordBot.Services;
using SixLabors.ImageSharp;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tests
{
    public class DungeonSceneRendererTests
    {
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

        private static DungeonScene SceneWith(int players, SpriteKind sprite)
        {
            var scene = new DungeonScene
            {
                Title = "CRAWL: 4 MIN",
                Caption = "THE TAX DRAGON",
                Sprite = sprite,
                EnemyHp = 12,
                EnemyMaxHp = 40,
                Footer = "Use .dng go"
            };
            for (int i = 0; i < players; i++)
                scene.Players.Add(new ScenePlayer { Name = $"Player {i + 1}", Hp = 20, MaxHp = 30, Floor = i + 1, Haul = 1234.5 });
            return scene;
        }

        [Theory]
        [InlineData(0, SpriteKind.None)]
        [InlineData(1, SpriteKind.Rat)]
        [InlineData(4, SpriteKind.Dragon)]
        public void Scene_RendersAtFixedSize(int players, SpriteKind sprite)
        {
            using MemoryStream picture = DungeonSceneRenderer.Render(SceneWith(players, sprite));

            AssertPngOfSize(picture, DungeonSceneRenderer.Width, DungeonSceneRenderer.Height);
        }

        [Fact]
        public void Scene_WithADownedPlayer_RendersAtFixedSize()
        {
            var scene = SceneWith(2, SpriteKind.Skull);
            scene.Players[1].Status = "DOWN";
            scene.Players[1].Hp = 0;

            using MemoryStream picture = DungeonSceneRenderer.Render(scene);

            AssertPngOfSize(picture, DungeonSceneRenderer.Width, DungeonSceneRenderer.Height);
        }

        [Theory]
        [InlineData(LootTier.Common)]
        [InlineData(LootTier.Rare)]
        [InlineData(LootTier.Epic)]
        public void LootCard_RendersAtFixedSize(LootTier tier)
        {
            var item = new LootItem { Tier = tier, Name = "Dragon Scale", Sprite = ItemSprite.Shield };
            item.Effects.Add(new LootEffectRoll { Effect = LootEffect.Attack, Amount = 2 });
            item.Effects.Add(new LootEffectRoll { Effect = LootEffect.Lifesteal, Amount = 20 });

            using MemoryStream card = LootCardRenderer.Render(item);

            AssertPngOfSize(card, LootCardRenderer.Width, LootCardRenderer.Height);
        }

        [Fact]
        public void EveryItemSprite_MapsToAnItemSprite()
        {
            foreach (ItemSprite sprite in Enum.GetValues(typeof(ItemSprite)))
                Assert.NotEqual(SpriteKind.None, LootCardRenderer.SpriteFor(sprite));
        }

        [Fact]
        public void EverySprite_IsA16By16Grid()
        {
            foreach (SpriteKind kind in Enum.GetValues(typeof(SpriteKind)).Cast<SpriteKind>().Where(k => k != SpriteKind.None))
            {
                var rows = PixelSprites.Rows(kind);
                Assert.Equal(PixelSprites.Size, rows.Count);
                Assert.All(rows, row => Assert.Equal(PixelSprites.Size, row.Length));
            }
        }
    }

    public class DungeonImageStoreTests
    {
        [Fact]
        public void Store_HasNoPictureUntilOneIsSet()
        {
            var store = new DungeonImageStore();

            Assert.Null(store.Latest());
            Assert.Equal(0, store.Version);
        }

        [Fact]
        public void Update_KeepsTheLatestPictureAndBumpsTheVersion()
        {
            var store = new DungeonImageStore();
            store.Update(new byte[] { 1, 2, 3 });
            store.Update(new byte[] { 9 });

            Assert.Equal(new byte[] { 9 }, store.Latest());
            Assert.Equal(2, store.Version);
        }
    }
}
