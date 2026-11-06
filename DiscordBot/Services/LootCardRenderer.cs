using DiscordBot.Games.Models.Dungeon;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.IO;

namespace DiscordBot.Services
{
    /// <summary>Draws a loot item as a PNG: a tier band, the item's pixel-art icon, its name, and each effect on its own line.</summary>
    public static class LootCardRenderer
    {
        public const int Width = 800;
        public const int Height = 240;

        private const int BandHeight = 52;
        private const int IconScale = 8;

        private static readonly Rgba32 Background = new Rgba32(30, 31, 34);
        private static readonly Rgba32 TextColor = new Rgba32(181, 186, 193);
        private static readonly Rgba32 BrightColor = new Rgba32(255, 255, 255);
        private static readonly Rgba32 EffectColor = new Rgba32(87, 242, 135);

        public static MemoryStream Render(LootItem item)
        {
            using var image = new Image<Rgba32>(Width, Height);
            PixelFont.FillRect(image, 0, 0, Width, Height, Background);
            PixelFont.FillRect(image, 0, 0, Width, BandHeight, TierColor(item.Tier));

            PixelFont.DrawText(image, $"LOOT - {item.Tier.ToString().ToUpperInvariant()}", 16, (BandHeight - PixelFont.GlyphHeight * 3) / 2, BrightColor, 3);

            int iconX = 24, iconY = BandHeight + (Height - BandHeight - PixelSprites.Size * IconScale) / 2;
            PixelSprites.Draw(image, SpriteFor(item.Sprite), iconX, iconY, IconScale);

            int textX = iconX + PixelSprites.Size * IconScale + 32;
            PixelFont.DrawText(image, item.Name.ToUpperInvariant(), textX, BandHeight + 16, BrightColor, 3);

            int y = BandHeight + 64;
            foreach (LootEffectRoll roll in item.Effects)
            {
                PixelFont.DrawText(image, roll.Describe().ToUpperInvariant(), textX, y, EffectColor, 2);
                y += 26;
            }

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
        }

        public static SpriteKind SpriteFor(ItemSprite sprite)
        {
            switch (sprite)
            {
                case ItemSprite.Sword: return SpriteKind.Sword;
                case ItemSprite.Shield: return SpriteKind.Shield;
                case ItemSprite.Amulet: return SpriteKind.Amulet;
                case ItemSprite.Boots: return SpriteKind.Boots;
                case ItemSprite.Ring: return SpriteKind.Ring;
                default: return SpriteKind.Potion;
            }
        }

        private static Rgba32 TierColor(LootTier tier)
        {
            switch (tier)
            {
                case LootTier.Epic: return new Rgba32(155, 89, 182);
                case LootTier.Rare: return new Rgba32(52, 152, 219);
                default: return new Rgba32(120, 124, 132);
            }
        }
    }
}
