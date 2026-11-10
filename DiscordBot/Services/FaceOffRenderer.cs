using DiscordBot.Games.Models.BattleArena;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace DiscordBot.Services
{
    /// <summary>One fighter's panel on the arena picture. Built by the manager, so the renderer doesn't depend on the game model.</summary>
    public class FaceOffFighterView
    {
        public string Name { get; set; }
        public int Level { get; set; }
        public CardRarity Rarity { get; set; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        /// <summary>The card's image file, or null to draw a stand-in.</summary>
        public string ArtPath { get; set; }
        /// <summary>Short status such as PICKED, THINKING, OUT or WINNER.</summary>
        public string Status { get; set; }
        public bool Out { get; set; }
        public bool Winner { get; set; }
        /// <summary>The revealed pick, such as ATTACK, with its d20 and target. Null before the reveal.</summary>
        public string RevealAction { get; set; }
        public int? Roll { get; set; }
        public string TargetName { get; set; }
        /// <summary>Damage taken this round, shown on the reveal.</summary>
        public int? DamageTaken { get; set; }
    }

    public class FaceOffScene
    {
        public string Title { get; set; }
        public List<FaceOffFighterView> Fighters { get; } = new List<FaceOffFighterView>();
        public string Footer { get; set; }
    }

    /// <summary>Draws the Face Off arena: a title bar, then up to six fighter panels (three a row) with card art and HP bars.</summary>
    public static class FaceOffRenderer
    {
        public const int Width = 800;
        public const int MaxFighters = 6;
        public const int PerRow = 3;

        private const int TitleHeight = 48;
        private const int FooterHeight = 36;
        private const int Margin = 16;
        private const int Gap = 12;
        private const int PanelHeight = 176;
        private const int ArtSize = 112;

        private static readonly Rgba32 Background = new Rgba32(24, 25, 30);
        private static readonly Rgba32 TitleColor = new Rgba32(44, 46, 52);
        private static readonly Rgba32 PanelColor = new Rgba32(40, 42, 48);
        private static readonly Rgba32 OutPanelColor = new Rgba32(28, 29, 32);
        private static readonly Rgba32 TrackColor = new Rgba32(58, 60, 66);
        private static readonly Rgba32 HealthyColor = new Rgba32(87, 242, 135);
        private static readonly Rgba32 HurtColor = new Rgba32(250, 166, 26);
        private static readonly Rgba32 CriticalColor = new Rgba32(237, 66, 69);
        private static readonly Rgba32 TextColor = new Rgba32(181, 186, 193);
        private static readonly Rgba32 BrightColor = new Rgba32(255, 255, 255);
        private static readonly Rgba32 MutedColor = new Rgba32(110, 114, 122);
        private static readonly Rgba32 GoldColor = new Rgba32(250, 220, 60);
        private static readonly Rgba32 PickedColor = new Rgba32(88, 101, 242);

        //resized card art, so each frame doesn't decode the photo again
        private static readonly ConcurrentDictionary<string, Image<Rgba32>> ArtCache = new ConcurrentDictionary<string, Image<Rgba32>>();

        public static int HeightFor(int fighters)
        {
            int rows = Math.Max(1, (Math.Min(fighters, MaxFighters) + PerRow - 1) / PerRow);
            return TitleHeight + Margin + rows * PanelHeight + (rows - 1) * Gap + FooterHeight;
        }

        public static MemoryStream Render(FaceOffScene scene)
        {
            int count = Math.Min(scene.Fighters.Count, MaxFighters);
            int columns = Math.Clamp(count, 1, PerRow);
            int panelWidth = (Width - 2 * Margin - (columns - 1) * Gap) / columns;

            using var image = new Image<Rgba32>(Width, HeightFor(count));
            PixelFont.FillRect(image, 0, 0, image.Width, image.Height, Background);
            PixelFont.FillRect(image, 0, 0, Width, TitleHeight, TitleColor);
            PixelFont.DrawText(image, Clip((scene.Title ?? "").ToUpperInvariant(), 40), Margin, (TitleHeight - PixelFont.GlyphHeight * 3) / 2, BrightColor, 3);

            for (int i = 0; i < count; i++)
            {
                int column = i % PerRow, row = i / PerRow;
                int x = Margin + column * (panelWidth + Gap);
                int y = TitleHeight + Margin + row * (PanelHeight + Gap);
                DrawFighter(image, scene.Fighters[i], x, y, panelWidth);
            }

            if (!string.IsNullOrEmpty(scene.Footer))
                PixelFont.DrawText(image, Clip(scene.Footer.ToUpperInvariant(), 64), Margin, image.Height - FooterHeight + 10, TextColor, 2);

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
        }

        private static void DrawFighter(Image<Rgba32> image, FaceOffFighterView fighter, int x, int y, int width)
        {
            PixelFont.FillRect(image, x, y, width, PanelHeight, fighter.Out ? OutPanelColor : PanelColor);
            if (fighter.Winner)
                Border(image, x, y, width, PanelHeight, GoldColor, 3);

            //card art with a border in the rarity colour
            int artX = x + 10, artY = y + 10;
            Border(image, artX - 3, artY - 3, ArtSize + 6, ArtSize + 6, RarityColor(fighter.Rarity), 3);
            DrawArt(image, fighter, artX, artY);

            int textX = artX + ArtSize + 12;
            int textChars = Math.Max(4, (x + width - 8 - textX) / 12);
            PixelFont.DrawText(image, Clip((fighter.Name ?? "").ToUpperInvariant(), textChars), textX, artY, fighter.Out ? MutedColor : BrightColor, 2);
            PixelFont.DrawText(image, Clip($"LV {fighter.Level}", textChars), textX, artY + 22, RarityColor(fighter.Rarity), 2);
            PixelFont.DrawText(image, Clip(fighter.Rarity.ToString().ToUpperInvariant(), textChars), textX, artY + 40, MutedColor, 2);

            if (fighter.RevealAction != null)
            {
                string pick = fighter.Roll.HasValue && fighter.Roll > 0 ? $"{fighter.RevealAction} {fighter.Roll}" : fighter.RevealAction;
                PixelFont.DrawText(image, Clip(pick, textChars), textX, artY + 64, GoldColor, 2);
                if (!string.IsNullOrEmpty(fighter.TargetName))
                    PixelFont.DrawText(image, Clip($"ON {fighter.TargetName.ToUpperInvariant()}", textChars), textX, artY + 82, TextColor, 2);
            }
            else if (!string.IsNullOrEmpty(fighter.Status))
            {
                Rgba32 statusColor = fighter.Winner ? GoldColor : fighter.Out ? CriticalColor : fighter.Status == "PICKED" ? PickedColor : TextColor;
                PixelFont.DrawText(image, Clip(fighter.Status, textChars), textX, artY + 64, statusColor, 2);
            }

            //HP bar across the panel, under the art
            int barX = x + 10, barY = y + PanelHeight - 40, barWidth = width - 20;
            double fraction = fighter.MaxHp <= 0 ? 0 : Math.Clamp((double)Math.Max(0, fighter.Hp) / fighter.MaxHp, 0, 1);
            PixelFont.FillRect(image, barX, barY, barWidth, 12, TrackColor);
            if (fraction > 0)
                PixelFont.FillRect(image, barX, barY, (int)Math.Round(barWidth * fraction), 12, HealthColor(fraction));
            PixelFont.DrawText(image, $"HP {Math.Max(0, fighter.Hp)}", barX, barY + 18, fighter.Out ? MutedColor : TextColor, 2);

            if (fighter.DamageTaken.HasValue && fighter.DamageTaken > 0)
            {
                string hit = $"-{fighter.DamageTaken}";
                PixelFont.DrawText(image, hit, x + width - 10 - PixelFont.TextWidth(hit, 2), barY + 18, CriticalColor, 2);
            }
        }

        private static void DrawArt(Image<Rgba32> image, FaceOffFighterView fighter, int x, int y)
        {
            Image<Rgba32> art = LoadArt(fighter.ArtPath);
            if (art == null)
            {
                PixelFont.FillRect(image, x, y, ArtSize, ArtSize, TrackColor);
                PixelSprites.Draw(image, SpriteKind.Sword, x + (ArtSize - PixelSprites.Size * 5) / 2, y + (ArtSize - PixelSprites.Size * 5) / 2, 5);
            }
            else
            {
                for (int dy = 0; dy < ArtSize; dy++)
                    for (int dx = 0; dx < ArtSize; dx++)
                    {
                        Rgba32 pixel = art[dx, dy];
                        if (fighter.Out)
                        {
                            byte grey = (byte)((pixel.R + pixel.G + pixel.B) / 3 / 2);
                            pixel = new Rgba32(grey, grey, grey);
                        }
                        image[x + dx, y + dy] = pixel;
                    }
            }
        }

        private static Image<Rgba32> LoadArt(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            return ArtCache.GetOrAdd(path, p =>
            {
                try
                {
                    var art = Image.Load<Rgba32>(p);
                    art.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(ArtSize, ArtSize), Mode = ResizeMode.Crop }));
                    return art;
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }

        /// <summary>Forgets cached art for a card image that was replaced.</summary>
        public static void ForgetArt(string path)
        {
            if (path != null && ArtCache.TryRemove(path, out Image<Rgba32> art))
                art?.Dispose();
        }

        private static void Border(Image<Rgba32> image, int x, int y, int width, int height, Rgba32 color, int thickness)
        {
            PixelFont.FillRect(image, x, y, width, thickness, color);
            PixelFont.FillRect(image, x, y + height - thickness, width, thickness, color);
            PixelFont.FillRect(image, x, y, thickness, height, color);
            PixelFont.FillRect(image, x + width - thickness, y, thickness, height, color);
        }

        public static Rgba32 RarityColor(CardRarity rarity)
        {
            switch (rarity)
            {
                case CardRarity.Rare: return new Rgba32(66, 135, 245);
                case CardRarity.Epic: return new Rgba32(163, 73, 235);
                case CardRarity.Legendary: return GoldColor;
                default: return new Rgba32(150, 152, 158);
            }
        }

        private static Rgba32 HealthColor(double fraction)
        {
            if (fraction > 0.5) return HealthyColor;
            if (fraction > 0.25) return HurtColor;
            return CriticalColor;
        }

        private static string Clip(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }
    }
}
