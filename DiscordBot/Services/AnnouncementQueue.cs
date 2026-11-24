using Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    /// <summary>
    /// Messages that must not be lost (the raffle winner, a kept power-up going off) when sending fails: a channel the bot can't
    /// see or write in, a deleted thread, a Discord hiccup. They wait here and are tried again every minute for
    /// <see cref="Life"/>, then dropped. Held in memory, so a restart forgets them.
    /// </summary>
    public sealed class AnnouncementQueue
    {
        public static readonly TimeSpan Life = TimeSpan.FromMinutes(30);
        public const int MaxWaiting = 30;

        public sealed class Item
        {
            public string Text { get; set; }
            public MessageComponent Components { get; set; }
            public DateTime QueuedUtc { get; set; }
        }

        private readonly List<Item> _items = new List<Item>();
        private readonly object _gate = new object();

        public int Count { get { lock (_gate) return _items.Count; } }

        /// <summary>Adds a message to try again later. The oldest is dropped when too many are waiting.</summary>
        public void Enqueue(string text, MessageComponent components, DateTime nowUtc)
        {
            lock (_gate)
            {
                _items.Add(new Item { Text = text, Components = components, QueuedUtc = nowUtc });
                while (_items.Count > MaxWaiting) _items.RemoveAt(0);
            }
        }

        /// <summary>Tries each waiting message in order with <paramref name="trySend"/>; the ones that went are removed, and ones older than <see cref="Life"/> are dropped. Returns how many were sent.</summary>
        public async Task<int> Flush(Func<Item, Task<bool>> trySend, DateTime nowUtc)
        {
            List<Item> waiting;
            lock (_gate)
            {
                _items.RemoveAll(i => nowUtc - i.QueuedUtc > Life);
                waiting = _items.ToList();
            }
            int sent = 0;
            foreach (var item in waiting)
            {
                bool ok;
                try { ok = await trySend(item); }
                catch { ok = false; }
                if (!ok) continue;
                sent++;
                lock (_gate) _items.Remove(item);
            }
            return sent;
        }
    }
}
