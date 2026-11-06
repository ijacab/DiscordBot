using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Generic;

namespace DiscordBot.Services
{
    public enum SpriteKind
    {
        None, Rat, Skeleton, Slime, Bat, Dragon, Golem, Skull,
        Sword, Shield, Amulet, Boots, Ring, Potion
    }

    /// <summary>
    /// Rudimentary hand-drawn pixel art. Each sprite is a 16x16 grid of characters, one per pixel.
    /// '.' is transparent. The other characters map to the shared palette below.
    /// </summary>
    public static class PixelSprites
    {
        public const int Size = 16;

        private static readonly Dictionary<char, Rgba32> Palette = new Dictionary<char, Rgba32>
        {
            ['#'] = new Rgba32(18, 18, 22),
            ['W'] = new Rgba32(235, 235, 230),
            ['G'] = new Rgba32(140, 142, 152),
            ['D'] = new Rgba32(70, 72, 82),
            ['R'] = new Rgba32(220, 52, 52),
            ['O'] = new Rgba32(240, 150, 40),
            ['Y'] = new Rgba32(250, 220, 60),
            ['g'] = new Rgba32(80, 200, 90),
            ['B'] = new Rgba32(70, 130, 230),
            ['P'] = new Rgba32(150, 90, 200),
            ['E'] = new Rgba32(140, 90, 50),
        };

        private static readonly Dictionary<SpriteKind, string[]> Grids = new Dictionary<SpriteKind, string[]>
        {
            [SpriteKind.Rat] = new[]
            {
                "................", "................", "...##...........", "..#GG#..........",
                ".#GGGG#.........", ".#GGGGG####.....", "#GGGGGGGGGGG#...", "#GGW#GGGGGGG#...",
                "#GGGGGGGGGGGG#..", ".#GGGGGGGGGG#...", "..#GGGGGGGG#....", "...#GGGGGG#.....",
                "....##..##......", "................", "...........#....", "............#...",
            },
            [SpriteKind.Skeleton] = new[]
            {
                "....WWWWWW......", "...WWWWWWWW.....", "...W##WW##W.....", "...WWWWWWWW.....",
                "....WWWWWW......", ".....W##W.......", "....WWWWWW......", "..WWWWWWWWWW....",
                "..W.WWWWWW.W....", "....WWWWWW......", ".....W.W.W......", ".....W.W.W......",
                "....WW..WW......", "................", "................", "................",
            },
            [SpriteKind.Slime] = new[]
            {
                "................", "................", "................", "................",
                ".....ggggg......", "...ggggggggg....", "..ggg##ggggg....", "..ggggggggggg...",
                ".gggggggggggggg.", "ggggggggggggggg.", "gggggggggggggggg", "ggggggggggggggg.",
                "#gggggggggggggg#", "................", "................", "................",
            },
            [SpriteKind.Bat] = new[]
            {
                "................", "..##........##..", ".####......####.", ".#####.##.#####.",
                "..###########...", "...###.##.###...", "....##.##.##....", ".....#.##.#.....",
                "......#..#......", "................", "................", "................",
                "................", "................", "................", "................",
            },
            [SpriteKind.Dragon] = new[]
            {
                "................", "..#.........#...", ".#O#.......#O#..", ".#OO#.....#OO#..",
                "#OOOO#####OOOO#.", "#OOOOOOOOOOOOO#.", "#OYO#RRRRRR#OYO#", "#OOOORRRRRRRROO#",
                ".#OOOOOOOOOOOO#.", "..#RRRRRRRRRR#..", "...RRRRRRRRRR...", "....RR.RR.RR....",
                "................", "................", "................", "................",
            },
            [SpriteKind.Golem] = new[]
            {
                "................", "....GGGGGGGG....", "...GGGGGGGGGG...", "...GYGGGGGGYG...",
                "...GGGGGGGGGG...", "...GGDGGGGDGG...", "...GGGGGGGGGG...", "..GGGGGGGGGGGG..",
                ".GGGGGGGGGGGGGG.", ".GGGGGDDGGGGGG..", ".GGGGGGGGGGGGGG.", "..GGG.GG..GG.GG.",
                "...GG.GG..GG.GG.", "..GGG....GGG....", "................", "................",
            },
            [SpriteKind.Skull] = new[]
            {
                "....PPP.PPP.....", "...PPPPPPPPP....", "....WWWWWWWW....", "...WWWWWWWWWW...",
                "...WW##WW##WW...", "...WW##WW##WW...", "...WWWWWWWWWW...", "....WWWW.WWWW...",
                ".....WWWWWWW....", "......W.W.W.....", "......WWWWW.....", "................",
                "................", "................", "................", "................",
            },
            [SpriteKind.Sword] = new[]
            {
                "................", "................", ".............GGG", "............GGG.",
                "...........GGG..", "..YY.....GGG....", "...YY..GGG......", "....YYGGG.......",
                ".....EEY........", "....EE.Y........", "...EE...........", "..EE............",
                "................", "................", "................", "................",
            },
            [SpriteKind.Shield] = new[]
            {
                "................", "..BBBBBBBBBBBB..", "..BBBBBBBBBBBB..", "..BBBBYYYYBBBB..",
                "..BBBBYYYYBBBB..", "..BBBBBBBBBBBB..", "..BBBBBBBBBBBB..", "...BBBBBBBBBB...",
                "....BBBBBBBB....", ".....BBBBBB.....", "......BBBB......", ".......BB.......",
                "................", "................", "................", "................",
            },
            [SpriteKind.Amulet] = new[]
            {
                "...G..........G.", "...G..........G.", "....G........G..", ".....G......G...",
                "......G....G....", ".......GGGG.....", "........GG......", "......PPPPPP....",
                ".....PPWPPPP....", ".....PPPPPPP....", "......PPPPP.....", ".......PPP......",
                "................", "................", "................", "................",
            },
            [SpriteKind.Boots] = new[]
            {
                "................", "....EEEE........", "....EEEE........", "....EEEE........",
                "....EEEEEE......", "....EEEEEEEE....", "....EEEEEEEE....", "...EEEEEEEEEE...",
                "................", "................", "................", "................",
                "................", "................", "................", "................",
            },
            [SpriteKind.Ring] = new[]
            {
                "................", "......BBBB......", ".....BWBBBB.....", "......BBBB......",
                "......YYYY......", ".....Y....Y.....", "....Y......Y....", "....Y......Y....",
                "....Y......Y....", ".....Y....Y.....", "......YYYY......", "................",
                "................", "................", "................", "................",
            },
            [SpriteKind.Potion] = new[]
            {
                "......WWWW......", "......WWWW......", ".....BBBBBB.....", "....BBRRRRBB....",
                "....BBRRRRBB....", "...BBRRRRRRBB...", "...BBRRRRRRBB...", "...BBRRRRRRBB...",
                "...BBRRRRRRBB...", "....BBBBBBBB....", "................", "................",
                "................", "................", "................", "................",
            },
        };

