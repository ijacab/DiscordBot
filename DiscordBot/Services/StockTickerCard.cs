using DiscordBot.Models;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiscordBot.Services
{
    /// <summary>
    /// The graph and stats for one stock over a window: used by <c>.stocks SYMBOL 48h</c> and by the live tickers in
    /// <c>.stocksthread</c>. Pure, so it can be tested without Discord.
    /// </summary>
    public static class StockTickerCard
    {
        /// <summary>Time between price points: one per stock tick.</summary>
        public static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

        public const string StocksFooter = "*Each point is one 5 minute price tick, newest on the right. Add a time like `48h` to change the window.*";

        public sealed class Card
        {
            public string Title { get; set; }
            public string Text { get; set; }
            /// <summary>The chart, or null when there are fewer than two price points to draw.</summary>
            public byte[] Png { get; set; }
            /// <summary>The prices drawn, oldest first.</summary>
            public IReadOnlyList<double> Points { get; set; }
        }

        /// <summary>
        /// The most recent stretch of <paramref name="history"/> covering <paramref name="window"/> (one point per tick plus
        /// the starting point), or all of it when the window is null or longer than the history.
        /// </summary>
        public static IReadOnlyList<double> Window(IReadOnlyList<double> history, TimeSpan? window)
        {
            if (!window.HasValue) return history;
            int points = (int)Math.Max(2, window.Value.Ticks / Tick.Ticks + 1);
            return points < history.Count ? history.Skip(history.Count - points).ToList() : history;
        }

        /// <summary>% change from the first point to the last.</summary>
        public static double ChangeOf(IReadOnlyList<double> points)
        {
            if (points.Count == 0) return 0;
            double first = points[0];
            return first <= 0 ? 0 : (points[points.Count - 1] - first) / first * 100;
        }

        /// <summary>
        /// Green or red. By the change over the drawn points when <paramref name="colourByWindow"/> (the live last-hour
        /// ticker), otherwise by the stock's all-time change, as <c>.stocks</c> does.
        /// </summary>
        public static Rgba32 LineColourFor(Stock stock, IReadOnlyList<double> points, bool colourByWindow) =>
            StockChart.ColourForChange(colourByWindow ? ChangeOf(points) : stock.AllTimeChange);

        public static Card Build(Stock stock, TimeSpan? window, bool colourByWindow = false, string footer = StocksFooter)
        {
            IReadOnlyList<double> history = stock.History;
            string title = $"{stock.Symbol} price history";
            if (history.Count < 2)
                return new Card { Title = title, Text = $"{NameLine(stock)}\nNot enough price history yet. Check back after a few ticks.", Points = history };

            IReadOnlyList<double> points = Window(history, window);
            string windowNote = "";
            if (window.HasValue && window.Value.Ticks / Tick.Ticks + 1 > history.Count)
                windowNote = $" (only {StockChart.FormatSpan(history.Count, Tick)} of history so far)";

            double windowChange = ChangeOf(points);
            string colourNote = colourByWindow
                ? $"Line is {(windowChange >= 0 ? "green" : "red")} for this window."
                : $"Line is {(stock.AllTimeChange >= 0 ? "green" : "red")} for that.";
            string text = $"{NameLine(stock)}\n" +
                $"{windowChange:+0.00;-0.00;0.00}% over the last {StockChart.FormatSpan(points.Count, Tick)}{windowNote} | low ${points.Min():N2} | high ${points.Max():N2}\n" +
                $"All time {stock.AllTimeChange:+0.00;-0.00;0.00}% (launched at ${stock.BasePrice:N2}). {colourNote}\n" +
                footer;

            using MemoryStream chart = StockChart.RenderPng(points, Tick, LineColourFor(stock, points, colourByWindow));
            return new Card { Title = title, Text = text, Png = chart.ToArray(), Points = points };
        }

        private static string NameLine(Stock stock)
        {
            string name = stock.TypeEmoji == null ? stock.Name : $"{stock.TypeEmoji} {stock.Name}";
            return $"{name} ({stock.KindLabel}): ${stock.Price:N2}";
        }

        /// <summary>The order <c>.stocks</c> lists them in: ordinary stocks by tier then symbol, then the safe havens.</summary>
        public static List<Stock> Ordered(IEnumerable<Stock> stocks)
        {
            var list = stocks.ToList();
            return list.Where(s => s.Role == StockRole.Normal).OrderBy(s => s.RiskTier).ThenBy(s => s.Symbol)
                .Concat(list.Where(s => s.Role != StockRole.Normal))
                .ToList();
        }
    }
}
