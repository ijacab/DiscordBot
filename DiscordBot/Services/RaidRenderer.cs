using DiscordBot.Games.Models.Cards;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiscordBot.Services
{
    /// <summary>One side of a raid picture: a card's face, name and numbers. Built by the command, so the renderer doesn't depend on the game model.</summary>
    public class RaidSide
    {
        public string Name { get; set; }
        /// <summary>A short line under the name, like CREW OF 3 or MANSION.</summary>
        public string Kind { get; set; }
        public int Level { get; set; }
        public CardRarity Rarity { get; set; }
        public double Power { get; set; }
        /// <summary>The card's image file, or null to draw a stand-in.</summary>
        public string ArtPath { get; set; }
        public bool Winner { get; set; }
    }

    public class RaidScene
    {
        public RaidSide Attacker { get; set; }
        public RaidSide Defender { get; set; }
        /// <summary>The bar across the top, like RAID SUCCEEDED.</summary>
        public string Banner { get; set; }
        public bool Won { get; set; }
        /// <summary>Under the VS: what the dice needed, and what they rolled.</summary>
        public int Needed { get; set; }
        public int Roll { get; set; }
        /// <summary>The bar across the bottom, like +$2,000 LOOT.</summary>
        public string Footer { get; set; }
    }

    /// <summary>Draws a raid as a picture: the raider's card on the left, the defender's on the right, a big VS between them and the dice result.</summary>
    public static class RaidRenderer
    {
        public const int Width = 800;
        public const int Height = 306;

        private const int TitleHeight = 48;
        private const int FooterHeight = 44;
        private const int Margin = 16;
        private const int PanelWidth = 340;
        private const int PanelHeight = 190;
        private const int ArtSize = 128;

        private static readonly Rgba32 Background = new Rgba32(24, 25, 30);
        private static readonly Rgba32 WonColor = new Rgba32(36, 120, 70);
        private static readonly Rgba32 LostColor = new Rgba32(150, 46, 50);
        private static readonly Rgba32 PanelColor = new Rgba32(40, 42, 48);
        private static readonly Rgba32 TrackColor = new Rgba32(58, 60, 66);
        private static readonly Rgba32 TextColor = new Rgba32(181, 186, 193);
        private static readonly Rgba32 BrightColor = new Rgba32(255, 255, 255);
        private static readonly Rgba32 MutedColor = new Rgba32(110, 114, 122);
        private static readonly Rgba32 GoldColor = new Rgba32(250, 220, 60);
        private static readonly Rgba32 GoodColor = new Rgba32(87, 242, 135);
        private static readonly Rgba32 BadColor = new Rgba32(237, 66, 69);

        //resized card art, so each picture doesn't decode the photo again
        private static readonly ConcurrentDictionary<string, Image<Rgba32>> ArtCache = new ConcurrentDictionary<string, Image<Rgba32>>();

        public static MemoryStream Render(RaidScene scene)
        {
            using var image = new Image<Rgba32>(Width, Height);
            PixelFont.FillRect(image, 0, 0, Width, Height, Background);
            PixelFont.FillRect(image, 0, 0, Width, TitleHeight, scene.Won ? WonColor : LostColor);
            string banner = Clip((scene.Banner ?? "").ToUpperInvariant(), 40);
            PixelFont.DrawText(image, banner, (Width - PixelFont.TextWidth(banner, 3)) / 2, (TitleHeight - PixelFont.GlyphHeight * 3) / 2, BrightColor, 3);

            int panelY = TitleHeight + 12;
            DrawSide(image, scene.Attacker, Margin, panelY);
            DrawSide(image, scene.Defender, Width - Margin - PanelWidth, panelY);

            //the VS and the dice in the middle
            int middle = Width / 2;
            PixelFont.DrawText(image, "VS", middle - PixelFont.TextWidth("VS", 6) / 2, panelY + 14, GoldColor, 6);
            string need = $"{scene.Needed}+";
            PixelFont.DrawText(image, "NEED", middle - PixelFont.TextWidth("NEED", 2) / 2, panelY + 68, MutedColor, 2);
            PixelFont.DrawText(image, need, middle - PixelFont.TextWidth(need, 3) / 2, panelY + 85, TextColor, 3);
            string rolled = scene.Roll.ToString();
            PixelFont.DrawText(image, "ROLLED", middle - PixelFont.TextWidth("ROLLED", 2) / 2, panelY + 114, MutedColor, 2);
            PixelFont.DrawText(image, rolled, middle - PixelFont.TextWidth(rolled, 4) / 2, panelY + 133, scene.Won ? GoodColor : BadColor, 4);

            int footerY = Height - FooterHeight;
            PixelFont.FillRect(image, 0, footerY, Width, FooterHeight, PanelColor);
            string footer = Clip((scene.Footer ?? "").ToUpperInvariant(), 50);
            PixelFont.DrawText(image, footer, (Width - PixelFont.TextWidth(footer, 3)) / 2, footerY + (FooterHeight - PixelFont.GlyphHeight * 3) / 2, scene.Won ? GoodColor : TextColor, 3);

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
        }

        private static void DrawSide(Image<Rgba32> image, RaidSide side, int x, int y)
        {
            PixelFont.FillRect(image, x, y, PanelWidth, PanelHeight, PanelColor);
            if (side.Winner)
                Border(image, x, y, PanelWidth, PanelHeight, GoldColor, 3);

            int artX = x + 12, artY = y + 12;
            Border(image, artX - 3, artY - 3, ArtSize + 6, ArtSize + 6, RarityColor(side.Rarity), 3);
            DrawArt(image, side, artX, artY);

            int textX = artX + ArtSize + 14;
            int chars = Math.Max(4, (x + PanelWidth - 8 - textX) / 12);
            int lineY = artY;
            foreach (string line in Wrap((side.Name ?? "").ToUpperInvariant().Replace("\"", ""), chars, 3))
            {
                PixelFont.DrawText(image, line, textX, lineY, side.Winner ? BrightColor : TextColor, 2);
                lineY += 18;
            }
            lineY = Math.Max(lineY + 6, artY + 62);
            PixelFont.DrawText(image, Clip($"LV {side.Level} {side.Kind}".ToUpperInvariant(), chars), textX, lineY, RarityColor(side.Rarity), 2);
            PixelFont.DrawText(image, Clip(side.Rarity.ToString().ToUpperInvariant(), chars), textX, lineY + 20, MutedColor, 2);

            //the power across the bottom of the panel
            string power = $"POWER {side.Power:0}";
            PixelFont.DrawText(image, power, x + (PanelWidth - PixelFont.TextWidth(power, 3)) / 2, y + PanelHeight - 34, GoldColor, 3);
        }

        private static void DrawArt(Image<Rgba32> image, RaidSide side, int x, int y)
        {
            Image<Rgba32> art = LoadArt(side.ArtPath);
            if (art == null)
            {
                PixelFont.FillRect(image, x, y, ArtSize, ArtSize, TrackColor);
                PixelSprites.Draw(image, SpriteKind.Sword, x + (ArtSize - PixelSprites.Size * 5) / 2, y + (ArtSize - PixelSprites.Size * 5) / 2, 5);
                return;
            }
            for (int dy = 0; dy < ArtSize; dy++)
                for (int dx = 0; dx < ArtSize; dx++)
                    image[x + dx, y + dy] = art[dx, dy];
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

        private const int StripCard = 150;
        private const int StripGap = 10;

        /// <summary>
        /// A row of cards, as opened from a pack: each face framed in its rarity's colour (a foil gets a gold star), with its name, rarity and
        /// power under it.
        /// </summary>
        public static MemoryStream RenderStrip(IList<BattlePerson> cards)
        {
            int count = Math.Max(1, cards.Count);
            int width = Margin * 2 + count * StripCard + (count - 1) * StripGap;
            int height = Margin * 2 + StripCard + 70;
            using var image = new Image<Rgba32>(width, height);
            PixelFont.FillRect(image, 0, 0, width, height, Background);
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                int x = Margin + i * (StripCard + StripGap), y = Margin;
                PixelFont.FillRect(image, x, y, StripCard, StripCard + 66, PanelColor);
                Border(image, x, y, StripCard, StripCard + 66, RarityColor(card.Rarity), 3);
                int artX = x + (StripCard - ArtSize) / 2, artY = y + 8;
                DrawArt(image, new RaidSide { ArtPath = card.FilePath }, artX, artY);
                if (card.Foil)
                    PixelFont.DrawText(image, "*", artX + ArtSize - 16, artY + 2, GoldColor, 3);
                string name = (card.Name ?? "").ToUpperInvariant().Replace("\"", "");
                int textY = artY + ArtSize + 6;
                foreach (string line in Wrap(name, (StripCard - 8) / 6, 2))
                {
                    PixelFont.DrawText(image, line, x + (StripCard - PixelFont.TextWidth(line, 1)) / 2, textY, BrightColor, 1);
                    textY += 10;
                }
                string rarity = (card.Foil ? "FOIL " : "") + card.Rarity.ToString().ToUpperInvariant();
                PixelFont.DrawText(image, rarity, x + (StripCard - PixelFont.TextWidth(rarity, 1)) / 2, y + StripCard + 40, RarityColor(card.Rarity), 1);
                string power = $"POWER {card.Power:0}";
                PixelFont.DrawText(image, power, x + (StripCard - PixelFont.TextWidth(power, 2)) / 2, y + StripCard + 50, GoldColor, 2);
            }
            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
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

        /// <summary>Breaks text into lines of at most <paramref name="width"/> characters at spaces, at most <paramref name="maxLines"/> of them.</summary>
        public static List<string> Wrap(string text, int width, int maxLines)
        {
            var lines = new List<string>();
            string current = "";
            foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string w = word.Length > width ? word.Substring(0, width) : word;
                if (current.Length == 0) current = w;
                else if (current.Length + 1 + w.Length <= width) current += " " + w;
                else
                {
                    lines.Add(current);
                    current = w;
                }
            }
            if (current.Length > 0) lines.Add(current);
            return lines.Take(maxLines).ToList();
        }

        private static string Clip(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }
    }
}
