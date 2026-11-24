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
        public const double Chaos = 0.85;
        /// <summary>
        /// Like an LLM's temperature, for the plain chain steps: 1 picks a follow-up word as often as it was really said, higher makes
        /// the rare ones likelier (weight = count^(1/temperature)), 3 is nearly a coin flip between every word ever used there.
        /// </summary>
        public const double Temperature = 1.5;
        private const int WarmSample = 400;
        /// <summary>How often a reply picks up a word from the line before it.</summary>
        public const double ReplyHookChance = 0.35;
        /// <summary>How often a line starts with a word its speaker really starts messages with.</summary>
        public const double OwnStartChance = 0.5;
        /// <summary>With words after `.convo`, how often a line (after the first, which always does) is built around one of them.</summary>
        public const double TopicLineChance = 0.6;
        /// <summary>In a line that hasn't mentioned the topic yet, how often a step takes a topic word when the speaker has one to follow.</summary>
        public const double TopicPull = 0.5;
        /// <summary>How many places everyone's chat leads into a topic word are kept, for speakers who never used it.</summary>
        private const int SharedLeadIns = 64;
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
        /// <summary>The knobs this model generates with. Shared with the service, so `.convoset` changes apply to the next `.convo`.</summary>
        public ConvoSettings Settings { get; set; } = new ConvoSettings();
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
            var model = new ConvoModel { Settings = new ConvoSettings { FavouredShare = favouredShare } };
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

        /// <summary>The favoured pool <see cref="ConvoSettings.FavouredShare"/> of the time, else the rest.</summary>
        private int PickPool(Random random) => random.NextDouble() < Settings.FavouredShare ? FavouredPool : OtherPool;

        private int MessagesBy(int speaker) => _speakerMsgs[Group(speaker, FavouredPool)].Length + _speakerMsgs[Group(speaker, OtherPool)].Length;

        /// <summary>The speaker's next word from the pool picked for this step, or from their other pool when it has nothing.</summary>
        private bool TryPickOwn<TKey>(Chain<TKey> chain, int speaker, TKey key, Random random, out int next) where TKey : struct, IComparable<TKey>
        {
            int pool = PickPool(random);
            return chain.TryPick(Group(speaker, pool), key, Settings.Temperature, random, out next) || chain.TryPick(Group(speaker, 1 - pool), key, Settings.Temperature, random, out next);
        }

        /// <summary>
        /// A conversation of <see cref="MinLines"/> to <see cref="MaxLines"/> lines. A <paramref name="seed"/> (the words after `.convo`)
        /// is the topic: the first line and about <see cref="TopicLineChance"/> of the rest are built around one of its words, led in
        /// the way the speaker (or, failing that, anyone) really led into it, and every line leans towards the topic words
        /// (<see cref="TopicPull"/>). Words nobody ever used are ignored. Null when the model has no messages.
        /// </summary>
        public List<ConvoLine> Generate(Random random, string seed = null)
        {
            if (MessageCount == 0)
                return null;

            int count = random.Next(Settings.MinLines, Settings.MaxLines + 1);
            var speakers = PickSpeakers(random, count);
            var lines = new List<ConvoLine>();
            int total = 0;
            var topic = TopicOf(Tokenize(seed));
            //how anyone led into the topic is only looked up if a speaker never used it themselves (a scan of every word said)
            var sharedLeadIns = topic == null ? null : new Lazy<List<(int Before, int Word)>>(() => SharedLeadInsTo(topic, random));

            for (int i = 0; i < speakers.Count; i++)
            {
                string text = null;
                for (int attempt = 0; attempt < 8 && string.IsNullOrEmpty(text); attempt++)
                {
                    var prefix = new List<string>();
                    if (topic != null && attempt < 4 && (i == 0 || random.NextDouble() < Settings.TopicLineChance))
                        prefix.AddRange(LeadInto(topic, speakers[i], sharedLeadIns, random));
                    else if (i > 0 && attempt < 4 && random.NextDouble() < Settings.ReplyHookChance && PickHook(random, lines[i - 1].Text, speakers[i]) is string hook)
                        prefix.Add(hook);
                    else if (attempt < 4 && random.NextDouble() < Settings.OwnStartChance)
                    {
                        int pool = PickPool(random);
                        var own = _speakerMsgs[Group(speakers[i], pool)];
                        if (own.Length == 0) own = _speakerMsgs[Group(speakers[i], 1 - pool)];
                        prefix.Add(_words[_msgFirst[own[random.Next(own.Length)]]]);
                    }
                    text = Line(random, speakers[i], prefix, topic);
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

        /// <summary>The ids of the seed words the server has used, leaving out filler when there's anything else; null when none.</summary>
        private int[] TopicOf(string[] seedWords)
        {
            var known = seedWords.Select(w => (Word: w, Id: Find(w.Trim('.', ',', '!', '?', '"', '\'')) ?? Find(w))).Where(x => x.Id != null).ToList();
            var meaningful = known.Where(x => !StopWords.Contains(x.Word)).ToList();
            var ids = (meaningful.Count > 0 ? meaningful : known).Select(x => x.Id.Value).Distinct().ToArray();
            return ids.Length > 0 ? ids : null;
        }

        /// <summary>Places anyone in the server led into a topic word (the word before it, and the word), sampled once per convo.</summary>
        private List<(int Before, int Word)> SharedLeadInsTo(int[] topic, Random random) =>
            _bi.SampleLeadingTo(0, topic, SharedLeadIns, random);

        /// <summary>
        /// The start of a line about the topic: up to two words the speaker really said before a topic word, then the word, from the
        /// pool picked for this line. If they never used any topic word, how someone else led into one; failing that, the word alone.
        /// </summary>
        private List<string> LeadInto(int[] topic, int speaker, Lazy<List<(int Before, int Word)>> sharedLeadIns, Random random)
        {
            int pool = PickPool(random);
            foreach (int group in new[] { Group(speaker, pool), Group(speaker, 1 - pool) })
            {
                var own = _speakerTri.SampleLeadingTo(group, topic, 1, random);
                if (own.Count > 0)
                {
                    var (key, word) = own[0];
                    return new[] { (int)(key >> 32), (int)key, word }.Where(t => t != StartToken).Select(t => _words[t]).ToList();
                }
            }
            var shared = sharedLeadIns.Value;
            if (shared.Count > 0)
            {
                var (before, word) = shared[random.Next(shared.Count)];
                return new[] { before, word }.Where(t => t != StartToken).Select(t => _words[t]).ToList();
            }
            return new List<string> { _words[topic[random.Next(topic.Length)]] };
        }

        /// <summary>The speaker's next word, only if one of <paramref name="wanted"/> can follow, from the pool picked for this step first.</summary>
        private bool TryPickOwnFrom<TKey>(Chain<TKey> chain, int speaker, TKey key, int[] wanted, Random random, out int next) where TKey : struct, IComparable<TKey>
        {
            int pool = PickPool(random);
            return chain.TryPickFrom(Group(speaker, pool), key, wanted, random, out next) || chain.TryPickFrom(Group(speaker, 1 - pool), key, wanted, random, out next);
        }

        /// <summary>Whether a line is a real message repeated word for word (short ones don't count).</summary>
        public bool IsCopy(string text) =>
            !string.IsNullOrEmpty(text) && Tokenize(text).Length > Settings.MaxCopiedWords && _realMessages.Contains(HashOf(string.Join(" ", Tokenize(text))));

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

        /// <summary>Where a run of speakers starts: a favoured channel's message <see cref="ConvoSettings.FavouredShare"/> of the time, if there are any.</summary>
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
        /// Everyone's chain is only used when they have no word to follow the last one. With a <paramref name="topic"/>, until the
        /// line mentions it, each step takes a topic word <see cref="TopicPull"/> of the time when the speaker has one to follow.
        /// </summary>
        private string Line(Random random, int speaker, IList<string> prefix, int[] topic = null)
        {
            bool mentioned = topic == null || prefix.Any(w => Find(w) is int t && Array.IndexOf(topic, t) >= 0);
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
                bool found = false;
                if (!mentioned && random.NextDouble() < Settings.TopicPull)
                    found = TryPickOwnFrom(_speakerTri, speaker, Pack(a, b), topic, random, out next)
                        || TryPickOwnFrom(_speakerBi, speaker, b, topic, random, out next);
                bool chaos = b != StartToken && random.NextDouble() < Settings.Chaos;
                if (!found)
                    found = !chaos && TryPickOwn(_speakerTri, speaker, Pack(a, b), random, out next);
                if (!found && !(chaos && rare))
                    found = TryPickOwn(_speakerBi, speaker, b, random, out next);
                if (!found && !_bi.TryPick(0, b, Settings.Temperature, random, out next))
                    next = EndToken;
                if (next == EndToken || next == StartToken)
                    break;
                string word = _words[next];
                if (length + word.Length + 1 > MaxLineChars)
                    break;
                words.Add(word);
                length += word.Length + 1;
                mentioned |= topic != null && Array.IndexOf(topic, next) >= 0;
                a = b;
                b = next;
            }
            return string.Join(" ", words).Trim();
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

            public bool TryPick(int group, TKey key, double temperature, Random random, out int next)
            {
                if (!Range(group, key, out int lo, out int hi))
                {
                    next = EndToken;
                    return false;
                }
                next = temperature <= 1.0 ? _next[lo + random.Next(hi - lo)] : PickWarm(lo, hi, temperature, random);
                return true;
            }

            /// <summary>
            /// A next word where each distinct word's weight is its count to the power 1/the temperature (<see cref="ConvoSettings.Temperature"/>), so rare follow-ups
            /// are likelier than they really were. Big ranges are judged on a random sample of <see cref="WarmSample"/> entries.
            /// </summary>
            private int PickWarm(int lo, int hi, double temperature, Random random)
            {
                var counts = new Dictionary<int, int>();
                int n = hi - lo;
                if (n <= WarmSample)
                    for (int i = lo; i < hi; i++)
                        counts[_next[i]] = counts.TryGetValue(_next[i], out int c) ? c + 1 : 1;
                else
                    for (int i = 0; i < WarmSample; i++)
                    {
                        int w = _next[lo + random.Next(n)];
                        counts[w] = counts.TryGetValue(w, out int c) ? c + 1 : 1;
                    }
                double total = 0;
                foreach (var count in counts.Values)
                    total += Math.Pow(count, 1.0 / temperature);
                double roll = random.NextDouble() * total;
                int last = EndToken;
                foreach (var pair in counts)
                {
                    last = pair.Key;
                    roll -= Math.Pow(pair.Value, 1.0 / temperature);
                    if (roll <= 0)
                        break;
                }
                return last;
            }

            /// <summary>A next word for the context that is one of <paramref name="wanted"/>, picked by how often each was said.</summary>
            public bool TryPickFrom(int group, TKey key, int[] wanted, Random random, out int next)
            {
                next = EndToken;
                if (!Range(group, key, out int lo, out int hi))
                    return false;
                int seen = 0;
                for (int i = lo; i < hi; i++)
                {
                    if (IsWanted(wanted, _next[i]) && random.Next(++seen) == 0)
                        next = _next[i];
                }
                return seen > 0;
            }

            /// <summary>Up to <paramref name="max"/> random (context, word) entries in a group whose word is one of <paramref name="wanted"/>.</summary>
            public List<(TKey Key, int Word)> SampleLeadingTo(int group, int[] wanted, int max, Random random)
            {
                var sample = new List<(TKey, int)>();
                int seen = 0;
                for (int i = _start[group]; i < _start[group + 1]; i++)
                {
                    if (!IsWanted(wanted, _next[i]))
                        continue;
                    seen++;
                    if (sample.Count < max)
                        sample.Add((_keys[i], _next[i]));
                    else if (random.Next(seen) is int slot && slot < max)
                        sample[slot] = (_keys[i], _next[i]);
                }
                return sample;
            }

            /// <summary>A topic is a word or three, so a plain loop beats a hash lookup in these scans.</summary>
            private static bool IsWanted(int[] wanted, int word)
            {
                for (int i = 0; i < wanted.Length; i++)
                    if (wanted[i] == word) return true;
                return false;
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
