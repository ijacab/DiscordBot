using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Services
{
    /// <summary>Draws a price history as an ASCII chart, meant to be sent inside a code block.</summary>
    public static class StockChart
    {
        public const int Width = 32;
        public const int Height = 10;
        private const int _labelWidth = 10;

        /// <summary>Returns <see cref="Height"/> rows of chart followed by an axis line. Expects at least one price.</summary>
        public static string Render(IReadOnlyList<double> prices)
        {
            var columns = Downsample(prices, Width);
            double min = columns.Min(c => c.Low);
            double max = columns.Max(c => c.High);
            double range = max - min;

            var sb = new StringBuilder();
            for (int row = Height - 1; row >= 0; row--)
            {
                string label = row == Height - 1 ? FormatPrice(max) : row == 0 ? FormatPrice(min) : "";
                sb.Append(label.PadLeft(_labelWidth)).Append(" |");

                foreach (var column in columns)
                {
                    int low = Level(column.Low, min, range);
                    int high = Level(column.High, min, range);
                    //each column is a bar from its lowest to its highest price, so spikes stay visible
                    sb.Append(row >= low && row <= high ? '#' : ' ');
                }
                sb.Append('\n');
            }

            sb.Append(new string(' ', _labelWidth)).Append(" +").Append(new string('-', columns.Count));
            return sb.ToString();
        }

        private static int Level(double price, double min, double range) =>
            range == 0 ? Height / 2 : (int)Math.Round((price - min) / range * (Height - 1));

        /// <summary>Splits the prices into at most <paramref name="width"/> columns, each holding the low and high of its span.</summary>
        private static List<(double Low, double High)> Downsample(IReadOnlyList<double> prices, int width)
        {
            if (prices.Count <= width)
                return prices.Select(p => (Low: p, High: p)).ToList();

            var columns = new List<(double Low, double High)>(width);
            for (int i = 0; i < width; i++)
            {
                int start = i * prices.Count / width;
                int end = Math.Max(start, (i + 1) * prices.Count / width - 1);
                double low = double.MaxValue, high = double.MinValue;
                for (int j = start; j <= end; j++)
                {
                    low = Math.Min(low, prices[j]);
                    high = Math.Max(high, prices[j]);
                }
                columns.Add((low, high));
            }
            return columns;
        }

        private static string FormatPrice(double price) => $"${price:N2}";
    }
}
