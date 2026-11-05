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

        //margins around the plot area, leaving room for the labels. The left margin is sized to the widest price label.
        private const int _labelEdgePadding = 8;
        private const int _labelGap = 12;
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

        /// <summary>
        /// Renders a series of values (oldest first) as a PNG. <paramref name="pointInterval"/> is the time between points,
        /// used for the time labels. Expects at least two values.
        /// </summary>
        public static MemoryStream RenderPng(IReadOnlyList<double> prices, TimeSpan pointInterval)
        {
            if (prices.Count < 2) throw new ArgumentException("A chart needs at least two prices.", nameof(prices));

            double min = prices.Min();
            double max = prices.Max();
            double padding = (max - min) * 0.08;
            if (padding == 0) padding = Math.Max(max * 0.05, 0.01); //flat prices still get a sensible scale
            double low = min - padding;
            double high = max + padding;

            //work out the price labels first, so the left margin can fit the widest one
            var gridValues = new double[_gridLines + 1];
            var gridLabels = new string[_gridLines + 1];
            int widestLabel = 0;
            for (int i = 0; i <= _gridLines; i++)
            {
                gridValues[i] = low + (high - low) * i / _gridLines;
                gridLabels[i] = FormatPrice(gridValues[i]);
                widestLabel = Math.Max(widestLabel, TextWidth(gridLabels[i]));
            }
            int plotLeft = _labelEdgePadding + widestLabel + _labelGap;

            int XOf(int index) => plotLeft + (int)Math.Round((double)index * (_plotRight - plotLeft) / (prices.Count - 1));
            int YOf(double price) => (int)Math.Round(_plotBottom - (price - low) / (high - low) * (_plotBottom - _plotTop));

            using var image = new Image<Rgba32>(Width, Height);
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    image[x, y] = _background;

            //horizontal grid lines, each labelled with its price
            for (int i = 0; i <= _gridLines; i++)
            {
                int y = YOf(gridValues[i]);
                for (int x = plotLeft; x <= _plotRight; x++)
                    SetPixel(image, x, y, _gridColor);

                string label = gridLabels[i];
                DrawText(image, label, plotLeft - _labelGap - TextWidth(label), y - _glyphHeight * _textScale / 2, _textColor);
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
            DrawLabel(image, highLabel, highX - TextWidth(highLabel) / 2, highTop, plotLeft);

            int lowX = XOf(lowIndex), lowY = YOf(prices[lowIndex]);
            FillCircle(image, lowX, lowY, 3, _markerColor);
            string lowLabel = PriceWithChange(prices[lowIndex], current);
            int lowTop = lowY + 10;
            if (lowTop + LabelHeight + LabelPadding > _plotBottom + 10) lowTop = lowY - 12 - LabelHeight; //would run into the time labels, so put it above the point
            DrawLabel(image, lowLabel, lowX - TextWidth(lowLabel) / 2, lowTop, plotLeft);

            //current value, drawn last so it sits on top of the line
            int lastX = XOf(prices.Count - 1);
            int lastY = YOf(current);
            FillCircle(image, lastX, lastY, 5, _markerColor);
            FillCircle(image, lastX, lastY, 3, lineColor);
            string currentLabel = $"now {FormatPrice(current)}";
            DrawLabel(image, currentLabel, lastX - 10 - TextWidth(currentLabel), lastY - LabelHeight / 2, plotLeft);

            //time labels under the plot, oldest on the left
            int labelY = _plotBottom + 14;
            DrawText(image, "-" + FormatSpan(prices.Count, pointInterval), plotLeft, labelY, _textColor);
            DrawText(image, "now", _plotRight - TextWidth("now"), labelY, _textColor);

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
        }

        /// <summary>
        /// Colours for the lines in <see cref="RenderMultiPng"/>, in order. The .nw all legend uses matching coloured
        /// circles in the same order, so keep the two in step.
        /// </summary>
        public static readonly Rgba32[] SeriesColors =
        {
            new Rgba32(237, 66, 69),   //red
            new Rgba32(250, 166, 26),  //orange
            new Rgba32(254, 231, 92),  //yellow
            new Rgba32(87, 242, 135),  //green
            new Rgba32(88, 101, 242),  //blue
            new Rgba32(155, 89, 182),  //purple
            new Rgba32(255, 255, 255), //white
            new Rgba32(170, 110, 60),  //brown
        };

        /// <summary>
        /// Renders several series (oldest first) on one chart, one line per series in <see cref="SeriesColors"/>
        /// (wrapping after the last colour). Series are lined up by their newest point, so a shorter series starts
        /// later on the x axis. Expects every series to have at least one value and the longest to have two.
        /// </summary>
        public static MemoryStream RenderMultiPng(IReadOnlyList<IReadOnlyList<double>> series, TimeSpan pointInterval, Func<double, string> formatLabel = null)
        {
            formatLabel ??= v => FormatMoney(v);
            if (series.Count == 0) throw new ArgumentException("A chart needs at least one series.", nameof(series));
            if (series.Any(s => s.Count == 0)) throw new ArgumentException("Every series needs at least one value.", nameof(series));
            int longest = series.Max(s => s.Count);
            if (longest < 2) throw new ArgumentException("A chart needs at least two points.", nameof(series));

            double min = series.Min(s => s.Min());
            double max = series.Max(s => s.Max());
            double padding = (max - min) * 0.08;
            if (padding == 0) padding = Math.Max(Math.Abs(max) * 0.05, 0.01); //flat values still get a sensible scale
            double low = min - padding;
            double high = max + padding;

            var gridValues = new double[_gridLines + 1];
            var gridLabels = new string[_gridLines + 1];
            int widestLabel = 0;
            for (int i = 0; i <= _gridLines; i++)
            {
                gridValues[i] = low + (high - low) * i / _gridLines;
                gridLabels[i] = formatLabel(gridValues[i]);
                widestLabel = Math.Max(widestLabel, TextWidth(gridLabels[i]));
            }
            int plotLeft = _labelEdgePadding + widestLabel + _labelGap;

            //index counts from the start of the longest series, so the newest points all line up on the right
            int XOf(int index) => plotLeft + (int)Math.Round((double)index * (_plotRight - plotLeft) / (longest - 1));
            int YOf(double value) => (int)Math.Round(_plotBottom - (value - low) / (high - low) * (_plotBottom - _plotTop));

            using var image = new Image<Rgba32>(Width, Height);
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    image[x, y] = _background;

            for (int i = 0; i <= _gridLines; i++)
            {
                int y = YOf(gridValues[i]);
                for (int x = plotLeft; x <= _plotRight; x++)
                    SetPixel(image, x, y, _gridColor);

                string label = gridLabels[i];
                DrawText(image, label, plotLeft - _labelGap - TextWidth(label), y - _glyphHeight * _textScale / 2, _textColor);
            }

            for (int s = 0; s < series.Count; s++)
            {
                IReadOnlyList<double> values = series[s];
                Rgba32 color = SeriesColors[s % SeriesColors.Length];
                int offset = longest - values.Count; //a shorter series has no points before its first value
                for (int i = 1; i < values.Count; i++)
                    DrawLine(image, XOf(offset + i - 1), YOf(values[i - 1]), XOf(offset + i), YOf(values[i]), color);
            }

            //a dot at each line's newest value
            for (int s = 0; s < series.Count; s++)
            {
                IReadOnlyList<double> values = series[s];
                FillCircle(image, XOf(longest - 1), YOf(values[values.Count - 1]), 4, SeriesColors[s % SeriesColors.Length]);
            }

            int labelY = _plotBottom + 14;
            DrawText(image, "-" + FormatSpan(longest, pointInterval), plotLeft, labelY, _textColor);
            DrawText(image, "now", _plotRight - TextWidth("now"), labelY, _textColor);

            var stream = new MemoryStream();
            image.SaveAsPng(stream);
            stream.Position = 0;
            return stream;
        }

        private static string FormatMoney(double value) => $"${value:N0}";

        /// <summary>How long the series covers, given one point per <paramref name="pointInterval"/>. Example: "6d 22h" or "1h 0m".</summary>
        public static string FormatSpan(int pointCount, TimeSpan pointInterval)
        {
            TimeSpan span = TimeSpan.FromTicks(pointInterval.Ticks * Math.Max(0, pointCount - 1));
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

        /// <summary>
        /// Draws a label as plain text (no box, so it never hides part of the line). Kept inside the plot area so it
        /// doesn't cover the price scale or time labels.
        /// </summary>
        private static void DrawLabel(Image<Rgba32> image, string text, int x, int top, int plotLeft)
        {
            int width = TextWidth(text);
            x = Math.Clamp(x, plotLeft, _plotRight - width);
            top = Math.Clamp(top, _plotTop, _plotBottom - LabelHeight);
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
