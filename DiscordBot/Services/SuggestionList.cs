using DiscordBot.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DiscordBot.Services
{
    /// <summary>One line of the suggestions list: the text and who wrote it (0 and "unknown" for lines saved before authors were kept).</summary>
    public sealed class Suggestion
    {
        public string Text { get; set; }
        public ulong AuthorId { get; set; }
        public string Author { get; set; } = "unknown";
    }

    /// <summary>
    /// The master suggestions list behind `.suggest` / `.sug`: one "- text" line per suggestion in a plain file next to the bot
    /// (never in the repo), followed by a tab, the author's id, a tab and their name so single suggestions or one person's can be
    /// removed. One writer at a time, written atomically.
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

        /// <summary>Every suggestion, oldest first.</summary>
        public IReadOnlyList<Suggestion> All()
        {
            lock (_gate) return Read();
        }

        /// <summary>Just the texts, oldest first.</summary>
        public IReadOnlyList<string> Texts() => All().Select(s => s.Text).ToList();

        /// <summary>Adds a suggestion and returns how many there are now. Throws <see cref="BadInputException"/> when it is empty, too long or the list is full.</summary>
        public int Add(string text, ulong authorId = 0, string author = "unknown")
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
                items.Add(new Suggestion { Text = clean, AuthorId = authorId, Author = CleanName(author) });
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
                Write(new List<Suggestion>());
                return count;
            }
        }

        /// <summary>Removes the suggestions at these 1-based places (as `.sug list` numbers them). Nothing is removed if any number is off the list.</summary>
        public List<Suggestion> RemoveNumbers(IEnumerable<int> numbers)
        {
            lock (_gate)
            {
                var items = Read();
                var wanted = numbers.Distinct().ToList();
                foreach (int n in wanted)
                    if (n < 1 || n > items.Count)
                        throw new BadInputException(items.Count == 0 ? "The list is empty." : $"There's no suggestion #{n}. The list has {items.Count}.");
                var removed = wanted.OrderBy(n => n).Select(n => items[n - 1]).ToList();
                Write(items.Where(i => !removed.Contains(i)).ToList());
                return removed;
            }
        }

        /// <summary>
        /// Removes the suggestions at these 1-based places, but only if every one was written by <paramref name="authorId"/>:
        /// someone deleting their own. Nothing is removed if one is off the list or is somebody else's.
        /// </summary>
        public List<Suggestion> RemoveOwnNumbers(IEnumerable<int> numbers, ulong authorId)
        {
            lock (_gate)
            {
                var items = Read();
                var wanted = numbers.Distinct().OrderBy(n => n).ToList();
                foreach (int n in wanted)
                {
                    if (n < 1 || n > items.Count)
                        throw new BadInputException(items.Count == 0 ? "The list is empty." : $"There's no suggestion #{n}. The list has {items.Count}.");
                    if (authorId == 0 || items[n - 1].AuthorId != authorId)
                        throw new BadInputException($"Suggestion #{n} isn't yours, so you can't delete it. `.sug list` shows who wrote what.");
                }
                var removed = wanted.Select(n => items[n - 1]).ToList();
                Write(items.Where(i => !removed.Contains(i)).ToList());
                return removed;
            }
        }

        /// <summary>Removes everything one person wrote, found by their user id (when it is known) or else by name, ignoring case.</summary>
        public List<Suggestion> RemoveBy(ulong authorId, string name)
        {
            lock (_gate)
            {
                var items = Read();
                bool Mine(Suggestion s) => (authorId != 0 && s.AuthorId == authorId)
                    || (!string.IsNullOrWhiteSpace(name) && s.Author.Equals(name.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase));
                var removed = items.Where(Mine).ToList();
                if (removed.Count > 0)
                    Write(items.Where(i => !removed.Contains(i)).ToList());
                return removed;
            }
        }

        /// <summary>"3", "2 5" and "2-4" as place numbers, or null when any word isn't one.</summary>
        public static List<int> ParseNumbers(IEnumerable<string> words)
        {
            var numbers = new List<int>();
            foreach (var word in words)
            {
                var range = Regex.Match(word.Trim(), @"^(\d{1,4})-(\d{1,4})$");
                if (range.Success)
                {
                    int from = int.Parse(range.Groups[1].Value), to = int.Parse(range.Groups[2].Value);
                    if (to < from || to - from >= MaxItems) return null;
                    numbers.AddRange(Enumerable.Range(from, to - from + 1));
                }
                else if (int.TryParse(word.Trim().TrimStart('#'), out int n) && n > 0)
                    numbers.Add(n);
                else
                    return null;
            }
            return numbers.Count == 0 ? null : numbers;
        }

        /// <summary>One line: whitespace collapsed, a leading bullet or dash dropped.</summary>
        public static string Clean(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim().TrimStart('-', '*', '•').Trim();

        private static string CleanName(string name)
        {
            string clean = Regex.Replace(name ?? "", @"\s+", " ").Trim();
            return clean.Length == 0 ? "unknown" : clean;
        }

        /// <summary>Lines joined into messages of at most <paramref name="limit"/> characters, never cutting a line in half.</summary>
        public static List<string> Chunks(IEnumerable<string> lines, int limit)
        {
            var chunks = new List<string>();
            var current = new StringBuilder();
            foreach (var raw in lines)
            {
                string line = raw.Length > limit ? raw.Substring(0, limit) : raw;
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

        private List<Suggestion> Read()
        {
            if (!File.Exists(_path)) return new List<Suggestion>();
            var items = new List<Suggestion>();
            foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                var parts = line.Split('\t');
                string text = Clean(parts[0]);
                if (text.Length == 0) continue;
                ulong.TryParse(parts.Length > 1 ? parts[1] : "", out ulong id);
                items.Add(new Suggestion { Text = text, AuthorId = id, Author = CleanName(parts.Length > 2 ? parts[2] : "") });
            }
            return items;
        }

        private void Write(List<Suggestion> items)
        {
            string tmp = _path + ".tmp";
            File.WriteAllLines(tmp, items.Select(i => $"- {i.Text}\t{i.AuthorId}\t{i.Author}"), new UTF8Encoding(false));
            File.Move(tmp, _path, true);
        }
    }
}
