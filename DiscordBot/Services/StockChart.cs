using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Services
{
    /// <summary>Draws a price history as a text chart, meant to be sent inside a code block.</summary>
    public static class StockChart
    {
        public const int Width = 60;
        public const int Height = 10;
        private const int _labelWidth = 10;
        private const int _eighthsPerRow = 8;
        //partial block characters, index n fills n + 1 eighths of a row
        private static readonly char[] _partialBlocks = { '▁', '▂', '▃', '▄', '▅', '▆', '▇' };

        /// <summary>Returns <see cref="Height"/> rows of chart followed by an axis line. Expects at least one price.</summary>
        public static string Render(IReadOnlyList<double> prices)
        {
            var columns = Downsample(prices, Width);
            double min = columns.Min();
            double max = columns.Max();
            double range = max - min;
            int levels = Height * _eighthsPerRow;

            var sb = new StringBuilder();
            for (int row = Height - 1; row >= 0; row--)
            {
                string label = row == Height - 1 ? FormatPrice(max) : row == 0 ? FormatPrice(min) : "";
                sb.Append(label.PadLeft(_labelWidth)).Append(" ┤");

                int rowBottom = row * _eighthsPerRow;
                foreach (double price in columns)
                {
                    int level = range == 0 ? levels / 2 : (int)Math.Round((price - min) / range * (levels - 1));
                    int filled = level - rowBottom + 1;
                    sb.Append(filled >= _eighthsPerRow ? '█' : filled >= 1 ? _partialBlocks[filled - 1] : ' ');
                }
                sb.Append('\n');
            }

            sb.Append(new string(' ', _labelWidth)).Append(" └").Append(new string('─', columns.Count));
            return sb.ToString();
        }

        /// <summary>Picks evenly spaced prices when there are more than <paramref name="width"/>.</summary>
        private static List<double> Downsample(IReadOnlyList<double> prices, int width)
        {
            if (prices.Count <= width)
                return prices.ToList();

            var columns = new List<double>(width);
            for (int i = 0; i < width; i++)
                columns.Add(prices[(int)Math.Round(i * (prices.Count - 1) / (double)(width - 1))]);
            return columns;
        }

        private static string FormatPrice(double price) => $"${price:N2}";
    }
}
