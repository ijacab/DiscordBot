using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiscordBot.Services
{
    /// <summary>Draws a price history as a PNG line chart with a price scale and time labels.</summary>
    public static class StockChart
    {
        public const int Width = 800;
        public const int Height = 420;

        //margins around the plot area, leaving room for the labels
        private const int _plotLeft = 120;
        private const int _plotRight = Width - 30;
        private const int _plotTop = 30;
        private const int _plotBottom = Height - 60;
        private const int _gridLines = 4;
        private const int _textScale = 2;
        private const int _glyphWidth = 5;
        private const int _glyphHeight = 7;
        private const int LabelHeight = _glyphHeight * _textScale;
        private const int LabelPadding = 4;

        private static readonly Rgba32 _background = new Rgba32(30, 31, 34);
        private static readonly Rgba32 _gridColor = new Rgba32(58, 60, 66);
        private static readonly Rgba32 _textColor = new Rgba32(181, 186, 193);
        private static readonly Rgba32 _upColor = new Rgba32(87, 242, 135);
        private static readonly Rgba32 _downColor = new Rgba32(237, 66, 69);
        private static readonly Rgba32 _markerColor = new Rgba32(255, 255, 255);

        /// <summary>Renders the prices (oldest first, one per 5 minute tick) as a PNG. Expects at least two prices.</summary>
        public static MemoryStream RenderPng(IReadOnlyList<double> prices)
        {
            if (prices.Count < 2) throw new ArgumentException("A chart needs at least two prices.", nameof(prices));

            double min = prices.Min();
            double max = prices.Max();
            double padding = (max - min) * 0.08;
            if (padding == 0) padding = Math.Max(max * 0.05, 0.01); //flat prices still get a sensible scale
            double low = min - padding;
            double high = max + padding;

            int XOf(int index) => _plotLeft + (int)Math.Round((double)index * (_plotRight - _plotLeft) / (prices.Count - 1));
            int YOf(double price) => (int)Math.Round(_plotBottom - (price - low) / (high - low) * (_plotBottom - _plotTop));

            using var image = new Image<Rgba32>(Width, Height);
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    image[x, y] = _background;

            //horizontal grid lines, each labelled with its price
            for (int i = 0; i <= _gridLines; i++)
            {
                double value = low + (high - low) * i / _gridLines;
                int y = YOf(value);
                for (int x = _plotLeft; x <= _plotRight; x++)
                    SetPixel(image, x, y, _gridColor);

                string label = FormatPrice(value);
                DrawText(image, label, _plotLeft - 12 - TextWidth(label), y - _glyphHeight * _textScale / 2, _textColor);
            }

            //the line goes green if the stock finished above where it started, red if below
            Rgba32 lineColor = prices[prices.Count - 1] >= prices[0] ? _upColor : _downColor;
            for (int i = 1; i < prices.Count; i++)
                DrawLine(image, XOf(i - 1), YOf(prices[i - 1]), XOf(i), YOf(prices[i]), lineColor);

            //highest and lowest points, each labelled with its price and % difference from the current price
            double current = prices[prices.Count - 1];
            int highIndex = IndexOfExtreme(prices, highest: true);
            int lowIndex = IndexOfExtreme(prices, highest: false);

            int highX = XOf(highIndex), highY = YOf(prices[highIndex]);
            FillCircle(image, highX, highY, 3, _markerColor);
            string highLabel = PriceWithChange(prices[highIndex], current);
            int highTop = highY - 12 - LabelHeight;
            if (highTop < 2) highTop = highY + 10; //no room above, so put it below the point
            DrawLabel(image, highLabel, highX - TextWidth(highLabel) / 2, highTop);

            int lowX = XOf(lowIndex), lowY = YOf(prices[lowIndex]);
            FillCircle(image, lowX, lowY, 3, _markerColor);
            string lowLabel = PriceWithChange(prices[lowIndex], current);
            int lowTop = lowY + 10;
            if (lowTop + LabelHeight + LabelPadding > _plotBottom + 10) lowTop = lowY - 12 - LabelHeight; //would run into the time labels, so put it above the point
            DrawLabel(image, lowLabel, lowX - TextWidth(lowLabel) / 2, lowTop);

            //current value, drawn last so it sits on top of the line
            int lastX = XOf(prices.Count - 1);
            int lastY = YOf(current);
            FillCircle(image, lastX, lastY, 5, _markerColor);
            FillCircle(image, lastX, lastY, 3, lineColor);
            string currentLabel = $"now {FormatPrice(current)}";
            DrawLabel(image, currentLabel, lastX - 10 - TextWidth(currentLabel), lastY - LabelHeight / 2);

            //time labels under the plot, oldest on the left
            int labelY = _plotBottom + 14;
            DrawText(image, "-" + FormatSpan(prices.Count), _plotLeft, labelY, _textColor);
            DrawText(image, "now", _plotRight - TextWidth("now"), labelY, _textColor);

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
        }

        /// <summary>How long the history covers, given one point per 5 minute tick. Example: "6d 22h" or "1h 0m".</summary>
        public static string FormatSpan(int pointCount)
        {
            TimeSpan span = TimeSpan.FromMinutes(5 * Math.Max(0, pointCount - 1));
            return span.TotalHours >= 24 ? $"{(int)span.TotalDays}d {span.Hours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        private static string FormatPrice(double price) => $"${price:N2}";

        /// <summary>Price followed by its % difference from the current price, e.g. "$12.34 -3.10%".</summary>
        private static string PriceWithChange(double price, double current) =>
            $"{FormatPrice(price)} {(price - current) / current * 100:+0.00;-0.00;0.00}%";

        private static int IndexOfExtreme(IReadOnlyList<double> prices, bool highest)
        {
            int index = 0;
            for (int i = 1; i < prices.Count; i++)
                if (highest ? prices[i] > prices[index] : prices[i] < prices[index])
                    index = i;
            return index;
        }

        /// <summary>Draws text on a background-coloured box so it stays readable over the line and grid.</summary>
        private static void DrawLabel(Image<Rgba32> image, string text, int x, int top)
        {
            int width = TextWidth(text);
            //keep the label inside the plot area so it never covers the price scale or time labels
            x = Math.Clamp(x, _plotLeft, _plotRight - width);
            top = Math.Clamp(top, _plotTop, _plotBottom - LabelHeight);

            for (int y = top - LabelPadding; y < top + LabelHeight + LabelPadding; y++)
                for (int px = x - LabelPadding; px < x + width + LabelPadding; px++)
                    SetPixel(image, px, y, _background);
            DrawText(image, text, x, top, _textColor);
        }

        private static void SetPixel(Image<Rgba32> image, int x, int y, Rgba32 color)
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height)
                image[x, y] = color;
        }

        /// <summary>Bresenham line, drawn 2px thick so it stays visible when the image is scaled down in Discord.</summary>
        private static void DrawLine(Image<Rgba32> image, int x0, int y0, int x1, int y1, Rgba32 color)
        {
            int dx = Math.Abs(x1 - x0), stepX = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), stepY = y0 < y1 ? 1 : -1;
            int error = dx + dy;

            while (true)
            {
                SetPixel(image, x0, y0, color);
                SetPixel(image, x0 + 1, y0, color);
                SetPixel(image, x0, y0 + 1, color);
                SetPixel(image, x0 + 1, y0 + 1, color);

                if (x0 == x1 && y0 == y1) break;
                int doubled = 2 * error;
                if (doubled >= dy) { error += dy; x0 += stepX; }
                if (doubled <= dx) { error += dx; y0 += stepY; }
            }
        }

        private static void FillCircle(Image<Rgba32> image, int centerX, int centerY, int radius, Rgba32 color)
        {
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dy * dy <= radius * radius)
                        SetPixel(image, centerX + dx, centerY + dy, color);
        }

        private static int TextWidth(string text) =>
            text.Length == 0 ? 0 : text.Length * (_glyphWidth + 1) * _textScale - _textScale;

        private static void DrawText(Image<Rgba32> image, string text, int x, int y, Rgba32 color)
        {
            int advance = (_glyphWidth + 1) * _textScale;
            for (int i = 0; i < text.Length; i++)
            {
                if (!_glyphs.TryGetValue(text[i], out string[] rows)) continue; //unknown characters are left blank
                for (int row = 0; row < _glyphHeight; row++)
                    for (int col = 0; col < _glyphWidth; col++)
                    {
                        if (rows[row][col] != '#') continue;
                        int blockX = x + i * advance + col * _textScale;
                        int blockY = y + row * _textScale;
                        for (int dy = 0; dy < _textScale; dy++)
                            for (int dx = 0; dx < _textScale; dx++)
                                SetPixel(image, blockX + dx, blockY + dy, color);
                    }
            }
        }

        //5x7 pixel font, '#' is ink. Only the characters the chart labels need.
        private static readonly Dictionary<char, string[]> _glyphs = new Dictionary<char, string[]>
        {
            ['0'] = new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
            ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
            ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
            ['3'] = new[] { "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###." },
            ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
            ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
            ['6'] = new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." },
            ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
            ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
            ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." },
            ['$'] = new[] { "..#..", ".####", "#.#..", ".###.", "..#.#", "####.", "..#.." },
            ['.'] = new[] { ".....", ".....", ".....", ".....", ".....", ".##..", ".##.." },
            [','] = new[] { ".....", ".....", ".....", ".....", ".##..", "..#..", ".#..." },
            ['-'] = new[] { ".....", ".....", ".....", "#####", ".....", ".....", "....." },
            ['+'] = new[] { ".....", "..#..", "..#..", "#####", "..#..", "..#..", "....." },
            ['%'] = new[] { "##..#", "##.#.", "...#.", "..#..", ".#...", ".#.##", "#..##" },
            ['d'] = new[] { "....#", "....#", ".####", "#...#", "#...#", "#...#", ".####" },
            ['h'] = new[] { "#....", "#....", "####.", "#...#", "#...#", "#...#", "#...#" },
            ['m'] = new[] { ".....", ".....", "##.##", "#.#.#", "#.#.#", "#...#", "#...#" },
            ['n'] = new[] { ".....", ".....", "####.", "#...#", "#...#", "#...#", "#...#" },
            ['o'] = new[] { ".....", ".....", ".###.", "#...#", "#...#", "#...#", ".###." },
            ['w'] = new[] { ".....", ".....", "#...#", "#...#", "#.#.#", "#.#.#", ".#.#." },
        };
    }
}
