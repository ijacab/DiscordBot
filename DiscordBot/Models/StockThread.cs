using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    /// <summary>The live stock ticker threads opened with <c>.stocksthread</c>, saved in stockthreads.json. One per server.</summary>
    public class StockThreads
    {
        public List<LiveStockThread> Threads { get; set; } = new List<LiveStockThread>();
    }

    public class LiveStockThread
    {
        public ulong GuildId { get; set; }
        /// <summary>The channel the thread was opened in.</summary>
        public ulong ParentChannelId { get; set; }
        public ulong ThreadId { get; set; }
        /// <summary>The first message, holding the last-update time.</summary>
        public ulong HeaderMessageId { get; set; }
        /// <summary>The ticker messages in thread order: every stock's all-time ticker, then every stock's last-3-hours ticker.</summary>
        public List<TickerMessage> Tickers { get; set; } = new List<TickerMessage>();
        /// <summary>The stock tick the tickers last showed (<see cref="StockMarket.LastTickSlot"/>).</summary>
        public long LastSlot { get; set; }

        /// <summary>The layout the tickers should have for these symbols, in thread order.</summary>
        public static List<(string Symbol, bool LastHour)> LayoutFor(IReadOnlyList<string> orderedSymbols) =>
            orderedSymbols.Select(s => (s, false)).Concat(orderedSymbols.Select(s => (s, true))).ToList();

        /// <summary>
        /// True when the saved tickers aren't exactly the layout for these symbols (a stock was added, removed or reordered,
        /// for example after the market was reset), so they have to be posted again.
        /// </summary>
        public bool NeedsRepost(IReadOnlyList<string> orderedSymbols)
        {
            var layout = LayoutFor(orderedSymbols);
            if (layout.Count != Tickers.Count) return true;
            for (int i = 0; i < layout.Count; i++)
                if (layout[i].Symbol != Tickers[i].Symbol || layout[i].LastHour != Tickers[i].LastHour)
                    return true;
            return false;
        }
    }

    public class TickerMessage
    {
        public string Symbol { get; set; }
        /// <summary>True for the recent ticker (the last 3 hours, <see cref="Services.StockThreadService.RecentWindow"/>), false for the all-time one. Named from when it was 1 hour; kept so saved threads load.</summary>
        public bool LastHour { get; set; }
        public ulong MessageId { get; set; }
    }
}
