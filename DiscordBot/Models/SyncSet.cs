using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DiscordBot.Models
{
    /// <summary>A small thread-safe set. Enumerating takes a snapshot.</summary>
    public sealed class SyncSet<T> : IEnumerable<T>
    {
        private readonly ConcurrentDictionary<T, byte> _items = new ConcurrentDictionary<T, byte>();

        /// <summary>Adds the item; false when it was already there. Check-and-add in one step.</summary>
        public bool TryAdd(T item) => _items.TryAdd(item, 0);
        public void Add(T item) => _items.TryAdd(item, 0);
        public bool Remove(T item) => _items.TryRemove(item, out _);
        public bool Contains(T item) => _items.ContainsKey(item);
        public int Count => _items.Count;
        public IEnumerator<T> GetEnumerator() => _items.Keys.ToList().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
