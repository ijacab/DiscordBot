using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Managers
{
    /// <summary>`.suggest` / `.sug`: a master list of suggestions to copy out and hand over for implementing.</summary>
    public partial class CommandManager
    {
        private readonly SuggestionList _suggestions = new SuggestionList(SuggestionList.DefaultPath);

        /// <summary>
        /// `.sug text` adds a point (anyone), `.sug` shows the list to copy and `.sug list` the numbered list with names (anyone),
        /// and for bot admins `.sug clear` empties it, `.sug clear 3` / `2-4` removes those numbers and `.sug clear @person` removes everything one person wrote.
        /// </summary>
        private async Task Suggest(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            bool admin = _adminIds.Contains(message.Author.Id);
            bool clearWord = args.Count > 0 && (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase) || args[0].Equals("remove", StringComparison.OrdinalIgnoreCase));
            //a non-admin's "clear the leaderboard bug" is still a suggestion; only a bare "clear" is refused
            if (clearWord && (admin || args.Count == 1))
            {
                if (!admin)
                    throw new BadInputException("Only a bot admin can clear suggestions.");
                await ClearSuggestions(message, args.Skip(1).ToList(), args[0].Equals("remove", StringComparison.OrdinalIgnoreCase));
                return;
            }

            if (args.Count == 1 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var all = _suggestions.All();
                if (all.Count == 0)
                {
                    await message.Channel.SendMessageAsync("No suggestions yet. Add one with `.sug your idea`.");
                    return;
                }
                var numbered = all.Select((s, i) => $"{i + 1}. {s.Text} — *{s.Author}*");
                foreach (var chunk in SuggestionList.Chunks(numbered, _messageCharLimit - 100))
                    await message.Channel.SendMessageAsync(chunk, allowedMentions: AllowedMentions.None);
                await message.Channel.SendMessageAsync("`.sug clear 3`, `.sug clear 2-4` or `.sug clear @person` removes some. `.sug clear` removes all.", allowedMentions: AllowedMentions.None);
                return;
            }

            if (args.Count == 0)
            {
                var items = _suggestions.Texts();
                if (items.Count == 0)
                {
                    await message.Channel.SendMessageAsync("No suggestions yet. Add one with `.sug your idea`.");
                    return;
                }
                foreach (var chunk in SuggestionList.Chunks(items.Select(i => "- " + i), _messageCharLimit - 100))
                    await message.Channel.SendMessageAsync(chunk, allowedMentions: AllowedMentions.None);
                await message.Channel.SendMessageAsync($"{items.Count} suggestion{(items.Count == 1 ? "" : "s")}. `.sug list` numbers them with names, `.sug clear` empties the list.", allowedMentions: AllowedMentions.None);
                return;
            }

            int count = _suggestions.Add(PlainMentions(string.Join(" ", args), message), message.Author.Id, message.Author.Username);
            await message.Channel.SendMessageAsync($"📝 Added. {count} on the list.");
        }

        private async Task ClearSuggestions(SocketMessage message, List<string> what, bool needsTarget)
        {
            if (what.Count == 0)
            {
                if (needsTarget)
                    throw new BadInputException("Remove which? `.sug remove 3`, `.sug remove 2-4` or `.sug remove @person`.");
                int all = _suggestions.Clear();
                await message.Channel.SendMessageAsync(all == 0 ? "The suggestions list was already empty." : $"🧹 Cleared {all} suggestion{(all == 1 ? "" : "s")}.");
                return;
            }

            List<Suggestion> removed;
            string whose;
            if (message.MentionedUsers.Count > 0)
            {
                removed = new List<Suggestion>();
                foreach (var user in message.MentionedUsers)
                    removed.AddRange(_suggestions.RemoveBy(user.Id, user.Username));
                whose = string.Join(", ", message.MentionedUsers.Select(u => u.Username));
            }
            else if (SuggestionList.ParseNumbers(what) is List<int> numbers)
            {
                removed = _suggestions.RemoveNumbers(numbers);
                string shown = string.Join("\n", removed.Take(5).Select(s => $"- {(s.Text.Length > 100 ? s.Text.Substring(0, 100) + "…" : s.Text)}"));
                await message.Channel.SendMessageAsync($"🧹 Removed {removed.Count} suggestion{(removed.Count == 1 ? "" : "s")}:\n{shown}{(removed.Count > 5 ? $"\n…and {removed.Count - 5} more" : "")}", allowedMentions: AllowedMentions.None);
                return;
            }
            else
            {
                whose = string.Join(" ", what).TrimStart('@');
                removed = _suggestions.RemoveBy(0, whose);
            }

            if (removed.Count == 0)
                throw new BadInputException($"There are no suggestions from {whose}. `.sug list` shows who wrote what.");
            await message.Channel.SendMessageAsync($"🧹 Removed {removed.Count} suggestion{(removed.Count == 1 ? "" : "s")} from {whose}.", allowedMentions: AllowedMentions.None);
        }

        /// <summary>Turns "<@123>" into "@name" so a saved suggestion reads as plain text.</summary>
        private static string PlainMentions(string text, SocketMessage message)
        {
            foreach (var user in message.MentionedUsers)
                text = text.Replace($"<@{user.Id}>", "@" + user.Username).Replace($"<@!{user.Id}>", "@" + user.Username);
            return text;
        }
    }
}