        /// <summary>Draws a sprite onto the image at the given top-left corner, scaled up by <paramref name="scale"/>.</summary>
        public static void Draw(Image<Rgba32> image, SpriteKind kind, int x, int y, int scale)
        {
            if (kind == SpriteKind.None || !Grids.TryGetValue(kind, out string[] rows))
                return;

            for (int row = 0; row < Size; row++)
            {
                string line = row < rows.Length ? rows[row] : "";
                for (int col = 0; col < Size; col++)
                {
                    char pixel = col < line.Length ? line[col] : '.';
                    if (pixel == '.' || !Palette.TryGetValue(pixel, out Rgba32 color))
                        continue;
                    PixelFontFill(image, x + col * scale, y + row * scale, scale, color);
                }
            }
        }

        private static void PixelFontFill(Image<Rgba32> image, int x, int y, int size, Rgba32 color)
        {
            PixelFont.FillRect(image, x, y, size, size, color);
        }

        /// <summary>Number of grid rows in a sprite, for tests.</summary>
        public static int RowCount(SpriteKind kind) => Grids.TryGetValue(kind, out string[] rows) ? rows.Length : 0;

        /// <summary>The grid rows of a sprite, for tests.</summary>
        public static IReadOnlyList<string> Rows(SpriteKind kind) => Grids.TryGetValue(kind, out string[] rows) ? rows : new string[0];
    }
}
