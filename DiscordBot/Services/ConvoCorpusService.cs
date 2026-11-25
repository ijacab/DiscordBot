using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using DiscordBot.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    /// <summary>
    /// The server history behind `.convo`. `.scrapeconvo` (bot admins) downloads every message the bot can read in
    /// <see cref="GuildId"/>, except <see cref="ExcludedChannelId"/>, in the background, into <see cref="DataDir"/> on the Pi (one
    /// file per channel, never uploaded anywhere), saving its place after every page so it can stop and pick up again, and fetching
    /// only new messages on later runs. The <see cref="ConvoModel"/> is built from those files at startup and after each scrape.
    /// </summary>
    public class ConvoCorpusService
    {
        public const ulong GuildId = 187621924342333441;
        public const ulong ExcludedChannelId = 871960465775935568;
        /// <summary>Channels `.convo` leans on: they make up <see cref="FavouredShare"/> of it, are kept whole under the cap and are scraped first.</summary>
        public static readonly IReadOnlySet<ulong> FavouredChannelIds = new HashSet<ulong> { 187622109856399360, 213585904059678720 };
        public const double FavouredShare = ConvoModel.DefaultFavouredShare;
        public const string DataDir = "convo_data";
        public const string MessagesDir = "messages";
        public const string StateFile = "scrape_state.json";
        /// <summary>The most messages the model learns from; with more, the favoured channels are kept whole and the rest sampled (memory on the Pi).</summary>
        public const int MaxMessages = 600_000;
        public const int MaxMessageChars = 300;
        /// <summary>Pause between pages so the scrape never crowds out the bot's own requests.</summary>
        public static readonly TimeSpan PagePause = TimeSpan.FromMilliseconds(250);
        private const int PageSize = 100;
        public const string SamplesDir = "samples";
        /// <summary>
        /// The first pass reads this many pages (100 messages each) taken at random moments across a channel's whole history, so
        /// `.convo` works within minutes without leaning on whatever was posted most recently. Favoured channels read more.
        /// </summary>
        public const int SamplePages = 20;
        public const int FavouredSamplePages = 100;
        /// <summary>How often the model is rebuilt while a scrape runs, so `.convo` keeps learning as the history comes in.</summary>
        public static readonly TimeSpan ReloadDuringScrape = TimeSpan.FromMinutes(15);

        private readonly ILogger<ConvoCorpusService> _logger;
        private readonly object _gate = new object();
        private volatile ConvoModel _model;
        private bool _loadStarted;
        private Task _loadTask = Task.CompletedTask;

        private CancellationTokenSource _scrapeCts;
        private bool _scraping;
        private int _channelIndex;
        private int _channelTotal;
        private string _currentChannel;
        private long _savedThisRun;
        private string _pass;
        private DateTime _autoScrapeNotBefore = DateTime.MinValue;
        private volatile bool _stoppedByAdmin;
        /// <summary>After a scrape ends with channels still unfinished (an error, a channel that failed), it tries again after this long.</summary>
        public static readonly TimeSpan AutoScrapeRetry = TimeSpan.FromMinutes(10);
        private DateTime _lastReload = DateTime.MinValue;

        public ConvoCorpusService(ILogger<ConvoCorpusService> logger)
        {
            _logger = logger;
        }

        /// <summary>The knobs `.convoset` changes, loaded from <see cref="ConvoSettings.FileName"/>. The same object is handed to every model, so a change applies to the next `.convo`.</summary>
        public ConvoSettings Settings { get; } = ConvoSettings.Load(ConvoSettings.DefaultPath);

        /// <summary>Writes the current settings to their file so they survive a restart or deploy.</summary>
        public void SaveSettings() => Settings.Save(ConvoSettings.DefaultPath);

        /// <summary>The model, or null until the history has been scraped and loaded.</summary>
        public ConvoModel Model => _model;
        public bool IsLoading => !_loadTask.IsCompleted;
        public bool IsScraping => _scraping;
        /// <summary>What the last model build read.</summary>
        private volatile LoadReport _loadReport;
        /// <summary>Messages saved by the scrape that's running (or the last one).</summary>
        public long SavedThisRun => Interlocked.Read(ref _savedThisRun);

        /// <summary>
        /// Whether to start a scrape by itself: once per process, when nothing was ever scraped or an earlier scrape was cut off
        /// (every deploy restarts the bot, which kills a scrape that takes hours) and some channel is still unfinished, the bot can
        /// see the server and no scrape is running. A later refresh for new messages is `.scrapeconvo`.
        /// </summary>
        public static bool ShouldAutoScrape(bool stateExists, bool unfinishedChannels, bool canSeeGuild, bool scraping, bool alreadyTried) =>
            (!stateExists || unfinishedChannels) && canSeeGuild && !scraping && !alreadyTried;

        /// <summary>A channel the scrape has found but not finished (neither all saved nor out of reach).</summary>
        public static bool HasUnfinishedChannels(IEnumerable<ChannelProgress> channels) => channels.Any(p => !p.Done && !p.NoAccess);

        /// <summary>Called every minute: starts (or, after a restart, resumes) the scrape on its own once the bot is connected to the server.</summary>
        public void EnsureScrapeStarted(DiscordSocketClient client)
        {
            bool canSee = client.ConnectionState == ConnectionState.Connected && client.GetGuild(GuildId) != null;
            bool waiting = _stoppedByAdmin || DateTime.UtcNow < _autoScrapeNotBefore;
            if (!ShouldAutoScrape(File.Exists(StatePath), canSee && HasUnfinishedChannels(LoadState().Values), canSee, _scraping, waiting))
                return;
            _autoScrapeNotBefore = DateTime.UtcNow + AutoScrapeRetry;
            if (StartScrape(client, null))
                _logger.LogInformation("Convo scrape started by itself: the history isn't all saved yet");
        }

        public static string DataPath => Path.Join(Directory.GetCurrentDirectory(), DataDir);

        /// <summary>Starts loading the model once, in the background, if anything has been scraped. Called from the minute loop.</summary>
        public void EnsureLoadStarted()
        {
            lock (_gate)
            {
                if (_loadStarted || !Directory.Exists(Path.Join(DataPath, MessagesDir)))
                    return;
                _loadStarted = true;
            }
            StartReload();
        }

        /// <summary>Rebuilds the model from the files in the background; the old one keeps answering until the new one is ready.</summary>
        public Task StartReload()
        {
            lock (_gate)
            {
                if (!_loadTask.IsCompleted)
                    return _loadTask;
                _loadTask = Task.Run(() =>
                {
                    try
                    {
                        var report = new LoadReport();
                        var model = LoadFrom(DataPath, MaxMessages, new Random(), report, Settings);
                        if (model.MessageCount > 0)
                        {
                            _model = model;
                            _loadReport = report;
                        }
                        _logger.LogInformation("Convo model sources: {Report}", report);
                        _logger.LogInformation("Convo model loaded: {Messages} messages, {Speakers} people", model.MessageCount, model.SpeakerCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Loading the convo model failed");
                    }
                });
                return _loadTask;
            }
        }

        #region files

        /// <summary>One saved message: "id\tusername\ttext".</summary>
        public static string ToLine(ulong id, string speaker, string text) => $"{id}\t{OneLine(speaker)}\t{OneLine(text)}";

        public static ConvoMessage ParseLine(string line)
        {
            var parts = line.Split('\t', 3);
            if (parts.Length < 3 || !ulong.TryParse(parts[0], out ulong id) || parts[1].Length == 0 || parts[2].Length == 0)
                return null;
            return new ConvoMessage { Id = id, Speaker = parts[1], Text = parts[2] };
        }

        private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

        /// <summary>
        /// Reads every channel file in <paramref name="dataPath"/>, drops repeats (a page saved twice after a restart), puts each
        /// channel in time order (message ids are) and builds the model, keeping an even random sample when there are more than
        /// <paramref name="maxMessages"/>.
        /// </summary>
        public static ConvoModel LoadFrom(string dataPath, int maxMessages, Random random, LoadReport report = null, ConvoSettings settings = null)
        {
            var files = SourceFiles(dataPath);
            long favoured = files.Where(IsFavoured).Sum(f => (long)File.ReadLines(f).Count());
            long others = files.Where(f => !IsFavoured(f)).Sum(f => (long)File.ReadLines(f).Count());
            //the favoured channels are kept whole (up to the cap), and the rest are sampled into what's left
            double keepFavoured = favoured <= maxMessages ? 1.0 : (double)maxMessages / favoured;
            long room = Math.Max(0, maxMessages - (long)(favoured * keepFavoured));
            double keepOthers = others <= room ? 1.0 : (double)room / others;
            report?.Start(files, favoured + others, favoured <= maxMessages && others <= room);
            var model = ConvoModel.Build(Channels(files, keepFavoured, keepOthers, random, report), settings?.FavouredShare ?? FavouredShare);
            if (settings != null) model.Settings = settings;
            return model;
        }

        /// <summary>
        /// What a model build actually read: how many messages, from how many files (full histories or random samples), and how they
        /// spread over time, so "is it only recent chat?" can be answered with numbers (`.scrapeconvo status`).
        /// </summary>
        public sealed class LoadReport
        {
            private static readonly DateTime DiscordEpoch = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            private long _inLast30Days, _inLast365Days;
            private readonly DateTime _now = DateTime.UtcNow;
            public int Files { get; private set; }
            public int SampleFiles { get; private set; }
            public long OnDisk { get; private set; }
            public bool KeptEverything { get; private set; }
            public long Messages { get; private set; }
            public long FavouredMessages { get; private set; }
            public DateTime Oldest { get; private set; } = DateTime.MaxValue;
            public DateTime Newest { get; private set; } = DateTime.MinValue;

            public static DateTime TimeOf(ulong id) => DiscordEpoch.AddMilliseconds(id >> 22);

            internal void Start(string[] files, long onDisk, bool keptEverything)
            {
                Files = files.Length;
                SampleFiles = files.Count(f => Path.GetFileName(Path.GetDirectoryName(f)) == SamplesDir);
                OnDisk = onDisk;
                KeptEverything = keptEverything;
            }

            internal void Add(ConvoMessage message, bool favoured)
            {
                var time = TimeOf(message.Id);
                Messages++;
                if (favoured) FavouredMessages++;
                if (time < Oldest) Oldest = time;
                if (time > Newest) Newest = time;
                if (_now - time <= TimeSpan.FromDays(30)) _inLast30Days++;
                if (_now - time <= TimeSpan.FromDays(365)) _inLast365Days++;
            }

            public double ShareLast30Days => Messages == 0 ? 0 : _inLast30Days / (double)Messages;
            public double ShareLastYear => Messages == 0 ? 0 : _inLast365Days / (double)Messages;

            public override string ToString() => Messages == 0
                ? "Nothing read yet."
                : $"Built from {Messages:N0} of {OnDisk:N0} saved messages ({Files} files, {SampleFiles} of them random samples{(KeptEverything ? "" : ", randomly thinned to the cap")}), "
                  + $"{FavouredMessages:N0} from favoured channels. They run from {Oldest:yyyy-MM-dd} to {Newest:yyyy-MM-dd}: "
                  + $"{ShareLast30Days * 100:0}% from the last 30 days, {ShareLastYear * 100:0}% from the last year.";
        }

        /// <summary>
        /// The file each channel is read from. A channel whose history is all saved uses its full file. A channel still being scraped
        /// has only its newest messages saved (the scrape reads newest first), and learning from that would make `.convo` lean on
        /// whatever was posted lately, so it uses its random sample from across its whole history instead (and its partial file only
        /// when it has no sample yet). With no scrape state at all, every channel file is read as it is.
        /// </summary>
        public static string[] SourceFiles(string dataPath)
        {
            var messagesDir = Path.Join(dataPath, MessagesDir);
            var samplesDir = Path.Join(dataPath, SamplesDir);
            var state = LoadStateFrom(Path.Join(dataPath, StateFile));
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var dir in new[] { messagesDir, samplesDir })
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.tsv")) ids.Add(Path.GetFileNameWithoutExtension(f));

            var files = new List<string>();
            foreach (var id in ids)
            {
                string full = Path.Join(messagesDir, id + ".tsv");
                string sample = Path.Join(samplesDir, id + ".tsv");
                bool partial = ulong.TryParse(id, out ulong channelId) && state.TryGetValue(channelId, out var progress) && !progress.Done;
                string chosen = partial && File.Exists(sample) ? sample : File.Exists(full) ? full : null;
                if (chosen != null) files.Add(chosen);
            }
            return files.ToArray();
        }

        /// <summary>A random message id at a random moment between two ids, so a window read around it is a random part of the history.</summary>
        public static ulong RandomMessageId(ulong low, ulong high, Random random)
        {
            if (high <= low) return low;
            return low + (ulong)(random.NextDouble() * (high - low));
        }

        /// <summary>Whether a channel file (named by its channel id) is one of <see cref="FavouredChannelIds"/>.</summary>
        public static bool IsFavoured(string file) =>
            ulong.TryParse(Path.GetFileNameWithoutExtension(file), out ulong id) && FavouredChannelIds.Contains(id);

        private static IEnumerable<IReadOnlyList<ConvoMessage>> Channels(string[] files, double keepFavoured, double keepOthers, Random random, LoadReport report = null)
        {
            foreach (var file in files)
            {
                bool favoured = IsFavoured(file);
                double keep = favoured ? keepFavoured : keepOthers;
                var seen = new HashSet<ulong>();
                var messages = new List<ConvoMessage>();
                foreach (var line in File.ReadLines(file))
                {
                    var message = ParseLine(line);
                    if (message == null || !seen.Add(message.Id) || (keep < 1 && random.NextDouble() >= keep))
                        continue;
                    message.Favoured = favoured;
                    messages.Add(message);
                    report?.Add(message, favoured);
                }
                messages.Sort((x, y) => x.Id.CompareTo(y.Id));
                yield return messages;
            }
        }

        /// <summary>
        /// A message's text as the model should learn it, or null to leave it out: no bots, webhooks or system messages, no bot
        /// commands, no code blocks, links removed, mentions as plain @names (they can't ping), all on one line.
        /// </summary>
        public static string Extract(IMessage message)
        {
            if (message.Author == null || message.Author.IsBot || message.Author.IsWebhook)
                return null;
            if (message.Type != MessageType.Default && message.Type != MessageType.Reply)
                return null;
            string text = message.Content;
            if (message is IUserMessage user)
            {
                //mentions as plain text: people by their display name (nickname, else global name) rather than their username
                foreach (var tag in user.Tags.OrderByDescending(t => t.Index))
                {
                    string replacement = tag.Type switch
                    {
                        TagType.UserMention when tag.Value is IUser person => "@" + DisplayNameOf(person),
                        TagType.RoleMention when tag.Value is IRole role => "@" + role.Name,
                        TagType.ChannelMention when tag.Value is IChannel channel => "#" + channel.Name,
                        TagType.EveryoneMention or TagType.HereMention => "",
                        TagType.UserMention or TagType.RoleMention or TagType.ChannelMention => "",
                        _ => null, //emoji stay as they are
                    };
                    if (replacement != null && tag.Index >= 0 && tag.Index + tag.Length <= text.Length)
                        text = text.Remove(tag.Index, tag.Length).Insert(tag.Index, replacement);
                }
            }
            return Clean(text);
        }

        /// <summary>What people see for this user in the server: their nickname there, else their display name, else their username.</summary>
        public static string DisplayNameOf(IUser user) =>
            FirstNonEmpty((user as IGuildUser)?.Nickname, user.GlobalName, user.Username);

        private static string FirstNonEmpty(params string[] names) => names.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "someone";

        public static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Contains("```"))
                return null;
            text = Regex.Replace(text, @"https?://\S+|discord\.gg/\S+", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"@(everyone|here)\b", " ", RegexOptions.IgnoreCase);
            text = OneLine(text);
            if (text.Length == 0 || Regex.IsMatch(text, @"^[.!$/;][A-Za-z]"))
                return null;
            if (text.Length > MaxMessageChars)
            {
                int cut = text.LastIndexOf(' ', MaxMessageChars);
                text = text[..(cut > 0 ? cut : MaxMessageChars)];
            }
            return text;
        }

        public class ChannelProgress
        {
            public string Name { get; set; }
            /// <summary>The oldest message saved so far; the next page reads before it.</summary>
            public ulong OldestId { get; set; }
            /// <summary>The newest message saved; a later scrape reads after it.</summary>
            public ulong NewestId { get; set; }
            /// <summary>The whole history back to the first message is saved.</summary>
            public bool Done { get; set; }
            /// <summary>The random sample across the whole history (<see cref="SamplesDir"/>) has been read.</summary>
            public bool Sampled { get; set; }
            public bool NoAccess { get; set; }
            public long Saved { get; set; }
        }

        private static string StatePath => Path.Join(DataPath, StateFile);

        private static Dictionary<ulong, ChannelProgress> LoadState() => LoadStateFrom(StatePath);

        private static Dictionary<ulong, ChannelProgress> LoadStateFrom(string path) =>
            File.Exists(path)
                ? JsonConvert.DeserializeObject<Dictionary<ulong, ChannelProgress>>(File.ReadAllText(path)) ?? new Dictionary<ulong, ChannelProgress>()
                : new Dictionary<ulong, ChannelProgress>();

        private static void SaveState(Dictionary<ulong, ChannelProgress> state)
        {
            string tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(state, Formatting.Indented));
            File.Move(tmp, StatePath, true);
        }

        #endregion

        #region scrape

        public string Status()
        {
            var state = LoadState();
            long saved = state.Values.Sum(p => p.Saved);
            int done = state.Values.Count(p => p.Done);
            var model = _model;
            string modelText = model == null
                ? (IsLoading ? "The `.convo` model is loading." : "No `.convo` model yet.")
                : $"`.convo` knows {model.MessageCount:N0} messages from {model.SpeakerCount:N0} people{(IsLoading ? " (reloading)" : "")}.";
            string scrapeText = _scraping
                ? $"Scraping ({_pass}) channel {_channelIndex}/{_channelTotal} (#{_currentChannel}), {SavedThisRun:N0} new messages this run."
                : "Not scraping.";
            string sources = _loadReport == null ? "" : $"\n{_loadReport}";
            return $"{scrapeText}\nSaved: {saved:N0} messages, {done}/{state.Count} channels finished.\n{modelText}{sources}";
        }

        /// <summary>Starts a scrape in the background and returns at once; the result is posted to <paramref name="reportTo"/> (or only logged). False when one is already running.</summary>
        public bool StartScrape(DiscordSocketClient client, IMessageChannel reportTo)
        {
            lock (_gate)
            {
                if (_scraping)
                    return false;
                _scraping = true;
                _stoppedByAdmin = false;
                _scrapeCts = new CancellationTokenSource();
            }
            var token = _scrapeCts.Token;
            _ = Task.Run(async () =>
            {
                string summary;
                try
                {
                    await Scrape(client, token);
                    summary = token.IsCancellationRequested
                        ? $"Scrape stopped. {_savedThisRun:N0} new messages saved; `.scrapeconvo` carries on from here."
                        : $"Scrape finished: {_savedThisRun:N0} new messages saved.";
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Convo scrape failed");
                    summary = $"Scrape failed: {ex.Message}. `.scrapeconvo` carries on from where it got to.";
                }
                finally
                {
                    lock (_gate) _scraping = false;
                    _autoScrapeNotBefore = DateTime.UtcNow + AutoScrapeRetry;
                }

                int unfinished = LoadState().Values.Count(p => !p.Done && !p.NoAccess);
                if (unfinished > 0 && !_stoppedByAdmin)
                    summary += $" {unfinished} channels aren't fully saved yet, so it carries on by itself in {AutoScrapeRetry.TotalMinutes:0} minutes.";
                lock (_gate) _loadStarted = true;
                await StartReload();
                _logger.LogInformation("Convo scrape: {Summary}", summary);
                if (reportTo != null)
                    await Say(reportTo, $"{summary}\n{Status()}");
            });
            return true;
        }

        public bool StopScrape()
        {
            lock (_gate)
            {
                if (!_scraping)
                    return false;
                _stoppedByAdmin = true;
                _scrapeCts.Cancel();
                return true;
            }
        }

        private async Task Say(IMessageChannel channel, string text)
        {
            try
            {
                await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't post the convo scrape result");
            }
        }

        private async Task Scrape(DiscordSocketClient client, CancellationToken token)
        {
            _savedThisRun = 0;
            var guild = client.GetGuild(GuildId) ?? throw new InvalidOperationException($"the bot isn't in server {GuildId}");
            Directory.CreateDirectory(Path.Join(DataPath, MessagesDir));
            var state = LoadState();
            var channels = await ListChannels(guild, token);
            _channelTotal = channels.Count;
            _logger.LogInformation("Convo scrape: {Count} channels", channels.Count);

            //first a random sample from across every channel's whole history, so .convo has something unbiased from everywhere within
            //minutes (not just the newest messages), then all the way back; a channel is only used in full once all of it is saved
            _pass = "random sample";
            await SampleChannels(channels, state, token);
            if (!token.IsCancellationRequested)
            {
                lock (_gate) _loadStarted = true;
                _lastReload = DateTime.UtcNow;
                _ = StartReload();
            }
            _pass = "full history";
            await ScrapeChannels(channels, state, int.MaxValue, token);
        }

        private async Task SampleChannels(List<IMessageChannel> channels, Dictionary<ulong, ChannelProgress> state, CancellationToken token)
        {
            _channelIndex = 0;
            foreach (var channel in channels)
            {
                if (token.IsCancellationRequested)
                    break;
                _channelIndex++;
                _currentChannel = channel.Name;
                if (!state.TryGetValue(channel.Id, out var progress))
                    state[channel.Id] = progress = new ChannelProgress();
                progress.Name = channel.Name;
                if (progress.NoAccess || progress.Sampled || progress.Done)
                    continue;
                try
                {
                    await SampleChannel(channel, progress, state, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.Forbidden || ex.HttpCode == HttpStatusCode.NotFound)
                {
                    progress.NoAccess = true;
                    SaveState(state);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Convo sample: #{Channel} failed", channel.Name);
                }
            }
            SaveState(state);
        }

        /// <summary>Reads windows of messages around random moments between the channel's creation and now, and saves them apart from the full history.</summary>
        private async Task SampleChannel(IMessageChannel channel, ChannelProgress progress, Dictionary<ulong, ChannelProgress> state, CancellationToken token)
        {
            Directory.CreateDirectory(Path.Join(DataPath, SamplesDir));
            string file = Path.Join(DataPath, SamplesDir, $"{channel.Id}.tsv");
            var options = new RequestOptions { CancelToken = token, RetryMode = RetryMode.AlwaysRetry };
            ulong low = channel.Id; //a channel's id is when it was made, and nothing was posted before that
            ulong high = SnowflakeUtils.ToSnowflake(DateTimeOffset.UtcNow);
            int pages = FavouredChannelIds.Contains(channel.Id) ? FavouredSamplePages : SamplePages;
            var seen = new HashSet<ulong>();
            for (int i = 0; i < pages && !token.IsCancellationRequested; i++)
            {
                ulong around = RandomMessageId(low, high, Random.Shared);
                var page = (await channel.GetMessagesAsync(around, Direction.Around, PageSize, CacheMode.AllowDownload, options).FlattenAsync())
                    .Where(m => seen.Add(m.Id)).ToList();
                var lines = LinesOf(page);
                if (lines.Count > 0)
                    File.AppendAllLines(file, lines);
                await Task.Delay(PagePause, token);
            }
            if (!token.IsCancellationRequested)
            {
                progress.Sampled = true;
                SaveState(state);
            }
        }

        private async Task ScrapeChannels(List<IMessageChannel> channels, Dictionary<ulong, ChannelProgress> state, int maxPages, CancellationToken token)
        {
            _channelIndex = 0;
            foreach (var channel in channels)
            {
                if (token.IsCancellationRequested)
                    break;
                _channelIndex++;
                _currentChannel = channel.Name;
                if (!state.TryGetValue(channel.Id, out var progress))
                    state[channel.Id] = progress = new ChannelProgress();
                progress.Name = channel.Name;
                if (progress.NoAccess)
                    continue;
                try
                {
                    await ScrapeChannel(channel, progress, state, maxPages, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.Forbidden || ex.HttpCode == HttpStatusCode.NotFound)
                {
                    progress.NoAccess = true;
                    SaveState(state);
                }
                catch (Exception ex)
                {
                    //try this channel again on the next scrape; the rest carry on
                    _logger.LogWarning(ex, "Convo scrape: #{Channel} failed", channel.Name);
                }
            }
            SaveState(state);
        }

        private async Task ScrapeChannel(IMessageChannel channel, ChannelProgress progress, Dictionary<ulong, ChannelProgress> state, int maxPages, CancellationToken token)
        {
            string file = Path.Join(DataPath, MessagesDir, $"{channel.Id}.tsv");
            var options = new RequestOptions { CancelToken = token, RetryMode = RetryMode.AlwaysRetry };

            //back through the history to the first message (or only a few pages on the quick pass)
            for (int pages = 0; pages < maxPages && !progress.Done && !token.IsCancellationRequested; pages++)
            {
                var page = (progress.OldestId == 0
                    ? await channel.GetMessagesAsync(PageSize, CacheMode.AllowDownload, options).FlattenAsync()
                    : await channel.GetMessagesAsync(progress.OldestId, Direction.Before, PageSize, CacheMode.AllowDownload, options).FlattenAsync()).ToList();
                if (page.Count > 0)
                {
                    Save(file, page, progress);
                    progress.OldestId = page.Min(m => m.Id);
                    progress.NewestId = Math.Max(progress.NewestId, page.Max(m => m.Id));
                }
                if (page.Count < PageSize)
                    progress.Done = true;
                SaveState(state);
                ReloadIfDue();
                await Task.Delay(PagePause, token);
            }

            //then anything posted since the last scrape
            while (progress.Done && progress.NewestId != 0 && !token.IsCancellationRequested)
            {
                var page = (await channel.GetMessagesAsync(progress.NewestId, Direction.After, PageSize, CacheMode.AllowDownload, options).FlattenAsync())
                    .Where(m => m.Id > progress.NewestId).ToList();
                if (page.Count == 0)
                    break;
                Save(file, page, progress);
                progress.NewestId = page.Max(m => m.Id);
                SaveState(state);
                if (page.Count < PageSize)
                    break;
                await Task.Delay(PagePause, token);
            }
        }

        /// <summary>Rebuilds the model in the background every <see cref="ReloadDuringScrape"/> while the history comes in.</summary>
        private void ReloadIfDue()
        {
            if (DateTime.UtcNow - _lastReload < ReloadDuringScrape)
                return;
            _lastReload = DateTime.UtcNow;
            lock (_gate) _loadStarted = true;
            _ = StartReload();
        }

        private static List<string> LinesOf(IEnumerable<IMessage> page)
        {
            var lines = new List<string>();
            foreach (var message in page)
            {
                string text = Extract(message);
                if (text != null)
                    lines.Add(ToLine(message.Id, message.Author.Username, text));
            }
            return lines;
        }

        private void Save(string file, List<IMessage> page, ChannelProgress progress)
        {
            var lines = LinesOf(page);
            if (lines.Count == 0)
                return;
            File.AppendAllLines(file, lines);
            progress.Saved += lines.Count;
            Interlocked.Add(ref _savedThisRun, lines.Count);
        }

        /// <summary>True for the excluded channel, anything in it (a category's channels, a channel's threads) and its threads' parents.</summary>
        public static bool IsExcluded(ulong channelId, ulong? parentId, ulong? grandparentId) =>
            channelId == ExcludedChannelId || parentId == ExcludedChannelId || grandparentId == ExcludedChannelId;

        private static bool IsExcluded(SocketGuild guild, IGuildChannel channel)
        {
            ulong? parent = (channel as INestedChannel)?.CategoryId;
            if (channel is SocketThreadChannel socketThread)
                parent = socketThread.ParentChannel?.Id ?? parent;
            ulong? grandparent = parent.HasValue ? (guild.GetChannel(parent.Value) as INestedChannel)?.CategoryId : null;
            return IsExcluded(channel.Id, parent, grandparent);
        }

        /// <summary>Every channel with messages: text, news and voice chats, open threads, and the archived public threads of text and forum channels.</summary>
        private async Task<List<IMessageChannel>> ListChannels(SocketGuild guild, CancellationToken token)
        {
            var found = new Dictionary<ulong, IMessageChannel>();
            foreach (var channel in guild.TextChannels.Concat<SocketGuildChannel>(guild.ThreadChannels))
            {
                if (channel is IMessageChannel messages && !IsExcluded(guild, channel))
                    found[channel.Id] = messages;
            }

            var parents = guild.TextChannels.Where(c => c is not SocketThreadChannel && c is not SocketVoiceChannel)
                .Select(c => (Channel: (SocketGuildChannel)c, Archived: (Func<DateTimeOffset?, Task<IReadOnlyCollection<RestThreadChannel>>>)(before => c.GetPublicArchivedThreadsAsync(PageSize, before, null))))
                .Concat(guild.ForumChannels.Select(f => (Channel: (SocketGuildChannel)f, Archived: (Func<DateTimeOffset?, Task<IReadOnlyCollection<RestThreadChannel>>>)(before => f.GetPublicArchivedThreadsAsync(PageSize, before, null)))));
            foreach (var (parent, archived) in parents)
            {
                if (token.IsCancellationRequested || IsExcluded(guild, parent))
                    continue;
                try
                {
                    DateTimeOffset? before = null;
                    while (!token.IsCancellationRequested)
                    {
                        var threads = await archived(before);
                        foreach (var thread in threads)
                            found.TryAdd(thread.Id, thread);
                        if (threads.Count < PageSize)
                            break;
                        before = threads.Min(t => t.ArchiveTimestamp);
                        await Task.Delay(PagePause, token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogInformation("Convo scrape: no archived threads for #{Channel}: {Message}", parent.Name, ex.Message);
                }
            }
            //the favoured channels first, so their history is in before the rest
            return found.Values.OrderBy(c => FavouredChannelIds.Contains(c.Id) ? 0 : 1).ToList();
        }

        #endregion
    }
}
