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
        /// <summary>Channels `.convo` leans on: their messages count <see cref="FavouredWeight"/> times, and they're scraped first.</summary>
        public static readonly IReadOnlySet<ulong> FavouredChannelIds = new HashSet<ulong> { 187622109856399360, 213585904059678720 };
        public const int FavouredWeight = 3;
        public const string DataDir = "convo_data";
        public const string MessagesDir = "messages";
        public const string StateFile = "scrape_state.json";
        /// <summary>The most messages the model learns from, a favoured one counting <see cref="FavouredWeight"/>; with more, an even random sample is kept (memory on the Pi).</summary>
        public const int MaxMessages = 600_000;
        public const int MaxMessageChars = 300;
        /// <summary>Pause between pages so the scrape never crowds out the bot's own requests.</summary>
        public static readonly TimeSpan PagePause = TimeSpan.FromMilliseconds(250);
        private const int PageSize = 100;
        /// <summary>The quick first pass takes this many pages (the newest ~1,000 messages) of every channel, so `.convo` works within minutes.</summary>
        public const int QuickPassPages = 10;
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
        private bool _autoScrapeTried;
        private DateTime _lastReload = DateTime.MinValue;

        public ConvoCorpusService(ILogger<ConvoCorpusService> logger)
        {
            _logger = logger;
        }

        /// <summary>The model, or null until the history has been scraped and loaded.</summary>
        public ConvoModel Model => _model;
        public bool IsLoading => !_loadTask.IsCompleted;
        public bool IsScraping => _scraping;
        /// <summary>Messages saved by the scrape that's running (or the last one).</summary>
        public long SavedThisRun => Interlocked.Read(ref _savedThisRun);

        /// <summary>
        /// Whether to start the first scrape by itself: once per process, only when nothing was ever scraped (a later refresh is
        /// `.scrapeconvo`), the bot can see the server and no scrape is running.
        /// </summary>
        public static bool ShouldAutoScrape(bool stateExists, bool canSeeGuild, bool scraping, bool alreadyTried) =>
            !stateExists && canSeeGuild && !scraping && !alreadyTried;

        /// <summary>Called every minute: starts the very first scrape on its own once the bot is connected to the server.</summary>
        public void EnsureScrapeStarted(DiscordSocketClient client)
        {
            bool canSee = client.ConnectionState == ConnectionState.Connected && client.GetGuild(GuildId) != null;
            if (!ShouldAutoScrape(File.Exists(StatePath), canSee, _scraping, _autoScrapeTried))
                return;
            _autoScrapeTried = true;
            if (StartScrape(client, null))
                _logger.LogInformation("Convo scrape started by itself: nothing scraped yet");
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
                        var model = LoadFrom(DataPath, MaxMessages, new Random());
                        if (model.MessageCount > 0)
                            _model = model;
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
        public static ConvoModel LoadFrom(string dataPath, int maxMessages, Random random)
        {
            var dir = Path.Join(dataPath, MessagesDir);
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.tsv").OrderBy(f => f).ToArray() : Array.Empty<string>();
            long total = files.Sum(f => (long)File.ReadLines(f).Count() * WeightOf(f));
            double keep = total <= maxMessages ? 1.0 : (double)maxMessages / total;
            return ConvoModel.Build(Channels(files, keep, random));
        }

        /// <summary>A channel file's weight, from its name (the channel id).</summary>
        public static int WeightOf(string file) =>
            ulong.TryParse(Path.GetFileNameWithoutExtension(file), out ulong id) && FavouredChannelIds.Contains(id) ? FavouredWeight : 1;

        private static IEnumerable<IReadOnlyList<ConvoMessage>> Channels(string[] files, double keep, Random random)
        {
            foreach (var file in files)
            {
                int weight = WeightOf(file);
                var seen = new HashSet<ulong>();
                var messages = new List<ConvoMessage>();
                foreach (var line in File.ReadLines(file))
                {
                    var message = ParseLine(line);
                    if (message == null || !seen.Add(message.Id) || (keep < 1 && random.NextDouble() >= keep))
                        continue;
                    message.Weight = weight;
                    messages.Add(message);
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
            string text = message is IUserMessage user
                ? user.Resolve(TagHandling.Name, TagHandling.Name, TagHandling.Name, TagHandling.Remove, TagHandling.Ignore)
                : message.Content;
            return Clean(text);
        }

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
            public bool NoAccess { get; set; }
            public long Saved { get; set; }
        }

        private static string StatePath => Path.Join(DataPath, StateFile);

        private static Dictionary<ulong, ChannelProgress> LoadState() =>
            File.Exists(StatePath)
                ? JsonConvert.DeserializeObject<Dictionary<ulong, ChannelProgress>>(File.ReadAllText(StatePath)) ?? new Dictionary<ulong, ChannelProgress>()
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
            return $"{scrapeText}\nSaved: {saved:N0} messages, {done}/{state.Count} channels finished.\n{modelText}";
        }

        /// <summary>Starts a scrape in the background and returns at once; the result is posted to <paramref name="reportTo"/> (or only logged). False when one is already running.</summary>
        public bool StartScrape(DiscordSocketClient client, IMessageChannel reportTo)
        {
            lock (_gate)
            {
                if (_scraping)
                    return false;
                _scraping = true;
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
                }

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

            //first the newest messages of every channel, so .convo has something from everywhere within minutes, then all the way back
            _pass = "quick pass";
            await ScrapeChannels(channels, state, QuickPassPages, token);
            if (!token.IsCancellationRequested)
            {
                lock (_gate) _loadStarted = true;
                _lastReload = DateTime.UtcNow;
                _ = StartReload();
            }
            _pass = "full history";
            await ScrapeChannels(channels, state, int.MaxValue, token);
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

        private void Save(string file, List<IMessage> page, ChannelProgress progress)
        {
            var lines = new List<string>();
            foreach (var message in page)
            {
                string text = Extract(message);
                if (text != null)
                    lines.Add(ToLine(message.Id, message.Author.Username, text));
            }
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
