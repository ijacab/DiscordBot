using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DiscordBot.Models
{
    /// <summary>One message from the server's history, as the <see cref="ConvoModel"/> learns it.</summary>
    public sealed class ConvoMessage
    {
        public ulong Id { get; set; }
        public string Speaker { get; set; }
        public string Text { get; set; }
    }

    /// <summary>One generated line of a `.convo`.</summary>
    public sealed class ConvoLine
    {
        public string Speaker { get; set; }
        public string Text { get; set; }
        public override string ToString() => $"{Speaker}: {Text}";
    }

    /// <summary>
    /// The `.convo` generator: a word-level Markov chain over the server's scraped messages (two words of context, falling back to
    /// one word <see cref="Chaos"/> of the time, which is what makes it go off the rails). The speakers come from a real run of
    /// messages in one channel, each line starts the way that person really starts messages or picks up a word from the line
    /// before, so it reads like people talking to each other. Tokens are kept in sorted arrays (about 20 bytes a word), so a
    /// million messages fit comfortably on the Pi. Pure: the caller passes the <see cref="Random"/>.
    /// </summary>
    public sealed class ConvoModel
    {
        /// <summary>How often a word is picked from one word of context instead of two. Higher is crazier.</summary>
        public const double Chaos = 0.3;
        /// <summary>How often a reply picks up a word from the line before it.</summary>
        public const double ReplyHookChance = 0.35;
        /// <summary>How often a line starts with a word its speaker really starts messages with.</summary>
        public const double OwnStartChance = 0.5;
        public const int MinLines = 3;
        public const int MaxLines = 10;
        public const int MaxWords = 40;
        public const int MaxLineChars = 160;
        public const int MaxTotalChars = 1900;

        private const int StartToken = 0;
        private const int EndToken = 1;

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "you", "that", "this", "for", "are", "was", "but", "not", "with", "have", "its", "it's", "just", "what",
            "like", "they", "get", "can", "all", "one", "out", "his", "her", "him", "she", "has", "had", "then", "than", "too",
            "who", "how", "why", "when", "where", "there", "their", "your", "you're", "i'm", "im", "dont", "don't", "yeah", "lol",
            "lmao", "from", "about", "would", "will", "some", "been", "were", "them", "into", "also", "very", "really", "much",
        };

        private readonly List<string> _words = new List<string> { "<s>", "</s>" };
        private readonly Dictionary<string, int> _ids = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _lowerIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private long[] _triKey;
        private int[] _triNext;
        private int[] _biKey;
        private int[] _biNext;

        private string[] _speakers;
        private int[] _msgSpeaker;
        private int[] _msgChannel;
        private int[] _msgFirst;
        private int[][] _speakerMsgs;

        public int MessageCount => _msgSpeaker.Length;
        public int SpeakerCount => _speakers.Length;
        public int WordCount => _biKey.Length;

        private ConvoModel() { }

        /// <summary>Builds the model from each channel's messages, oldest first.</summary>
        public static ConvoModel Build(IEnumerable<IReadOnlyList<ConvoMessage>> channels)
        {
            var model = new ConvoModel();
            var speakerIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var speakers = new List<string>();
            var triKey = new List<long>();
            var triNext = new List<int>();
            var biKey = new List<int>();
            var biNext = new List<int>();
            var msgSpeaker = new List<int>();
            var msgChannel = new List<int>();
            var msgFirst = new List<int>();

            int channelIndex = 0;
            foreach (var channel in channels)
            {
                foreach (var message in channel)
                {
                    var tokens = Tokenize(message.Text);
                    if (tokens.Length == 0 || string.IsNullOrWhiteSpace(message.Speaker))
                        continue;
                    if (!speakerIds.TryGetValue(message.Speaker, out int speaker))
                    {
                        speakerIds[message.Speaker] = speaker = speakers.Count;
                        speakers.Add(message.Speaker);
                    }

                    int a = StartToken, b = StartToken;
                    for (int i = 0; i <= tokens.Length; i++)
                    {
                        int c = i < tokens.Length ? model.IdOf(tokens[i]) : EndToken;
                        triKey.Add(Pack(a, b));
                        triNext.Add(c);
                        biKey.Add(b);
                        biNext.Add(c);
                        a = b;
                        b = c;
                    }

                    msgSpeaker.Add(speaker);
                    msgChannel.Add(channelIndex);
                    msgFirst.Add(model.IdOf(tokens[0]));
                }
                channelIndex++;
            }

            model._triKey = triKey.ToArray();
            model._triNext = triNext.ToArray();
            Array.Sort(model._triKey, model._triNext);
            model._biKey = biKey.ToArray();
            model._biNext = biNext.ToArray();
            Array.Sort(model._biKey, model._biNext);

            model._speakers = speakers.ToArray();
            model._msgSpeaker = msgSpeaker.ToArray();
            model._msgChannel = msgChannel.ToArray();
            model._msgFirst = msgFirst.ToArray();
            var bySpeaker = new List<int>[speakers.Count];
            for (int i = 0; i < bySpeaker.Length; i++) bySpeaker[i] = new List<int>();
            for (int m = 0; m < model._msgSpeaker.Length; m++) bySpeaker[model._msgSpeaker[m]].Add(m);
            model._speakerMsgs = bySpeaker.Select(l => l.ToArray()).ToArray();
            return model;
        }

        public static string[] Tokenize(string text) =>
            string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        private int IdOf(string word)
        {
            if (_ids.TryGetValue(word, out int id))
                return id;
            id = _words.Count;
            _words.Add(word);
            _ids[word] = id;
            _lowerIds.TryAdd(word, id);
            return id;
        }

        private int? Find(string word) =>
            _ids.TryGetValue(word, out int id) ? id : _lowerIds.TryGetValue(word, out id) ? id : (int?)null;

        private static long Pack(int a, int b) => ((long)a << 32) | (uint)b;

        /// <summary>
        /// A conversation of <see cref="MinLines"/> to <see cref="MaxLines"/> lines. With a <paramref name="seed"/> the first line
        /// starts with it. Null when the model has no messages.
        /// </summary>
        public List<ConvoLine> Generate(Random random, string seed = null)
        {
            if (MessageCount == 0)
                return null;

            int count = random.Next(MinLines, MaxLines + 1);
            var speakers = PickSpeakers(random, count);
            var lines = new List<ConvoLine>();
            int total = 0;
            string[] seedWords = Tokenize(seed);

            for (int i = 0; i < speakers.Count; i++)
            {
                string text = null;
                for (int attempt = 0; attempt < 8 && string.IsNullOrEmpty(text); attempt++)
                {
                    var prefix = new List<string>();
                    if (i == 0 && seedWords.Length > 0 && attempt == 0)
                        prefix.AddRange(seedWords);
                    else if (i > 0 && attempt < 4 && random.NextDouble() < ReplyHookChance && PickHook(random, lines[i - 1].Text) is string hook)
                        prefix.Add(hook);
                    else if (attempt < 4 && random.NextDouble() < OwnStartChance)
                    {
                        var own = _speakerMsgs[speakers[i]];
                        prefix.Add(_words[_msgFirst[own[random.Next(own.Length)]]]);
                    }
                    text = Line(random, prefix);
                }
                if (string.IsNullOrEmpty(text))
                    continue;

                var line = new ConvoLine { Speaker = _speakers[speakers[i]], Text = text };
                int length = line.ToString().Length + 1;
                if (total + length > MaxTotalChars)
                    break;
                total += length;
                lines.Add(line);
            }
            return lines;
        }

        /// <summary>The lines as one message: "user: words", one per line.</summary>
        public static string Format(IEnumerable<ConvoLine> lines) =>
            string.Join("\n", lines.Select(l => $"{Discord.Format.Sanitize(l.Speaker)}: {l.Text}"));

        /// <summary>The speakers of a real run of messages in one channel with at least two people in it, so the right people talk.</summary>
        private List<int> PickSpeakers(Random random, int count)
        {
            for (int attempt = 0; attempt < 40 && MessageCount >= count; attempt++)
            {
                int start = random.Next(MessageCount - count + 1);
                if (_msgChannel[start] != _msgChannel[start + count - 1])
                    continue;
                var run = Enumerable.Range(start, count).Select(m => _msgSpeaker[m]).ToList();
                if (run.Distinct().Count() >= 2)
                    return run;
            }
            return Enumerable.Range(0, count).Select(_ => _msgSpeaker[random.Next(MessageCount)]).ToList();
        }

        /// <summary>A word from the line before worth replying to: three letters or more and not filler.</summary>
        private string PickHook(Random random, string previous)
        {
            var candidates = Tokenize(previous)
                .Where(w => w.Length >= 3 && !StopWords.Contains(w) && Find(w) != null)
                .ToList();
            return candidates.Count == 0 ? null : candidates[random.Next(candidates.Count)];
        }

        /// <summary>One message: the prefix, then words from the chain until it ends or gets too long.</summary>
        private string Line(Random random, IList<string> prefix)
        {
            var words = new List<string>(prefix);
            int a = StartToken, b = StartToken;
            foreach (var word in prefix)
            {
                if (Find(word) is int id)
                {
                    a = b;
                    b = id;
                }
                else
                {
                    //a word the server never used: carry on as if a new message started after it
                    a = b = StartToken;
                }
            }

            int length = words.Sum(w => w.Length + 1);
            while (words.Count < MaxWords)
            {
                int next;
                bool chaos = b != StartToken && random.NextDouble() < Chaos;
                if (chaos || !TryPick(_triKey, _triNext, Pack(a, b), random, out next))
                {
                    if (!TryPick(_biKey, _biNext, b, random, out next))
                        next = EndToken;
                }
                if (next == EndToken || next == StartToken)
                    break;
                string word = _words[next];
                if (length + word.Length + 1 > MaxLineChars)
                    break;
                words.Add(word);
                length += word.Length + 1;
                a = b;
                b = next;
            }
            return string.Join(" ", words).Trim();
        }

        private static bool TryPick(long[] keys, int[] values, long key, Random random, out int next)
        {
            int lo = Lower(keys, key), hi = Lower(keys, key + 1);
            next = lo < hi ? values[lo + random.Next(hi - lo)] : EndToken;
            return lo < hi;
        }

        private static bool TryPick(int[] keys, int[] values, int key, Random random, out int next)
        {
            int lo = Lower(keys, key), hi = Lower(keys, key + 1);
            next = lo < hi ? values[lo + random.Next(hi - lo)] : EndToken;
            return lo < hi;
        }

        private static int Lower(long[] a, long key)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (a[mid] < key) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private static int Lower(int[] a, int key)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (a[mid] < key) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }
}
