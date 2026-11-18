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
        /// <summary>From a favoured channel: those make up <see cref="ConvoModel.DefaultFavouredShare"/> of word choices and of who talks.</summary>
        public bool Favoured { get; set; }
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
        public const double Chaos = 0.6;
        /// <summary>How often a reply picks up a word from the line before it.</summary>
        public const double ReplyHookChance = 0.35;
        /// <summary>How often a line starts with a word its speaker really starts messages with.</summary>
        public const double OwnStartChance = 0.5;
        public const int MinLines = 3;
        public const int MaxLines = 10;
        public const int MaxWords = 40;
        public const int MaxLineChars = 160;
        public const int MaxTotalChars = 1900;
        /// <summary>How much of each choice (who talks, opening words, every next word) comes from the favoured channels when they have one.</summary>
        public const double DefaultFavouredShare = 0.95;

        private const int OtherPool = 0;
        private const int FavouredPool = 1;

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

        /// <summary>Each person's own two-word chain: lines are built from what that person says. Grouped by person and pool (<see cref="Group"/>).</summary>
        private Chain<long> _speakerTri;
        /// <summary>Each person's own one-word chain, for the chaos steps, so they stay in character even when scrambled.</summary>
        private Chain<int> _speakerBi;
        /// <summary>Everyone's one-word chain: only to get past a word the speaker never used (a seed or a reply hook).</summary>
        private Chain<int> _bi;

        private string[] _speakers;
        private int[] _msgSpeaker;
        private int[] _msgChannel;
        private int[] _msgFirst;
        /// <summary>Each person's messages, by <see cref="Group"/> (person and pool), for their opening words.</summary>
        private int[][] _speakerMsgs;
        /// <summary>Messages from the favoured channels and from the rest, for where a run of speakers starts.</summary>
        private int[] _favouredMsgs;
        private int[] _otherMsgs;
        private double _favouredShare;
        /// <summary>Hashes of every real message, so a line that just repeats one word for word is made again.</summary>
        private readonly HashSet<long> _realMessages = new HashSet<long>();

        /// <summary>Lines this long or shorter may repeat a real message ("lol", "no way"); longer copies are made again.</summary>
        public const int MaxCopiedWords = 2;

        public int MessageCount => _msgSpeaker.Length;
        public int SpeakerCount => _speakers.Length;
        public int WordCount => _bi.Length;

        /// <summary>People with fewer messages than this borrow everyone's words for the chaos steps, or they'd only quote themselves.</summary>
        public const int MinOwnMessages = 20;

        private ConvoModel() { }

        /// <summary>
        /// Builds the model from each channel's messages, oldest first. Favoured messages make up <paramref name="favouredShare"/> of
        /// every choice whenever they have something to offer, however few there are.
        /// </summary>
        public static ConvoModel Build(IEnumerable<IReadOnlyList<ConvoMessage>> channels, double favouredShare = DefaultFavouredShare)
        {
            var model = new ConvoModel { _favouredShare = favouredShare };
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
            var msgPool = new List<int>();

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

                    int pool = message.Favoured ? FavouredPool : OtherPool;
                    int group = Group(speaker, pool);
                    int a = StartToken, b = StartToken;
                    for (int i = 0; i <= tokens.Length; i++)
                    {
                        int c = i < tokens.Length ? model.IdOf(tokens[i]) : EndToken;
                        triSpeaker.Add(group);
                        triKey.Add(Pack(a, b));
                        triNext.Add(c);
                        biSpeaker.Add(group);
                        biKey.Add(b);
                        biNext.Add(c);
                        a = b;
                        b = c;
                    }
                    msgPool.Add(pool);

                    msgSpeaker.Add(speaker);
                    msgChannel.Add(channelIndex);
                    msgFirst.Add(model.IdOf(tokens[0]));
                    model._realMessages.Add(HashOf(string.Join(" ", tokens)));
                }
                channelIndex++;
            }

            model._speakerTri = new Chain<long>(triSpeaker, triKey, triNext, speakers.Count * 2);
            triSpeaker = null; triKey = null; triNext = null;
            model._speakerBi = new Chain<int>(biSpeaker, biKey, biNext, speakers.Count * 2);
            model._bi = new Chain<int>(null, biKey, biNext, 1);

            model._speakers = speakers.ToArray();
            model._msgSpeaker = msgSpeaker.ToArray();
            model._msgChannel = msgChannel.ToArray();
            model._msgFirst = msgFirst.ToArray();
            var byGroup = new List<int>[speakers.Count * 2];
            for (int i = 0; i < byGroup.Length; i++) byGroup[i] = new List<int>();
            for (int m = 0; m < model._msgSpeaker.Length; m++)
                byGroup[Group(model._msgSpeaker[m], msgPool[m])].Add(m);
            model._speakerMsgs = byGroup.Select(l => l.ToArray()).ToArray();
            model._favouredMsgs = Enumerable.Range(0, msgPool.Count).Where(m => msgPool[m] == FavouredPool).ToArray();
            model._otherMsgs = Enumerable.Range(0, msgPool.Count).Where(m => msgPool[m] == OtherPool).ToArray();
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

        /// <summary>A person's entries from one pool (the favoured channels or the rest).</summary>
        private static int Group(int speaker, int pool) => speaker * 2 + pool;

        /// <summary>The favoured pool <see cref="_favouredShare"/> of the time, else the rest.</summary>
        private int PickPool(Random random) => random.NextDouble() < _favouredShare ? FavouredPool : OtherPool;

        private int MessagesBy(int speaker) => _speakerMsgs[Group(speaker, FavouredPool)].Length + _speakerMsgs[Group(speaker, OtherPool)].Length;

        /// <summary>The speaker's next word from the pool picked for this step, or from their other pool when it has nothing.</summary>
        private bool TryPickOwn<TKey>(Chain<TKey> chain, int speaker, TKey key, Random random, out int next) where TKey : struct, IComparable<TKey>
        {
            int pool = PickPool(random);
            return chain.TryPick(Group(speaker, pool), key, random, out next) || chain.TryPick(Group(speaker, 1 - pool), key, random, out next);
        }

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
                        int pool = PickPool(random);
                        var own = _speakerMsgs[Group(speakers[i], pool)];
                        if (own.Length == 0) own = _speakerMsgs[Group(speakers[i], 1 - pool)];
                        prefix.Add(_words[_msgFirst[own[random.Next(own.Length)]]]);
                    }
                    text = Line(random, speakers[i], prefix, hidden);
                    //a word-for-word copy of something someone really said is too accurate: try again (and keep it if nothing else comes)
                    if (attempt < 6 && IsCopy(text))
                        text = null;
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

        /// <summary>Whether a line is a real message repeated word for word (short ones don't count).</summary>
        public bool IsCopy(string text) =>
            !string.IsNullOrEmpty(text) && Tokenize(text).Length > MaxCopiedWords && _realMessages.Contains(HashOf(string.Join(" ", Tokenize(text))));

        /// <summary>A stable 64-bit hash (FNV-1a) of a message's text.</summary>
        private static long HashOf(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return (long)hash;
        }

        /// <summary>The lines as one message: "user: words", one per line.</summary>
        public static string Format(IEnumerable<ConvoLine> lines) =>
            string.Join("\n", lines.Select(l => $"{Discord.Format.Sanitize(l.Speaker)}: {l.Text}"));

        /// <summary>The speakers of a real run of messages in one channel with at least two people in it, so the right people talk.</summary>
        private List<int> PickSpeakers(Random random, int count)
        {
            for (int attempt = 0; attempt < 40 && MessageCount >= count; attempt++)
            {
                int start = Math.Min(RunStart(random), MessageCount - count);
                if (_msgChannel[start] != _msgChannel[start + count - 1])
                    continue;
                var run = Enumerable.Range(start, count).Select(m => _msgSpeaker[m]).ToList();
                if (run.Distinct().Count() >= 2)
                    return run;
            }
            return Enumerable.Range(0, count).Select(_ => _msgSpeaker[random.Next(MessageCount)]).ToList();
        }

        /// <summary>Where a run of speakers starts: a favoured channel's message <see cref="_favouredShare"/> of the time, if there are any.</summary>
        private int RunStart(Random random)
        {
            var from = PickPool(random) == FavouredPool ? _favouredMsgs : _otherMsgs;
            if (from.Length == 0) from = from == _favouredMsgs ? _otherMsgs : _favouredMsgs;
            return from[random.Next(from.Length)];
        }

        /// <summary>A word from the line before worth replying to: three letters or more, not filler, and one the replier uses too.</summary>
        private string PickHook(Random random, string previous, int speaker)
        {
            var candidates = Tokenize(previous)
                .Where(w => w.Length >= 3 && !StopWords.Contains(w) && Find(w) is int id && (_speakerBi.Has(Group(speaker, FavouredPool), id) || _speakerBi.Has(Group(speaker, OtherPool), id)))
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

            bool rare = MessagesBy(speaker) < MinOwnMessages;
            int length = words.Sum(w => w.Length + 1);
            while (words.Count < MaxWords)
            {
                int next = EndToken;
                bool chaos = b != StartToken && random.NextDouble() < Chaos;
                bool found = !chaos && TryPickOwn(_speakerTri, speaker, Pack(a, b), random, out next);
                if (!found && !(chaos && rare))
                    found = TryPickOwn(_speakerBi, speaker, b, random, out next);
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
