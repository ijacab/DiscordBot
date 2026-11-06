using DiscordBot.Games.Models.Dungeon;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;

namespace DiscordBot.Services
{
    /// <summary>
    /// Draws the state of a dungeon party as a PNG: a header with the floor and room, the shared haul, and one panel
    /// per party slot with HP bars and stats. Uses the pixel font, so labels are uppercase only.
    /// </summary>
    public static class DungeonCardRenderer
    {
        public const int Width = 800;
        public const int Height = 460;

        private const int HeaderHeight = 56;
        private const int PanelWidth = 380;
        private const int PanelHeight = 170;
        private const int PanelGap = 8;
        private const int PanelLeft = 16;
        private const int PanelTop = HeaderHeight + 12;
        private const int TitleScale = 3;
        private const int TextScale = 2;
        private const int BarHeight = 16;

        private static readonly Rgba32 Background = new Rgba32(30, 31, 34);
        private static readonly Rgba32 HeaderColor = new Rgba32(44, 46, 52);
        private static readonly Rgba32 PanelColor = new Rgba32(40, 42, 48);
        private static readonly Rgba32 DeadPanelColor = new Rgba32(24, 25, 28);
        private static readonly Rgba32 OpenPanelColor = new Rgba32(34, 35, 39);
        private static readonly Rgba32 BarTrackColor = new Rgba32(58, 60, 66);
        private static readonly Rgba32 HealthyColor = new Rgba32(87, 242, 135);
        private static readonly Rgba32 HurtColor = new Rgba32(250, 166, 26);
        private static readonly Rgba32 CriticalColor = new Rgba32(237, 66, 69);
        private static readonly Rgba32 TextColor = new Rgba32(181, 186, 193);
        private static readonly Rgba32 BrightColor = new Rgba32(255, 255, 255);
        private static readonly Rgba32 MutedColor = new Rgba32(100, 104, 112);

        public static MemoryStream Render(DungeonRun run)
        {
            using var image = new Image<Rgba32>(Width, Height);
            PixelFont.FillRect(image, 0, 0, Width, Height, Background);
            DrawHeader(image, run);

            for (int slot = 0; slot < DungeonRun.MaxPartySize; slot++)
            {
                int column = slot % 2, row = slot / 2;
                int x = PanelLeft + column * (PanelWidth + PanelGap);
                int y = PanelTop + row * (PanelHeight + PanelGap);
                if (slot < run.Members.Count)
                    DrawMemberPanel(image, run.Members[slot], x, y);
                else
                    DrawOpenPanel(image, x, y);
            }

            DrawFooter(image, run);

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
        }

        private static void DrawHeader(Image<Rgba32> image, DungeonRun run)
        {
            PixelFont.FillRect(image, 0, 0, Width, HeaderHeight, HeaderColor);

            string title = run.LastRoom == null ? "THE DOOR" : $"FLOOR {run.Floor} {run.LastRoom.Value.ToString().ToUpperInvariant()}";
            PixelFont.DrawText(image, title, 16, (HeaderHeight - PixelFont.GlyphHeight * TitleScale) / 2, BrightColor, TitleScale);

            string haul = $"HAUL ${run.Haul:N2}";
            int haulX = Width - 16 - PixelFont.TextWidth(haul, TitleScale);
            PixelFont.DrawText(image, haul, haulX, (HeaderHeight - PixelFont.GlyphHeight * TitleScale) / 2, HurtColor, TitleScale);
        }

        private static void DrawFooter(Image<Rgba32> image, DungeonRun run)
        {
            //the pixel font has no slash, so counts are written as "X OF Y"
            string footer = $"PARTY {run.LivingCount} OF {run.Members.Count}   BUY-IN ${run.BuyIn:N2} EACH   CASH VOTES {run.CashOutVotes.Count} OF {run.LivingCount / 2 + 1}";
            PixelFont.DrawText(image, footer, PanelLeft, Height - 26, TextColor, TextScale);
        }

        private static void DrawMemberPanel(Image<Rgba32> image, DungeonMember member, int x, int y)
        {
            Rgba32 panel = member.Alive ? PanelColor : DeadPanelColor;
            PixelFont.FillRect(image, x, y, PanelWidth, PanelHeight, panel);

            string name = Clip(member.Name.ToUpperInvariant(), 26);
            PixelFont.DrawText(image, name, x + 12, y + 12, member.Alive ? BrightColor : MutedColor, TextScale);

            int barX = x + 12, barY = y + 44, barWidth = PanelWidth - 24;
            PixelFont.FillRect(image, barX, barY, barWidth, BarHeight, BarTrackColor);
            double fraction = member.MaxHp <= 0 ? 0 : Math.Clamp((double)member.Hp / member.MaxHp, 0, 1);
            if (member.Alive && fraction > 0)
                PixelFont.FillRect(image, barX, barY, (int)Math.Round(barWidth * fraction), BarHeight, HealthColor(fraction));

            int hp = Math.Max(0, member.Hp);
            PixelFont.DrawText(image, $"HP {hp} OF {member.MaxHp}", x + 12, y + 68, TextColor, TextScale);
            PixelFont.DrawText(image, $"ATK {member.Attack}  DEF {member.Defense}", x + 12, y + 92, TextColor, TextScale);
            PixelFont.DrawText(image, $"POTIONS {member.Potions}  KILLS {member.Kills}", x + 12, y + 116, TextColor, TextScale);

            if (!member.Alive)
                PixelFont.DrawText(image, "DOWN", x + 12, y + 130, CriticalColor, TitleScale);
        }

        private static void DrawOpenPanel(Image<Rgba32> image, int x, int y)
        {
            PixelFont.FillRect(image, x, y, PanelWidth, PanelHeight, OpenPanelColor);
            PixelFont.DrawText(image, "OPEN SLOT", x + 12, y + 12, MutedColor, TextScale);
            PixelFont.DrawText(image, "TYPE .DNG JOIN", x + 12, y + 36, MutedColor, TextScale);
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
