using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    /// <summary>The live stock ticker threads opened with <c>.stocksthread</c>, saved in stockthreads.json. One per server.</summary>
    public class StockThreads
    {
        public List<LiveStockThread> Threads { get; set; } = new List<LiveStockThread>();

        /// <summary>Bumped when every old ticker thread should be deleted once; see <see cref="CurrentCleanupVersion"/>.</summary>
        public int CleanupVersion { get; set; }

        /// <summary>1: delete every ticker thread made before the command became admin-only.</summary>
        public const int CurrentCleanupVersion = 1;
    }

    public class LiveStockThread
    {
        public ulong GuildId { get; set; }
        /// <summary>The channel the thread was opened in.</summary>
        public ulong ParentChannelId { get; set; }
        public ulong ThreadId { get; set; }
        /// <summary>The one message with the whole market: a text line per stock and one chart image, edited every tick.</summary>
        public ulong BoardMessageId { get; set; }
        /// <summary>Only on threads made before the single-image board: their first message and one ticker message per stock. They are deleted and replaced by the board.</summary>
        public ulong HeaderMessageId { get; set; }
        public List<TickerMessage> Tickers { get; set; } = new List<TickerMessage>();

        /// <summary>True for a thread saved before the single-image board, which has to be replaced.</summary>
        public bool IsOldLayout => BoardMessageId == 0 || Tickers.Count > 0 || HeaderMessageId != 0;
        /// <summary>The stock tick the tickers last showed (<see cref="StockMarket.LastTickSlot"/>).</summary>
        public long LastSlot { get; set; }

    }

    public class TickerMessage
    {
        public string Symbol { get; set; }
        /// <summary>Old layout only. True for the recent ticker (the last 3 hours, <see cref="Services.StockThreadService.RecentWindow"/>), false for the all-time one. Named from when it was 1 hour; kept so saved threads load.</summary>
        public bool LastHour { get; set; }
        public ulong MessageId { get; set; }
    }
}
