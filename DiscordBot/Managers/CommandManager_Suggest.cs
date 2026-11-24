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

        /// <summary>`.sug text` adds a point (anyone), `.sug` shows the whole list (anyone), `.sug clear` empties it (bot admins).</summary>
        private async Task Suggest(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count == 1 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                if (!_adminIds.Contains(message.Author.Id))
                    throw new BadInputException("Only a bot admin can clear the suggestions.");
                int removed = _suggestions.Clear();
                await message.Channel.SendMessageAsync(removed == 0 ? "The suggestions list was already empty." : $"🧹 Cleared {removed} suggestion{(removed == 1 ? "" : "s")}.");
                return;
            }

            if (args.Count == 0)
            {
                var items = _suggestions.All();
                if (items.Count == 0)
                {
                    await message.Channel.SendMessageAsync("No suggestions yet. Add one with `.sug your idea`.");
                    return;
                }
                foreach (var chunk in SuggestionList.Chunks(items, _messageCharLimit - 100))
                    await message.Channel.SendMessageAsync(chunk, allowedMentions: AllowedMentions.None);
                await message.Channel.SendMessageAsync($"{items.Count} suggestion{(items.Count == 1 ? "" : "s")}. `.sug clear` empties the list.", allowedMentions: AllowedMentions.None);
                return;
            }

            int count = _suggestions.Add(PlainMentions(string.Join(" ", args), message));
            await message.Channel.SendMessageAsync($"📝 Added. {count} on the list.");
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
