using DiscordBot.Games.Models.Dungeon;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;

namespace DiscordBot.Services
{
    /// <summary>
    /// Draws one player's solo run as a PNG: a header with the floor and room and the haul, a panel with HP and stats,
    /// and an optional footer for session info such as the time left in the crawl. Uses the pixel font, so labels are uppercase only.
    /// </summary>
    public static class DungeonCardRenderer
    {
        public const int Width = 800;
        public const int Height = 300;

        private const int HeaderHeight = 56;
        private const int PanelLeft = 16;
        private const int PanelTop = HeaderHeight + 16;
        private const int PanelWidth = Width - 32;
        private const int PanelHeight = 170;
        private const int TitleScale = 3;
        private const int TextScale = 2;
        private const int BarHeight = 16;

        private static readonly Rgba32 Background = new Rgba32(30, 31, 34);
        private static readonly Rgba32 HeaderColor = new Rgba32(44, 46, 52);
        private static readonly Rgba32 PanelColor = new Rgba32(40, 42, 48);
        private static readonly Rgba32 DeadPanelColor = new Rgba32(24, 25, 28);
        private static readonly Rgba32 BarTrackColor = new Rgba32(58, 60, 66);
        private static readonly Rgba32 HealthyColor = new Rgba32(87, 242, 135);
        private static readonly Rgba32 HurtColor = new Rgba32(250, 166, 26);
        private static readonly Rgba32 CriticalColor = new Rgba32(237, 66, 69);
        private static readonly Rgba32 TextColor = new Rgba32(181, 186, 193);
        private static readonly Rgba32 BrightColor = new Rgba32(255, 255, 255);
        private static readonly Rgba32 MutedColor = new Rgba32(100, 104, 112);

        /// <summary>Renders the run. <paramref name="footer"/> is optional session info drawn along the bottom.</summary>
        public static MemoryStream Render(DungeonRun run, string footer = null)
        {
            using var image = new Image<Rgba32>(Width, Height);
            PixelFont.FillRect(image, 0, 0, Width, Height, Background);
            DrawHeader(image, run);
            DrawPanel(image, run);

            if (!string.IsNullOrEmpty(footer))
                PixelFont.DrawText(image, footer.ToUpperInvariant(), PanelLeft, Height - 26, TextColor, TextScale);

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
        }

        private static void DrawHeader(Image<Rgba32> image, DungeonRun run)
        {
            PixelFont.FillRect(image, 0, 0, Width, HeaderHeight, HeaderColor);

            int titleY = (HeaderHeight - PixelFont.GlyphHeight * TitleScale) / 2;
            string title = run.LastRoom == null ? "THE DOOR" : $"FLOOR {run.Floor} {run.LastRoom.Value.ToString().ToUpperInvariant()}";
            PixelFont.DrawText(image, title, 16, titleY, BrightColor, TitleScale);

            string haul = $"HAUL ${run.Haul:N2}";
            int haulX = Width - 16 - PixelFont.TextWidth(haul, TitleScale);
            PixelFont.DrawText(image, haul, haulX, titleY, HurtColor, TitleScale);
        }

        private static void DrawPanel(Image<Rgba32> image, DungeonRun run)
        {
            int x = PanelLeft, y = PanelTop;
            PixelFont.FillRect(image, x, y, PanelWidth, PanelHeight, run.Alive ? PanelColor : DeadPanelColor);

            string name = Clip(run.Name.ToUpperInvariant(), 60);
            PixelFont.DrawText(image, name, x + 12, y + 12, run.Alive ? BrightColor : MutedColor, TextScale);

            int barX = x + 12, barY = y + 44, barWidth = PanelWidth - 24;
            PixelFont.FillRect(image, barX, barY, barWidth, BarHeight, BarTrackColor);
            double fraction = run.MaxHp <= 0 ? 0 : Math.Clamp((double)run.Hp / run.MaxHp, 0, 1);
            if (run.Alive && fraction > 0)
                PixelFont.FillRect(image, barX, barY, (int)Math.Round(barWidth * fraction), BarHeight, HealthColor(fraction));

            PixelFont.DrawText(image, $"HP {Math.Max(0, run.Hp)} OF {run.MaxHp}", x + 12, y + 68, TextColor, TextScale);
            PixelFont.DrawText(image, $"ATK {run.Attack}  DEF {run.Defense}", x + 12, y + 92, TextColor, TextScale);
            PixelFont.DrawText(image, $"POTIONS {run.Potions}  KILLS {run.Kills}", x + 12, y + 116, TextColor, TextScale);

            if (!run.Alive)
                PixelFont.DrawText(image, "DOWN", x + 12, y + 136, CriticalColor, TitleScale);
        }

        private static Rgba32 HealthColor(double fraction)
        {
            if (fraction > 0.5) return HealthyColor;
            if (fraction > 0.25) return HurtColor;
            return CriticalColor;
        }

        private static string Clip(string text, int maxLength)
        {
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }
    }
}
