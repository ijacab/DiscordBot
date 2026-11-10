using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;

namespace DiscordBot.Services
{
    /// <summary>One player's line in the scene. Built by the manager, so the renderer doesn't depend on the run model.</summary>
    public class ScenePlayer
    {
        public string Name { get; set; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Floor { get; set; }
        public double Haul { get; set; }
        /// <summary>Optional note such as DOWN, CASHED OUT or PVP.</summary>
        public string Status { get; set; }
        /// <summary>Floors done in the current block, 0 to 10, drawn as pips. -1 hides the pips (players who left).</summary>
        public int FloorInBlock { get; set; } = -1;
        /// <summary>True while the run waits at a boss door, which lights up the boss pip.</summary>
        public bool AtBossDoor { get; set; }
        public int Potions { get; set; }
    }

    /// <summary>What the dashboard picture shows: the title, the current enemy or room, and every player.</summary>
    public class DungeonScene
    {
        public string Title { get; set; }
        public string Caption { get; set; }
        public SpriteKind Sprite { get; set; } = SpriteKind.None;
        public int? EnemyHp { get; set; }
        public int? EnemyMaxHp { get; set; }
        public List<ScenePlayer> Players { get; } = new List<ScenePlayer>();
        public string Footer { get; set; }
    }

    /// <summary>Draws the dashboard picture: a title bar, the current enemy with its HP, and one panel per player.</summary>
    public static class DungeonSceneRenderer
    {
        public const int Width = 800;
        public const int Height = 420;
        public const int MaxPlayers = 4;

        private const int TitleHeight = 44;
        private const int PlayersTop = 268;
        private const int PanelHeight = 116;
        private const int Pips = 10;
        private const int PipGap = 3;
        private const int PanelGap = 8;
        private const int PanelLeft = 16;
        private const int PanelWidth = (Width - 2 * PanelLeft - (MaxPlayers - 1) * PanelGap) / MaxPlayers;
        private const int SpriteScale = 9;

        private static readonly Rgba32 Background = new Rgba32(24, 25, 30);
        private static readonly Rgba32 TitleColor = new Rgba32(44, 46, 52);
        private static readonly Rgba32 FloorColor = new Rgba32(34, 36, 42);
        private static readonly Rgba32 PanelColor = new Rgba32(40, 42, 48);
        private static readonly Rgba32 DownPanelColor = new Rgba32(26, 27, 30);
        private static readonly Rgba32 TrackColor = new Rgba32(58, 60, 66);
        private static readonly Rgba32 HealthyColor = new Rgba32(87, 242, 135);
        private static readonly Rgba32 HurtColor = new Rgba32(250, 166, 26);
        private static readonly Rgba32 CriticalColor = new Rgba32(237, 66, 69);
        private static readonly Rgba32 TextColor = new Rgba32(181, 186, 193);
        private static readonly Rgba32 BrightColor = new Rgba32(255, 255, 255);
        private static readonly Rgba32 MutedColor = new Rgba32(110, 114, 122);
        private static readonly Rgba32 GoldColor = new Rgba32(250, 220, 60);
        private static readonly Rgba32 PipDoneColor = new Rgba32(88, 101, 242);
        private static readonly Rgba32 BossPipColor = new Rgba32(120, 40, 44);

        public static MemoryStream Render(DungeonScene scene)
        {
            using var image = new Image<Rgba32>(Width, Height);
            PixelFont.FillRect(image, 0, 0, Width, Height, Background);
            PixelFont.FillRect(image, 0, TitleHeight, Width, PlayersTop - TitleHeight, FloorColor);
            PixelFont.FillRect(image, 0, 0, Width, TitleHeight, TitleColor);

            PixelFont.DrawText(image, Clip(scene.Title, 26).ToUpperInvariant(), 16, (TitleHeight - PixelFont.GlyphHeight * 3) / 2, BrightColor, 3);
            DrawEnemy(image, scene);

            for (int i = 0; i < Math.Min(scene.Players.Count, MaxPlayers); i++)
                DrawPlayer(image, scene.Players[i], PanelLeft + i * (PanelWidth + PanelGap), PlayersTop);

            if (!string.IsNullOrEmpty(scene.Footer))
                PixelFont.DrawText(image, Clip(scene.Footer.ToUpperInvariant(), 62), PanelLeft, Height - 24, TextColor, 2);

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
        }

