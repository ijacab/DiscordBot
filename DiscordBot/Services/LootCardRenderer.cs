using DiscordBot.Games.Models.Dungeon;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.IO;

namespace DiscordBot.Services
{
    /// <summary>Draws a loot box result as a PNG: the tier band, the item name, and the stat boosts.</summary>
    public static class LootCardRenderer
    {
        public const int Width = 800;
        public const int Height = 240;

        private const int BandHeight = 56;
        private const int TitleScale = 3;
        private const int TextScale = 2;

        private static readonly Rgba32 Background = new Rgba32(30, 31, 34);
        private static readonly Rgba32 TextColor = new Rgba32(181, 186, 193);
        private static readonly Rgba32 BrightColor = new Rgba32(255, 255, 255);
        private static readonly Rgba32 GainColor = new Rgba32(87, 242, 135);

        public static MemoryStream Render(LootResult loot)
        {
            using var image = new Image<Rgba32>(Width, Height);
            PixelFont.FillRect(image, 0, 0, Width, Height, Background);

            Rgba32 tierColor = TierColor(loot.Tier);
            PixelFont.FillRect(image, 0, 0, Width, BandHeight, tierColor);
            string band = $"LOOT BOX - {loot.Tier.ToString().ToUpperInvariant()}";
            PixelFont.DrawText(image, band, 16, (BandHeight - PixelFont.GlyphHeight * TitleScale) / 2, BrightColor, TitleScale);

            PixelFont.DrawText(image, loot.Name.ToUpperInvariant(), 16, BandHeight + 20, BrightColor, TitleScale);

            int y = BandHeight + 64;
            PixelFont.DrawText(image, $"ATTACK +{loot.AttackGain}", 16, y, GainColor, TextScale);
            PixelFont.DrawText(image, $"DEFENSE +{loot.DefenseGain}", 16, y + 28, GainColor, TextScale);
            PixelFont.DrawText(image, $"MAX HP +{loot.MaxHpGain}", 16, y + 56, GainColor, TextScale);
            PixelFont.DrawText(image, "THE BOOST LASTS UNTIL THE RUN ENDS", 16, Height - 24, TextColor, TextScale);

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
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
