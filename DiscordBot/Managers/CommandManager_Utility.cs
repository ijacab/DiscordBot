using Microsoft.Extensions.Logging;
using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Models;
using Common.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;
using System.IO;
using Newtonsoft.Json;
using Common.Services;
using System.Reflection;
using DiscordBot.Models;
using DiscordBot.Services;
using DiscordBot.Text;
using DiscordBot.Games.Models.Cards;

namespace DiscordBot.Managers
{
    /// <summary>Utility commands: custom commands, reminders, `.help`, patch notes and paged embeds.</summary>
    public partial class CommandManager
    {
        private async Task Add(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count() != 2)
                throw new BadSyntaxException();

            if (args[0].StartsWith('.'))
                throw new BadSyntaxException("you can't FUCKING create a custom command that starts with a '.'. STOP TRYING BREAK ME MY BRIAN TOO BIG FOR YOU");

            if (args[0].Length <= 1)
                throw new BadSyntaxException("key must be length of 2 or greater.");

            await _mappingService.Add(args[0], args[1]);
            await message.Channel.SendMessageAsync($"Added mapping {args[0]} : {args[1]}");
            _customMappings = await _mappingService.GetAll();
        }

        private async Task Remove(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count() != 1)
            {
                throw new BadSyntaxException();
            }
            else
            {
                await _mappingService.Remove(args[0]);
                _customMappings = await _mappingService.GetAll(); //or the old one keeps answering until a restart
                await message.Channel.SendMessageAsync($"Binned `{args[0]}`. Nobody liked it anyway.");
            }
        }

        private async Task AddReminder(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 0 && args[0] == "show")
            {
                //only your own: other people's reminders are their business
                var mine = (await _reminderService.GetAll()).Where(r => r.AuthorMention == message.Author.Mention).OrderBy(r => r.TimeToRemind).ToList();
                string msg = mine.Count == 0 ? "You've got no reminders. Go outside." :
                    "⏰ **Your reminders**\n" + string.Join("\n", mine.Select(r => $"{r.TimeToRemind.ToUniversalTime():yyyy-MM-dd HH:mm} UTC: {r.Message}"));
                await message.Channel.SendMessageAsync(msg, allowedMentions: AllowedMentions.None);
                return;
            }

            if (args.Count != 3 || !int.TryParse(args[1], out int numberOfTime))
                throw new BadSyntaxException();
            else
            {
                string reminderMessage = args[0].Trim('"');
                numberOfTime = Math.Clamp(numberOfTime, 0, 100_000);
                string timeType = args[2].ToLower();
                var timeToRemind = timeType.StartsWith("second") ? DateTimeOffset.Now.AddSeconds(numberOfTime)
                    : timeType.StartsWith("minute") ? DateTimeOffset.Now.AddMinutes(numberOfTime)
                    : timeType.StartsWith("hour") ? DateTimeOffset.Now.AddHours(numberOfTime)
                    : timeType.StartsWith("day") ? DateTimeOffset.Now.AddDays(numberOfTime)
                    : DateTimeOffset.Now;

                await _reminderService.Add(message.Author.Mention, reminderMessage, timeToRemind, message.Channel.Id);
                await message.Channel.SendMessageAsync($@"Added reminder for UTC time: {timeToRemind.UtcDateTime}");
            }
        }

        private async Task Help(DiscordSocketClient client, SocketMessage message, List<string> args)
        {

            if (Random.Shared.Next(15) == 0)
            {
                await message.Channel.SendMessageAsync("FUCK you I'm not helping you");
                _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => message.Channel.SendMessageAsync("just kidding")); //not on the gateway
            }
            var helpLines = new List<string>();
            foreach (var command in _commands.Where(c => c.Hidden == false))
            {
                string desc = string.IsNullOrEmpty(command.Description) ? string.Empty : $" {command.Description}";
                string syntax = string.IsNullOrEmpty(command.Syntax) ? string.Empty : $" Syntax: {command.Syntax}";
                helpLines.Add($"**.{command.Name}**{desc}{syntax}");
            }

            await SendPagedEmbeds(message.Channel, "List of commands available", helpLines);

            var customCommands = await _mappingService.GetAll();
            var customCommandNames = customCommands.Keys.ToList();
            if (customCommandNames.Count == 0)
                await message.SendRichEmbedMessage("Custom commands", "None added yet. Add one with `.add \"key\" \"value\"`");
            else
                await SendPagedEmbeds(message.Channel, "Custom commands", new[] { string.Join(", ", customCommandNames) });
        }

        /// <summary>
        /// Sends text to the last few channels money was used in (market news, patch notes). A channel that's gone is skipped, and
        /// so is a thread when <paramref name="skipThreads"/> is set (patch notes never go in threads).
        /// </summary>
        private async Task PostToRecentChannels(DiscordSocketClient client, string text = null, Func<IMessageChannel, Task> send = null, bool skipThreads = false)
        {
            foreach (ulong channelId in _coinService.RecentChannels())
            {
                try
                {
                    if (client.GetChannel(channelId) is not IMessageChannel channel) continue;
                    if (skipThreads && channel is IThreadChannel) continue;
                    if (send != null) await send(channel);
                    else await channel.SendMessageAsync(text);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Couldn't post to channel {Channel}: {Message}", channelId, ex.Message);
                }
            }
        }

        public const int PatchNotesPostedAfterUpdate = 8;

        /// <summary>
        /// After a restart that came with new patch notes, posts them in the last few active channels. Once per run, when the bot is
        /// connected. The first run with nothing recorded only records the newest note. A restart with nothing new posts nothing.
        /// </summary>
        private async Task AnnouncePatchNotesIfNew(DiscordSocketClient client)
        {
            if (_patchNotesChecked || client.ConnectionState != ConnectionState.Connected || client.Guilds.Count == 0) return;
            _patchNotesChecked = true;

            var data = _coinService.GetAll();
            string newest = PatchNotes.KeyOf(PatchNotes.All[0]);
            if (data.LastAnnouncedPatch == newest) return;

            var fresh = PatchNotes.NewSince(data.LastAnnouncedPatch, PatchNotesPostedAfterUpdate);
            int more = PatchNotes.CountSince(data.LastAnnouncedPatch) - fresh.Count;
            data.LastAnnouncedPatch = newest;
            await _coinService.Update(); //saved first, so a failed post can't repeat on every restart

            if (fresh.Count == 0) return;
            var lines = fresh.Select(n => $"**{n.Date}** · {n.Text}").ToList();
            if (more > 0) lines.Add($"…and {more} more. See `.patchnotes`.");
            await PostToRecentChannels(client, send: channel => SendPagedEmbeds(channel, PatchNotesTitle, lines), skipThreads: true);
        }

        /// <summary>The heading of the patch notes post (the first embed field's name), which is how old posts are found again.</summary>
        private const string PatchNotesTitle = "🛠️ Updated: new patch notes";
        private bool _threadCleanupStarted;

        /// <summary>
        /// Once, after the bot connects: deletes the bot's earlier patch note posts from threads (active threads, and public
        /// archived ones where Discord lets it; the last 100 messages of each). Threads it can't reach are skipped. The flag is saved
        /// at the end so it never scans again.
        /// </summary>
        private async Task CleanPatchNotesFromThreadsOnce(DiscordSocketClient client)
        {
            var data = _coinService.GetAll();
            if (data.PatchNotesThreadCleanupDone || _threadCleanupStarted || client.ConnectionState != ConnectionState.Connected || client.Guilds.Count == 0) return;
            _threadCleanupStarted = true;

            int deleted = 0, scanned = 0;
            foreach (var guild in client.Guilds)
            {
                var threads = new Dictionary<ulong, IMessageChannel>();
                foreach (var thread in guild.ThreadChannels) threads[thread.Id] = thread;
                foreach (ulong id in _coinService.RecentChannels())
                    if (client.GetChannel(id) is SocketThreadChannel recent && recent.Guild.Id == guild.Id) threads[recent.Id] = recent;
                foreach (var parent in guild.TextChannels.Where(c => c is not SocketThreadChannel && c is not SocketVoiceChannel))
                {
                    try
                    {
                        DateTimeOffset? before = null;
                        for (int page = 0; page < 5; page++)
                        {
                            var archived = await parent.GetPublicArchivedThreadsAsync(100, before, null);
                            foreach (var thread in archived) threads.TryAdd(thread.Id, thread);
                            if (archived.Count < 100) break;
                            before = archived.Min(t => t.ArchiveTimestamp);
                            await Task.Delay(500);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation("Patch note cleanup: no archived threads for #{Channel}: {Message}", parent.Name, ex.Message);
                    }
                }

                foreach (var thread in threads.Values)
                {
                    try
                    {
                        scanned++;
                        var messages = await thread.GetMessagesAsync(100).FlattenAsync();
                        foreach (var message in messages.Where(m => m.Author.Id == client.CurrentUser.Id
                            && m.Embeds.Any(e => e.Fields.Any(f => f.Name == PatchNotesTitle))))
                        {
                            await message.DeleteAsync();
                            deleted++;
                            await Task.Delay(300);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInformation("Patch note cleanup: couldn't clean thread {Thread}: {Message}", thread.Id, ex.Message);
                    }
                }
            }

            data.PatchNotesThreadCleanupDone = true;
            await _coinService.Update();
            _logger.LogInformation("Patch note cleanup: deleted {Deleted} old patch note posts from {Scanned} threads", deleted, scanned);
        }

        private async Task ShowPatchNotes(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            bool showAll = args.Count == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase);
            if (args.Count > 1 || (args.Count == 1 && !showAll)) throw new BadSyntaxException();

            var notes = showAll ? PatchNotes.All : PatchNotes.All.Take(10).ToList();
            await SendPagedEmbeds(message.Channel, showAll ? "Patch notes" : "Recent patch notes", notes.Select(n => $"**{n.Date}** · {n.Text}"));
        }

        // Discord caps an embed at 6000 characters, so long lists go out as several embeds (one message each)
        private static async Task SendPagedEmbeds(IMessageChannel channel, string title, IEnumerable<string> lines, MessageComponent components = null)
        {
            var pages = PagedEmbeds(title, lines);
            for (int i = 0; i < pages.Count; i++)
                await channel.SendMessageAsync(embed: pages[i], components: i == pages.Count - 1 ? components : null);
        }

        /// <summary>The embeds for a long list, one per message (see <see cref="TextPaginator.Paginate"/>).</summary>
        private static List<Embed> PagedEmbeds(string title, IEnumerable<string> lines)
        {
            var embeds = new List<Embed>();
            var pages = TextPaginator.Paginate(lines);
            for (int i = 0; i < pages.Count; i++)
            {
                var embed = new EmbedBuilder().WithColor(Color.DarkPurple);
                for (int f = 0; f < pages[i].Count; f++)
                {
                    string fieldName = (i == 0 && f == 0) ? title : "⠀";
                    embed.AddField(fieldName, pages[i][f]);
                }
                embeds.Add(embed.Build());
            }
            return embeds;
        }

        /// <summary>Buys for a player and returns the public line, for `.buy` and the Buy buttons.</summary>
        private async Task<(string Text, string Symbol)> BuyStockText(ulong userId, string username, string mention, string symbol, string amount)
        {
            EnsureNotBetting(userId, "trading stocks");
            CoinAccount account = await _coinService.Get(userId, username);
            var (stock, shares, cost) = _stockService.Buy(account, symbol, amount);
            return ($"{mention} bought {shares} share(s) of {stock.Symbol} at an average ${Stock.FormatPrice(cost / shares)} for ${FormatHelper.GetCommaNumber(cost)}. {stock.Symbol} is now ${Stock.FormatPrice(stock.Price)}. Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.", stock.Symbol);
        }

        /// <summary>Sells for a player and returns the public line, for `.sell` and the Sell buttons.</summary>
        private async Task<(string Text, string Symbol)> SellStockText(ulong userId, string username, string mention, string symbol, string amount)
        {
            EnsureNotBetting(userId, "trading stocks");
            CoinAccount account = await _coinService.Get(userId, username);
            var (stock, shares, gross, tax) = _stockService.Sell(account, symbol, amount);
            return ($"{mention} sold {shares} share(s) of {stock.Symbol} at an average ${Stock.FormatPrice(gross / shares)} for ${FormatHelper.GetCommaNumber(gross)}, less ${FormatHelper.GetCommaNumber(tax)} sell tax (goes to the jackpot). {stock.Symbol} is now ${Stock.FormatPrice(stock.Price)}. Cash is now ${FormatHelper.GetCommaNumber(account.NetWorth)}.", stock.Symbol);
        }

        /// <summary>Refuses while the player has a bet open, so a bet's stake can't be spent twice.</summary>
        private void EnsureNotBetting(ulong userId, string doing)
        {
            if (_betManager.InitiatedBetUserIds.Contains(userId))
                throw new BadInputException($"Finish your bet before {doing}.");
        }

        private void EnsureNotBetting(SocketMessage message) => EnsureNotBetting(message.Author.Id, "trading stocks");

        //private async Task Convo(DiscordSocketClient client, SocketMessage message, List<string> args)
        //{
        //    string? context = null;

        //    if (args.Count > 0)
        //        context = $"{message.Author.Username}:{args[0]}";

        //    string convo = await _gptService.GetConvo(context);
        //    await message.Channel.SendMessageAsync(convo);
        //}
    }
}