        private static void DrawEnemy(Image<Rgba32> image, DungeonScene scene)
        {
            int spriteSize = PixelSprites.Size * SpriteScale;
            int centreX = Width / 2;

            if (scene.Sprite == SpriteKind.None)
            {
                string caption = Clip((scene.Caption ?? "").ToUpperInvariant(), 20);
                int x = centreX - PixelFont.TextWidth(caption, 4) / 2;
                PixelFont.DrawText(image, caption, Math.Max(16, x), 110, BrightColor, 4);
                return;
            }

            int spriteX = centreX - spriteSize / 2;
            int spriteY = TitleHeight + 8;
            PixelSprites.Draw(image, scene.Sprite, spriteX, spriteY, SpriteScale);

            string caption2 = Clip((scene.Caption ?? "").ToUpperInvariant(), 28);
            PixelFont.DrawText(image, caption2, centreX - PixelFont.TextWidth(caption2, 2) / 2, spriteY + spriteSize + 6, BrightColor, 2);

            if (scene.EnemyHp.HasValue && scene.EnemyMaxHp.HasValue && scene.EnemyMaxHp > 0)
            {
                int barWidth = 320;
                int barX = centreX - barWidth / 2;
                int barY = spriteY + spriteSize + 26;
                double fraction = Math.Clamp((double)Math.Max(0, scene.EnemyHp.Value) / scene.EnemyMaxHp.Value, 0, 1);
                PixelFont.FillRect(image, barX, barY, barWidth, 12, TrackColor);
                PixelFont.FillRect(image, barX, barY, (int)Math.Round(barWidth * fraction), 12, HealthColor(fraction));
            }
        }

        private static void DrawPlayer(Image<Rgba32> image, ScenePlayer player, int x, int y)
        {
            bool down = !string.IsNullOrEmpty(player.Status) && player.Status == "DOWN";
            PixelFont.FillRect(image, x, y, PanelWidth, PanelHeight, down ? DownPanelColor : PanelColor);

            PixelFont.DrawText(image, Clip(player.Name.ToUpperInvariant(), 14), x + 8, y + 8, down ? MutedColor : BrightColor, 2);

            int barWidth = PanelWidth - 16;
            double fraction = player.MaxHp <= 0 ? 0 : Math.Clamp((double)Math.Max(0, player.Hp) / player.MaxHp, 0, 1);
            PixelFont.FillRect(image, x + 8, y + 28, barWidth, 12, TrackColor);
            if (!down && fraction > 0)
                PixelFont.FillRect(image, x + 8, y + 28, (int)Math.Round(barWidth * fraction), 12, HealthColor(fraction));

            PixelFont.DrawText(image, $"HP {Math.Max(0, player.Hp)} OF {player.MaxHp}", x + 8, y + 44, TextColor, 2);
            if (player.FloorInBlock >= 0)
                DrawPips(image, player, x + 8, y + 62, barWidth);

            string status = string.IsNullOrEmpty(player.Status) ? $"FLOOR {player.Floor}" : player.Status;
            Rgba32 statusColor = down ? CriticalColor : player.AtBossDoor ? CriticalColor : TextColor;
            PixelFont.DrawText(image, Clip(status, 14), x + 8, y + 76, statusColor, 2);
            PixelFont.DrawText(image, $"${player.Haul:N0}", x + 8, y + 94, GoldColor, 2);

            if (player.Potions > 0)
            {
                string count = player.Potions.ToString();
                int countX = x + PanelWidth - 8 - PixelFont.TextWidth(count, 2);
                PixelFont.DrawText(image, count, countX, y + 94, BrightColor, 2);
                PixelSprites.Draw(image, SpriteKind.Potion, countX - PixelSprites.Size - 2, y + 92, 1);
            }
        }

        /// <summary>Ten pips for the floors of the block: done floors filled, bosses (5 and 10) in red, the boss being faced lit.</summary>
        private static void DrawPips(Image<Rgba32> image, ScenePlayer player, int x, int y, int width)
        {
            int pipWidth = (width - (Pips - 1) * PipGap) / Pips;
            for (int i = 0; i < Pips; i++)
            {
                int floor = i + 1;
                bool boss = floor % 5 == 0;
                Rgba32 color = floor <= player.FloorInBlock ? PipDoneColor : boss ? BossPipColor : TrackColor;
                if (player.AtBossDoor && floor == player.FloorInBlock + 1)
                    color = CriticalColor;
                PixelFont.FillRect(image, x + i * (pipWidth + PipGap), y, pipWidth, 8, color);
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
