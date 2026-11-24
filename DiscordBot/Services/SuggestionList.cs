using DiscordBot.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DiscordBot.Services
{
    /// <summary>
    /// The master suggestions list behind `.suggest` / `.sug`: one "- text" line per suggestion in a plain file next to the bot
    /// (never in the repo), so the whole list can be copied out in one go. One writer at a time, written atomically.
    /// </summary>
    public sealed class SuggestionList
    {
        public const int MaxLength = 500;
        public const int MaxItems = 200;
        public const string FileName = "suggestions.txt";

        public static string DefaultPath => Path.Join(Directory.GetCurrentDirectory(), FileName);

        private readonly string _path;
        private readonly object _gate = new object();

        public SuggestionList(string path) => _path = path;

        /// <summary>Every suggestion, oldest first, without the leading dash.</summary>
        public IReadOnlyList<string> All()
        {
            lock (_gate) return Read();
        }

        /// <summary>Adds a suggestion and returns how many there are now. Throws <see cref="BadInputException"/> when it is empty, too long or the list is full.</summary>
        public int Add(string text)
        {
            string clean = Clean(text);
            if (clean.Length == 0)
                throw new BadInputException("Say what you're suggesting: `.sug make raids less boring`.");
            if (clean.Length > MaxLength)
                throw new BadInputException($"That's {clean.Length} characters. Keep a suggestion under {MaxLength}.");
            lock (_gate)
            {
                var items = Read();
                if (items.Count >= MaxItems)
                    throw new BadInputException($"The list already has {MaxItems} suggestions. It needs clearing first.");
                items.Add(clean);
                Write(items);
                return items.Count;
            }
        }

        /// <summary>Empties the list and returns how many were removed.</summary>
        public int Clear()
        {
            lock (_gate)
            {
                int count = Read().Count;
                Write(new List<string>());
                return count;
            }
        }

        /// <summary>One line: whitespace collapsed, a leading bullet or dash dropped.</summary>
        public static string Clean(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim().TrimStart('-', '*', '•').Trim();

        /// <summary>The list as "- text" lines split into messages of at most <paramref name="limit"/> characters, never cutting a suggestion in half.</summary>
        public static List<string> Chunks(IEnumerable<string> items, int limit)
        {
            var chunks = new List<string>();
            var current = new StringBuilder();
            foreach (var item in items)
            {
                string line = "- " + item;
                if (line.Length > limit) line = line.Substring(0, limit);
                if (current.Length > 0 && current.Length + 1 + line.Length > limit)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                if (current.Length > 0) current.Append('\n');
                current.Append(line);
            }
            if (current.Length > 0) chunks.Add(current.ToString());
            return chunks;
        }

        private List<string> Read() =>
            File.Exists(_path)
                ? File.ReadAllLines(_path, Encoding.UTF8).Select(l => Clean(l)).Where(l => l.Length > 0).ToList()
                : new List<string>();

        private void Write(List<string> items)
        {
            string tmp = _path + ".tmp";
            File.WriteAllLines(tmp, items.Select(i => "- " + i), new UTF8Encoding(false));
            File.Move(tmp, _path, true);
        }
    }
}
