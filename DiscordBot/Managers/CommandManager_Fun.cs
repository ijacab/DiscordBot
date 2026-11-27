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
    /// <summary>The non-money commands: `.roll`, `.time`, images, `.face`, `.lang`, `.convo`.</summary>
    public partial class CommandManager
    {

        private async Task Age(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            string info = $@"Your account was created at {message.Author.CreatedAt.DateTime.ToString("yyyy-MM-dd hh:mm:ss")}";
            var age = DateTimeOffset.Now - message.Author.CreatedAt.DateTime;
            info += $"\n\nYou are {Convert.ToInt32(age.TotalDays)} days old.";
            await message.Channel.SendMessageAsync(info);
        }

        private async Task Roll(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            int rand;
            if (args.Count == 0)
            {
                rand = new Random().Next(0, 101);
                await message.Channel.SendMessageAsync($"You rolled {rand}");
                return;
            }

            if (args.Count != 2)
                throw new BadSyntaxException();

            if (int.TryParse(args[0], out int num1) == false)
                throw new BadSyntaxException();
            if (int.TryParse(args[1], out int num2) == false)
                throw new BadSyntaxException();
            if (num1 > num2)
                throw new BadSyntaxException();

            rand = (int)Random.Shared.NextInt64(num1, (long)num2 + 1); //long, so 2147483647 doesn't overflow

            await message.SendRichEmbedMessage($"You rolled {rand}");
        }

        private async Task Time(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            //real time zones, so daylight saving is right
            string zone = args.Count > 0 ? args[0].ToLowerInvariant() : "utc";
            (string Label, string Id) pick = zone switch
            {
                "pst" or "pdt" or "pt" or "la" => ("LA", "America/Los_Angeles"),
                "est" or "edt" or "et" or "ny" => ("New York", "America/New_York"),
                "aest" or "aedt" or "syd" or "sydney" => ("Sydney", "Australia/Sydney"),
                "uk" or "gmt" or "bst" or "london" => ("London", "Europe/London"),
                _ => ("UTC", "UTC"),
            };
            DateTime now = DateTime.UtcNow;
            try
            {
                now = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, pick.Id);
            }
            catch (TimeZoneNotFoundException)
            {
                pick.Label = "UTC";
            }
            await message.Channel.SendMessageAsync($"{pick.Label}: {now:yyyy-MM-dd HH:mm:ss}");
        }

        public async Task ImageSearch(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            await ImageSearch(message, args);
        }

        /// <summary>Things to add to a `.dbz` search so it doesn't keep landing on the same first page of results.</summary>
        private static readonly string[] _dbzSubjects =
        {
            "goku", "vegeta", "gohan", "piccolo", "frieza", "cell", "majin buu", "trunks", "krillin", "bulma", "broly", "gotenks",
            "super saiyan", "super saiyan blue", "ultra instinct", "kamehameha", "shenron", "dragon balls", "namek", "saiyan saga",
        };

        public async Task ImageSearchDbz(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            string subject = _dbzSubjects[Random.Shared.Next(_dbzSubjects.Length)];
            await ImageSearch(message, new List<string> { "dragon", "ball", "z", subject }, isAlwaysOn: true);
        }

        /// <summary>
        /// Searches, downloads a picture that really works and posts it as an attachment. The search and the downloads are slow, so
        /// they run off the gateway task and report their own errors.
        /// </summary>
        private async Task ImageSearch(SocketMessage message, List<string> args, bool isAlwaysOn = false)
        {
            if (_imageSearchStopped && !isAlwaysOn)
                return;

            ThrowIfBlackListed(message);

            var searchQuery = string.Join(' ', args);
            if (string.IsNullOrWhiteSpace(searchQuery))
                throw new BadInputException("You didn't provide an argument");

            _ = Task.Run(async () =>
            {
                try
                {
                    using var typing = message.Channel.EnterTypingState();
                    var results = await _duckDuckGoService.GetImages(searchQuery);
                    var image = await _duckDuckGoService.FetchFirstWorkingAsync(DuckDuckGoService.Candidates(results, Random.Shared));
                    if (image == null)
                    {
                        await message.Channel.SendMessageAsync(results.Count == 0
                            ? "Searched the whole internet and found fuck all. Try again."
                            : "Found some pictures but every link was dead or blocked. Try again, it picks different ones each time.");
                        return;
                    }

                    using var stream = new MemoryStream(image.Bytes);
                    var msg = await message.Channel.SendFileAsync(stream: stream, filename: image.FileName);
                    if (searchQuery.Contains("safeoff", StringComparison.OrdinalIgnoreCase))
                        _ = msg.DeleteAfterDelay(); //delete nsfw messages after delay
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Image search for {Query} failed", searchQuery);
                    await message.Channel.SendMessageAsync("The picture machine broke: " + ex.Message);
                }
            });
        }

        public void ThrowIfBlackListed(SocketMessage message)
        {
            if (_appSettings.BlackListedIds.Contains(message.Author.Id))
                throw new BadInputException("No");

            _appSettings.BlackListedWords.ForEach(bw =>
            {
                if (message.Content.Contains(bw))
                {
                    _appSettings.BlackListedIds.Add(message.Author.Id);
                    string json = File.ReadAllText("appsettings.json");
                    dynamic jsonObj = JsonConvert.DeserializeObject(json);
                    jsonObj["AppSettings"]["BlackListedIds"] = JsonConvert.SerializeObject(_appSettings.BlackListedIds);
                    string output = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
                    File.WriteAllText("appsettings.json", output);
                    throw new BadInputException("No");
                }
            });
        }

        public async Task FaceGenerate(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            using var stream = await _faceService.Run();
            await message.Channel.SendFileAsync(stream: stream, "image.jpg");
        }

        /// <summary>
        /// `.convo`: in the scraped server, a conversation from <see cref="ConvoModel"/> (instant, no LLM, always "user: text"
        /// lines). Before the history is in, it says so (and starts the scrape if nothing is running); it never falls back to the
        /// LLM there. In other servers the local LLM continues the last few messages, in the background, keeping only lines in the
        /// right format.
        /// </summary>
        private async Task Convo(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            bool inServer = (message.Channel as SocketGuildChannel)?.Guild.Id == ConvoCorpusService.GuildId;
            if (!inServer)
            {
                _ = Task.Run(() => ConvoFromLlm(message));
                return;
            }

            var lines = MakeConvo(client, message, args, out var nameOf);
            await message.Channel.SendMessageAsync(ConvoModel.Format(lines, boldSpeakers: true, displayName: nameOf), allowedMentions: AllowedMentions.None);
        }

        /// <summary>
        /// The lines for `.convo` and `.convolang` in the scraped server, and how to show each speaker (display name, else username).
        /// Throws <see cref="BadInputException"/> with the reason when there is nothing to show yet (loading, scraping, or no history,
        /// which starts a scrape).
        /// </summary>
        private List<ConvoLine> MakeConvo(DiscordSocketClient client, SocketMessage message, List<string> args, out Func<string, string> nameOf)
        {
            var model = _convoCorpus.Model;

            //`.convo @someone [words]`: a short convo that is only that person talking. The tag isn't part of the topic words.
            var tagged = message.MentionedUsers.Where(u => !u.IsBot).ToList();
            string only = null;
            if (tagged.Count > 1)
                throw new BadInputException("Tag one person at a time: `.convo @someone`.");
            if (tagged.Count == 1)
            {
                only = tagged[0].Username; //the model keys people by username
                if (model != null && !model.HasSpeaker(only))
                    throw new BadInputException($"I haven't read anything from {ConvoCorpusService.DisplayNameOf(tagged[0])} yet, so I can't do their voice.");
                args = args.Where(a => !Regex.IsMatch(a, @"^<@!?\d+>$")).ToList();
            }

            var lines = model?.Generate(RandomHelper.ThisThreadsRandom, string.Join(" ", args), only);
            if (lines != null && lines.Count > 0)
            {
                //shown by display name: look each username up among the server's members, and fall back to the username
                var members = (message.Channel as SocketGuildChannel)?.Guild.Users
                    .GroupBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => ConvoCorpusService.DisplayNameOf(g.First()), StringComparer.OrdinalIgnoreCase);
                nameOf = username => members != null && members.TryGetValue(username, out string shown) ? shown : username;
                return lines;
            }

            if (_convoCorpus.IsLoading)
                throw new BadInputException("The convo brain is loading the server's history. Try again in a minute.");
            if (_convoCorpus.IsScraping)
                throw new BadInputException($"Still reading the server's history ({_convoCorpus.SavedThisRun:N0} messages so far). Try again in a few minutes.");
            _convoCorpus.StartScrape(client, message.Channel);
            throw new BadInputException("I haven't read the server's history yet, so I've started now. Try `.convo` again in a few minutes.");
        }

        /// <summary>
        /// `.convolang`: a `.convo` whose lines are each bounced through `.lang`'s chain of languages. The texts go through in one
        /// request, joined by new lines (speaker names are never translated); if the number of lines changes on the way, each line
        /// is translated on its own, a few at a time. Runs off the gateway task, since the chain takes seconds.
        /// </summary>
        private Task ConvoLang(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if ((message.Channel as SocketGuildChannel)?.Guild.Id != ConvoCorpusService.GuildId)
                throw new BadInputException("`.convolang` only works in the server the convo brain learned from.");
            var lines = MakeConvo(client, message, args, out var nameOf);
            _ = Task.Run(async () =>
            {
                try
                {
                    using (message.Channel.EnterTypingState())
                    {
                        var texts = await TranslateLines(lines.Select(l => l.Text).ToList());
                        var mangled = lines.Select((l, i) => new ConvoLine { Speaker = l.Speaker, Text = texts[i] }).ToList();
                        string text = ConvoModel.Format(mangled, boldSpeakers: true, displayName: nameOf);
                        if (text.Length > _messageCharLimit) text = text.Substring(0, _messageCharLimit);
                        await message.Channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None);
                    }
                }
                catch (TranslationUnavailableException)
                {
                    try { await message.Channel.SendMessageAsync("Google Translate isn't answering right now. Try again in a minute."); }
                    catch (Exception) { }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "convolang failed");
                    try { await message.Channel.SendMessageAsync("That convo got lost in translation. Try again."); }
                    catch (Exception) { }
                }
            });
            return Task.CompletedTask;
        }

        /// <summary>How many lines are translated at once when they have to go one by one.</summary>
        private const int ConvoLangParallel = 3;

        private async Task<List<string>> TranslateLines(List<string> texts)
        {
            var together = await _translationService.TranslateThroughLanguages(string.Join("\n", texts));
            var split = SplitTranslatedLines(together.Text, texts.Count);
            if (split != null) return split;

            //the lines merged or split on the way: do them one at a time, a few in parallel
            var results = new string[texts.Count];
            using var gate = new System.Threading.SemaphoreSlim(ConvoLangParallel);
            await Task.WhenAll(texts.Select(async (t, i) =>
            {
                await gate.WaitAsync();
                try { results[i] = (await _translationService.TranslateThroughLanguages(t)).Text.Replace("\n", " ").Trim(); }
                catch (TranslationUnavailableException) { results[i] = t; }
                finally { gate.Release(); }
            }));
            if (Enumerable.Range(0, texts.Count).All(i => results[i] == texts[i])) throw new TranslationUnavailableException();
            return results.ToList();
        }

        /// <summary>The translated block cut back into <paramref name="count"/> lines (blank lines dropped), or null when the count doesn't match.</summary>
        public static List<string> SplitTranslatedLines(string translated, int count)
        {
            var parts = (translated ?? "").Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            return parts.Count == count ? parts : null;
        }

        private async Task ScrapeConvo(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            string action = args.FirstOrDefault()?.ToLowerInvariant();
            if (action == "status")
            {
                await message.Channel.SendMessageAsync(_convoCorpus.Status());
                return;
            }
            if (action == "stop")
            {
                await message.Channel.SendMessageAsync(_convoCorpus.StopScrape() ? "Stopping the scrape after this page." : "No scrape is running.");
                return;
            }
            if (!_convoCorpus.StartScrape(client, message.Channel))
            {
                await message.Channel.SendMessageAsync($"A scrape is already running.\n{_convoCorpus.Status()}");
                return;
            }
            await message.Channel.SendMessageAsync("Scraping the server's history in the background (it can take a few hours the first time). `.scrapeconvo status` shows how far it's got, and I'll post here when it's done.");
        }

        public const int LlmConvoTries = 3;

        /// <summary>The old `.convo` for other servers: the local LLM continues the last 10 messages. Run off the gateway task.</summary>
        private async Task ConvoFromLlm(SocketMessage message)
        {
            try
            {
                var messages = await message.Channel.GetMessagesAsync(limit: 30).FlattenAsync();
                List<string> formattedMessages = messages
                    .Where(m => m.Id != message.Id && !string.IsNullOrWhiteSpace(m.Content) && !m.Author.IsBot)
                    .OrderBy(m => m.Timestamp)
                    .TakeLast(10)
                    .Select(m => $"{m.Author.Username}: {m.Content}")
                    .ToList();

                for (int attempt = 0; attempt < LlmConvoTries; attempt++)
                {
                    var lines = ConvoService.KeepChatLines(await _convoService.ProduceConvo(formattedMessages));
                    if (lines.Count >= ConvoModel.MinLines)
                    {
                        await message.Channel.SendMessageAsync(string.Join("\n", lines.Select(ConvoService.BoldSpeaker)), allowedMentions: AllowedMentions.None);
                        return;
                    }
                }
                await message.Channel.SendMessageAsync("Couldn't come up with a convo, try again.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM convo failed");
                try { await message.Channel.SendMessageAsync("The convo brain isn't answering right now, try again later."); }
                catch (Exception) { }
            }
        }

        private async Task Lang(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count == 0)
                throw new BadSyntaxException();

            TranslationResult result;
            try
            {
                result = await _translationService.TranslateThroughLanguages(string.Join(" ", args));
            }
            catch (TranslationUnavailableException)
            {
                await message.Channel.SendMessageAsync("Google Translate isn't answering right now. Try again in a minute.");
                return;
            }

            string translated = result.Text;
            if (translated.Length > _messageCharLimit)
                translated = translated.Substring(0, _messageCharLimit);

            await message.Channel.SendMessageAsync(translated, allowedMentions: AllowedMentions.None);
        }
    }
}
