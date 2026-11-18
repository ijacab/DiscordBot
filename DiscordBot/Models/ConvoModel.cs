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
        /// <summary>How many times this message counts: a favoured channel's messages count more, in word choice and in who talks.</summary>
        public int Weight { get; set; } = 1;
    }

    /// <summary>One generated line of a `.convo`.</summary>
    public sealed class ConvoLine
    {
        public string Speaker { get; set; }
        public string Text { get; set; }
        public override string ToString() => $"{Speaker}: {Text}";
    }

    /// <summary>
    /// The `.convo` generator: a word-level Markov chain over the server's scraped messages, one per person, so each line sounds like
    /// whoever it's from (two words of context, falling back to one word <see cref="Chaos"/> of the time, which is what makes it go
    /// off the rails; everyone's words only bridge a word the speaker never used). The speakers come from a real run of
    /// messages in one channel, each line starts the way that person really starts messages or picks up a word from the line
    /// before, so it reads like people talking to each other. Tokens are kept in sorted arrays (about 30 bytes a word), so a
    /// million messages fit comfortably on the Pi. Pure: the caller passes the <see cref="Random"/>.
    /// </summary>
    public sealed class ConvoModel
    {
        /// <summary>How often a word is picked from one word of context instead of two. Higher is crazier.</summary>
        public const double Chaos = 0.4;
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

        /// <summary>Each person's own two-word chain: lines are built from what that person says.</summary>
        private Chain<long> _speakerTri;
        /// <summary>Each person's own one-word chain, for the chaos steps, so they stay in character even when scrambled.</summary>
        private Chain<int> _speakerBi;
        /// <summary>Everyone's one-word chain: only to get past a word the speaker never used (a seed or a reply hook).</summary>
        private Chain<int> _bi;

        private string[] _speakers;
        private int[] _msgSpeaker;
        private int[] _msgChannel;
        private int[] _msgFirst;
        private int[][] _speakerMsgs;
        /// <summary>Running total of message weights, so a run of speakers starts at a message picked by weight.</summary>
        private long[] _msgCumulativeWeight;

        public int MessageCount => _msgSpeaker.Length;
        public int SpeakerCount => _speakers.Length;
        public int WordCount => _bi.Length;

        /// <summary>People with fewer messages than this borrow everyone's words for the chaos steps, or they'd only quote themselves.</summary>
        public const int MinOwnMessages = 20;

        private ConvoModel() { }

        /// <summary>Builds the model from each channel's messages, oldest first.</summary>
        public static ConvoModel Build(IEnumerable<IReadOnlyList<ConvoMessage>> channels)
        {
            var model = new ConvoModel();
            var speakerIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var speakers = new List<string>();
            var triSpeaker = new List<int>();
            var triKey = new List<long>();
            var triNext = new List<int>();
            var biSpeaker = new List<int>();
            var biKey = new List<int>();
            var biNext = new List<int>();
            var msgSpeaker = new List<int>();
            var msgChannel = new List<int>();
            var msgFirst = new List<int>();
            var msgWeight = new List<int>();

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

                    //a weighted message's entries go in that many times, which is how the chain counts frequency
                    int weight = Math.Max(1, message.Weight);
                    int a = StartToken, b = StartToken;
                    for (int i = 0; i <= tokens.Length; i++)
                    {
                        int c = i < tokens.Length ? model.IdOf(tokens[i]) : EndToken;
                        for (int w = 0; w < weight; w++)
                        {
                            triSpeaker.Add(speaker);
                            triKey.Add(Pack(a, b));
                            triNext.Add(c);
                            biSpeaker.Add(speaker);
                            biKey.Add(b);
                            biNext.Add(c);
                        }
                        a = b;
                        b = c;
                    }
                    msgWeight.Add(weight);

                    msgSpeaker.Add(speaker);
                    msgChannel.Add(channelIndex);
                    msgFirst.Add(model.IdOf(tokens[0]));
                }
                channelIndex++;
            }

            model._speakerTri = new Chain<long>(triSpeaker, triKey, triNext, speakers.Count);
            triSpeaker = null; triKey = null; triNext = null;
            model._speakerBi = new Chain<int>(biSpeaker, biKey, biNext, speakers.Count);
            model._bi = new Chain<int>(null, biKey, biNext, 1);

            model._speakers = speakers.ToArray();
            model._msgSpeaker = msgSpeaker.ToArray();
            model._msgChannel = msgChannel.ToArray();
            model._msgFirst = msgFirst.ToArray();
            var bySpeaker = new List<int>[speakers.Count];
            for (int i = 0; i < bySpeaker.Length; i++) bySpeaker[i] = new List<int>();
            model._msgCumulativeWeight = new long[msgWeight.Count];
            long cumulative = 0;
            for (int m = 0; m < model._msgSpeaker.Length; m++)
            {
                for (int w = 0; w < msgWeight[m]; w++) bySpeaker[model._msgSpeaker[m]].Add(m);
                model._msgCumulativeWeight[m] = cumulative += msgWeight[m];
            }
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
        /// carries on from it, as if it had been said first, but the seed itself isn't shown. Null when the model has no messages.
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
                    int hidden = 0;
                    if (i == 0 && seedWords.Length > 0 && attempt < 4)
                    {
                        prefix.AddRange(seedWords);
                        hidden = seedWords.Length;
                    }
                    else if (i > 0 && attempt < 4 && random.NextDouble() < ReplyHookChance && PickHook(random, lines[i - 1].Text, speakers[i]) is string hook)
                        prefix.Add(hook);
                    else if (attempt < 4 && random.NextDouble() < OwnStartChance)
                    {
                        var own = _speakerMsgs[speakers[i]];
                        prefix.Add(_words[_msgFirst[own[random.Next(own.Length)]]]);
                    }
                    text = Line(random, speakers[i], prefix, hidden);
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
                int start = Math.Min(WeightedMessage(random), MessageCount - count);
                if (_msgChannel[start] != _msgChannel[start + count - 1])
                    continue;
                var run = Enumerable.Range(start, count).Select(m => _msgSpeaker[m]).ToList();
                if (run.Distinct().Count() >= 2)
                    return run;
            }
            return Enumerable.Range(0, count).Select(_ => _msgSpeaker[random.Next(MessageCount)]).ToList();
        }

        /// <summary>A message picked in proportion to its weight.</summary>
        private int WeightedMessage(Random random)
        {
            long total = _msgCumulativeWeight[^1];
            long target = (long)(random.NextDouble() * total);
            int lo = 0, hi = _msgCumulativeWeight.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_msgCumulativeWeight[mid] <= target) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>A word from the line before worth replying to: three letters or more, not filler, and one the replier uses too.</summary>
        private string PickHook(Random random, string previous, int speaker)
        {
            var candidates = Tokenize(previous)
                .Where(w => w.Length >= 3 && !StopWords.Contains(w) && Find(w) is int id && _speakerBi.Has(speaker, id))
                .ToList();
            return candidates.Count == 0 ? null : candidates[random.Next(candidates.Count)];
        }

        /// <summary>
        /// One message by <paramref name="speaker"/>: the prefix, then words from their own chain until it ends or gets too long.
        /// Everyone's chain is only used when they have no word to follow the last one. The first <paramref name="hidden"/> words
        /// of the prefix steer the chain but are left out of the text (a `.convo` seed).
        /// </summary>
        private string Line(Random random, int speaker, IList<string> prefix, int hidden = 0)
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

            bool rare = _speakerMsgs[speaker].Length < MinOwnMessages;
            int length = words.Sum(w => w.Length + 1);
            while (words.Count < MaxWords)
            {
                int next = EndToken;
                bool chaos = b != StartToken && random.NextDouble() < Chaos;
                bool found = !chaos && _speakerTri.TryPick(speaker, Pack(a, b), random, out next);
                if (!found && !(chaos && rare))
                    found = _speakerBi.TryPick(speaker, b, random, out next);
                if (!found && !_bi.TryPick(0, b, random, out next))
                    next = EndToken;
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
            return string.Join(" ", words.Skip(hidden)).Trim();
        }

        /// <summary>
        /// A Markov table split by person: every (context → next word) seen, grouped by who said it and sorted by context inside each
        /// group, so picking a next word is a binary search and duplicates keep their real frequency. About 12 bytes an entry.
        /// </summary>
        private sealed class Chain<TKey> where TKey : struct, IComparable<TKey>
        {
            private readonly TKey[] _keys;
            private readonly int[] _next;
            private readonly int[] _start;

            public int Length => _keys.Length;

            /// <param name="groups">Who said each entry, or null for one group.</param>
            public Chain(List<int> groups, List<TKey> keys, List<int> next, int groupCount)
            {
                _start = new int[groupCount + 1];
                _keys = new TKey[keys.Count];
                _next = new int[keys.Count];
                if (groups == null)
                {
                    keys.CopyTo(_keys);
                    next.CopyTo(_next);
                    _start[1] = keys.Count;
                }
                else
                {
                    foreach (int g in groups) _start[g + 1]++;
                    for (int g = 0; g < groupCount; g++) _start[g + 1] += _start[g];
                    var position = (int[])_start.Clone();
                    for (int i = 0; i < keys.Count; i++)
                    {
                        int p = position[groups[i]]++;
                        _keys[p] = keys[i];
                        _next[p] = next[i];
                    }
                }
                for (int g = 0; g < groupCount; g++)
                    Array.Sort(_keys, _next, _start[g], _start[g + 1] - _start[g]);
            }

            public bool Has(int group, TKey key) => Range(group, key, out _, out _);

            public bool TryPick(int group, TKey key, Random random, out int next)
            {
                if (!Range(group, key, out int lo, out int hi))
                {
                    next = EndToken;
                    return false;
                }
                next = _next[lo + random.Next(hi - lo)];
                return true;
            }

            private bool Range(int group, TKey key, out int lo, out int hi)
            {
                lo = Bound(_start[group], _start[group + 1], key, upper: false);
                hi = Bound(lo, _start[group + 1], key, upper: true);
                return lo < hi;
            }

            private int Bound(int lo, int hi, TKey key, bool upper)
            {
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    int c = _keys[mid].CompareTo(key);
                    if (c < 0 || (upper && c == 0)) lo = mid + 1; else hi = mid;
                }
                return lo;
            }
        }
    }
}
