using Common.Helpers;
using Common.Services;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using DiscordBot.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    /// <summary>
    /// <c>.stocksthread</c>: a thread with one live message, a line per stock and one image with every stock's chart (all time on
    /// the left, the last 3 hours on the right, like `.stocks`), edited in place after every 5 minute stock tick. One thread per
    /// server. The thread and message ids are saved in stockthreads.json, so updates carry on after a restart or deploy.
    /// </summary>
    public class StockThreadService
    {
        private const string _fileName = "stockthreads.json";
        public const string ThreadName = "📈 Live stocks";
        /// <summary>The window of the second set of tickers.</summary>
        public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(3);

        private readonly LocalFileService _fileService;
        private readonly StockService _stockService;
        private readonly ILogger<StockThreadService> _logger;
        //one start, stop or refresh at a time, so the saved state is never edited by two at once
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly StockThreads _state;

        public StockThreadService(LocalFileService fileService, StockService stockService, ILogger<StockThreadService> logger)
        {
            _fileService = fileService;
            _stockService = stockService;
            _logger = logger;
            _state = _fileService.GetContent<StockThreads>(_fileName).Result ?? new StockThreads();
            _state.Threads ??= new List<LiveStockThread>();
        }

        /// <summary>Opens a new ticker thread in the channel. Any earlier one in the server stops updating and is pointed at the new one.</summary>
        public async Task<IThreadChannel> StartAsync(DiscordSocketClient client, ITextChannel channel)
        {
            await _gate.WaitAsync();
            try
            {
                //old threads go first, so the clean-up never deletes the one about to be made
                await CleanUpIfDue(client);
                IThreadChannel thread = await channel.CreateThreadAsync(ThreadName, ThreadType.PublicThread, ThreadArchiveDuration.OneWeek);
                var (stocks, slot) = _stockService.SnapshotForCharts();
                var entry = new LiveStockThread { GuildId = channel.GuildId, ParentChannelId = channel.Id, ThreadId = thread.Id, LastSlot = slot };
                await PostBoard(thread, entry, stocks, slot);

                var old = _state.Threads.FirstOrDefault(t => t.GuildId == channel.GuildId);
                if (old != null)
                {
                    _state.Threads.Remove(old);
                    await DeleteQuietly(client, old.ThreadId);
                }
                _state.Threads.Add(entry);
                Save();
                return thread;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Stops the server's ticker thread and deletes it. False if there wasn't one.</summary>
        public async Task<bool> StopAsync(DiscordSocketClient client, IGuild guild)
        {
            await _gate.WaitAsync();
            try
            {
                var entry = _state.Threads.FirstOrDefault(t => t.GuildId == guild.Id);
                if (entry == null) return false;
                _state.Threads.Remove(entry);
                Save();
                await DeleteQuietly(client, entry.ThreadId);
                return true;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Redraws every ticker thread that is behind the latest stock tick. Safe to call every minute: it returns at once if
        /// nothing changed, or if another start, stop or refresh is still running.
        /// </summary>
        public async Task RefreshIfDue(DiscordSocketClient client)
        {
            if (!await _gate.WaitAsync(0)) return;
            try
            {
                if (!await CleanUpIfDue(client)) return;
                if (_state.Threads.Count == 0) return;
                var (stocks, slot) = _stockService.SnapshotForCharts();
                bool changed = false;
                foreach (var entry in _state.Threads.ToList())
                {
                    if (entry.LastSlot == slot) continue;
                    try
                    {
                        changed |= await Refresh(client, entry, stocks, slot);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error refreshing the live stock thread {entry.ThreadId}: {ex.Message}");
                    }
                }
                if (changed) Save();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error refreshing live stock threads: {ex.Message}");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Redraws one thread. Returns true when the saved state changed.</summary>
        private async Task<bool> Refresh(DiscordSocketClient client, LiveStockThread entry, List<Stock> stocks, long slot)
        {
            IThreadChannel thread;
            try
            {
                thread = await client.GetChannelAsync(entry.ThreadId) as IThreadChannel;
            }
            catch (HttpException ex) when (IsGone(ex))
            {
                thread = null;
            }
            if (thread == null)
            {
                //the thread was deleted, or the bot can no longer see it
                _state.Threads.Remove(entry);
                return true;
            }

            try
            {
                if (thread.IsArchived)
                    await thread.ModifyAsync(p => p.Archived = false);

                bool repost = entry.IsOldLayout;
                if (repost)
                {
                    //made before the single-image board: its header and one message per stock go
                    foreach (ulong id in entry.Tickers.Select(t => t.MessageId).Append(entry.HeaderMessageId).Where(id => id != 0))
                    {
                        try { await thread.DeleteMessageAsync(id); }
                        catch (Exception) { /* already gone */ }
                    }
                    entry.Tickers = new List<TickerMessage>();
                    entry.HeaderMessageId = 0;
                }
                else
                {
                    try
                    {
                        await EditBoard(thread, entry, stocks, slot);
                    }
                    catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.UnknownMessage)
                    {
                        repost = true; //someone deleted the board
                    }
                }
                if (repost)
                    await PostBoard(thread, entry, stocks, slot);
            }
            catch (HttpException ex) when (IsGone(ex))
            {
                _state.Threads.Remove(entry);
                return true;
            }

            entry.LastSlot = slot;
            return true;
        }

        private static bool IsGone(HttpException ex) =>
            ex.DiscordCode == DiscordErrorCode.UnknownChannel || ex.DiscordCode == DiscordErrorCode.MissingPermissions
            || ex.DiscordCode == DiscordErrorCode.InsufficientPermissions || ex.HttpCode == System.Net.HttpStatusCode.Forbidden
            || ex.HttpCode == System.Net.HttpStatusCode.NotFound && ex.DiscordCode != DiscordErrorCode.UnknownMessage;

        private static async Task PostBoard(IThreadChannel thread, LiveStockThread entry, List<Stock> stocks, long slot)
        {
            var (embed, file) = Render(stocks, slot);
            IUserMessage message;
            if (file.HasValue)
            {
                using var attachment = file.Value;
                message = await thread.SendFileAsync(attachment, embed: embed);
            }
            else
            {
                message = await thread.SendMessageAsync(embed: embed);
            }
            entry.BoardMessageId = message.Id;
        }

        private static async Task EditBoard(IThreadChannel thread, LiveStockThread entry, List<Stock> stocks, long slot)
        {
            var (embed, file) = Render(stocks, slot);
            var files = file.HasValue ? new List<FileAttachment> { file.Value } : new List<FileAttachment>();
            try
            {
                await thread.ModifyMessageAsync(entry.BoardMessageId, m =>
                {
                    m.Attachments = files;
                    m.Embeds = new[] { embed };
                });
            }
            finally
            {
                foreach (var f in files) f.Dispose();
            }
        }

        /// <summary>
        /// The board's embed and its one chart image. The file name changes with each tick, so Discord clients never show a cached picture.
        /// </summary>
        private static (Embed Embed, FileAttachment? File) Render(IEnumerable<Stock> stocks, long slot)
        {
            var (text, png) = StockTickerCard.BuildBoard(stocks, RecentWindow);
            var builder = DiscordHelper.GetEmbedBuilder("Live stock market",
                text + $"\n\n*Each stock's chart: all time on the left, the last 3 hours on the right, as % change. Updated every 5 minutes, last <t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>.*");
            if (png == null) return (builder.Build(), null);

            string fileName = $"stocks-{slot}.png";
            builder.WithImageUrl($"attachment://{fileName}");
            return (builder.Build(), new FileAttachment(new MemoryStream(png), fileName));
        }

        /// <summary>
        /// Runs the one-off deletion of every old ticker thread if it hasn't run yet (<see cref="StockThreads.CleanupVersion"/>).
        /// Returns false if it is still due because the bot isn't connected to its servers yet. Call with the gate held.
        /// </summary>
        private async Task<bool> CleanUpIfDue(DiscordSocketClient client)
        {
            if (_state.CleanupVersion >= StockThreads.CurrentCleanupVersion) return true;
            if (client.ConnectionState != ConnectionState.Connected || client.Guilds.Count == 0) return false;
            await DeleteAllTickerThreads(client);
            _state.Threads.Clear();
            _state.CleanupVersion = StockThreads.CurrentCleanupVersion;
            Save();
            return true;
        }

        /// <summary>
        /// Deletes every ticker thread the bot made in every server: the ones it tracks, plus any open or archived thread
        /// it owns named <see cref="ThreadName"/>.
        /// </summary>
        private async Task DeleteAllTickerThreads(DiscordSocketClient client)
        {
            ulong botId = client.CurrentUser.Id;
            var ids = new HashSet<ulong>(_state.Threads.Select(t => t.ThreadId));
            foreach (SocketGuild guild in client.Guilds)
            {
                foreach (var thread in guild.ThreadChannels)
                    if (IsOurs(thread, botId)) ids.Add(thread.Id);

                foreach (var channel in guild.TextChannels.Where(c => c is not SocketThreadChannel))
                {
                    try
                    {
                        foreach (var thread in await channel.GetPublicArchivedThreadsAsync(100, null, null))
                            if (IsOurs(thread, botId)) ids.Add(thread.Id);
                    }
                    catch (Exception ex)
                    {
                        //no Read Message History there, so the bot can't list its archived threads in that channel
                        _logger.LogInformation($"Couldn't list archived threads in {channel.Name}: {ex.Message}");
                    }
                }
            }

            foreach (ulong id in ids)
                await DeleteQuietly(client, id);
            _logger.LogInformation($"Deleted {ids.Count} live stock thread(s).");
        }

        private static bool IsOurs(IThreadChannel thread, ulong botId) => thread.OwnerId == botId && thread.Name == ThreadName;

        /// <summary>
        /// Deletes a ticker thread. Without Manage Threads the bot can't delete a thread, so it deletes its own messages in
        /// it and archives it instead. Failures are logged and ignored: it may already be gone.
        /// </summary>
        private async Task DeleteQuietly(DiscordSocketClient client, ulong threadId)
        {
            IThreadChannel thread;
            try
            {
                thread = await client.GetChannelAsync(threadId) as IThreadChannel;
            }
            catch (Exception)
            {
                return; //already gone
            }
            if (thread == null) return;

            try
            {
                await thread.DeleteAsync();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Couldn't delete the live stock thread {threadId} ({ex.Message}), so emptying it instead.");
            }

            try
            {
                if (thread.IsArchived) await thread.ModifyAsync(p => p.Archived = false);
                var messages = await thread.GetMessagesAsync(100).FlattenAsync();
                foreach (var message in messages.Where(m => m.Author.Id == client.CurrentUser.Id))
                {
                    try { await message.DeleteAsync(); }
                    catch (Exception) { /* already gone */ }
                }
                await thread.ModifyAsync(p => p.Archived = true);
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"Couldn't empty the live stock thread {threadId}: {ex.Message}");
            }
        }

        private void Save() => _fileService.UpdateContent(_fileName, Newtonsoft.Json.JsonConvert.SerializeObject(_state));
    }
}
