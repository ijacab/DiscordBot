using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Managers
{
    /// <summary>`.convoset` (bot admins, and anyone they allow): change how wild `.convo` is without a deploy.</summary>
    public partial class CommandManager
    {
        /// <summary>
        /// `.convoset` lists the settings, `.convoset chaos 0.9` changes one, `.convoset reset [name]` puts them back. Bot admins can
        /// also `.convoset allow @user` / `deny @user` to let someone else use it, and `.convoset users` lists who can.
        /// </summary>
        private async Task ConvoSet(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var settings = _convoCorpus.Settings;
            bool admin = _adminIds.Contains(message.Author.Id);
            if (!admin && !settings.IsAllowed(message.Author.Id))
            {
                await message.Channel.SendMessageAsync("You not admin. FUCK OFF");
                return;
            }

            if (args.Count > 0 && (args[0].Equals("allow", StringComparison.OrdinalIgnoreCase) || args[0].Equals("deny", StringComparison.OrdinalIgnoreCase)
                || args[0].Equals("users", StringComparison.OrdinalIgnoreCase)))
            {
                if (!admin)
                    throw new BadInputException("Only a bot admin can choose who uses `.convoset`.");
                await ConvoSetUsers(client, message, args);
                return;
            }

            if (args.Count == 0)
            {
                var lines = new List<string> { "🎛️ **`.convo` settings**" };
                lines.AddRange(settings.Describe());
                lines.Add("Change one with `.convoset chaos 0.9` (or `90%`). `.convoset reset` or `.convoset reset chaos` puts them back.");
                foreach (var chunk in SuggestionList.Chunks(lines, _messageCharLimit - 100))
                    await message.Channel.SendMessageAsync(chunk, allowedMentions: AllowedMentions.None);
                return;
            }

            if (args[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Count > 2) throw new BadSyntaxException();
                string done = settings.Reset(args.Count == 2 ? args[1] : null)
                    ?? throw new BadInputException($"I don't know a setting called \"{args[1]}\". `.convoset` lists them.");
                _convoCorpus.SaveSettings();
                await message.Channel.SendMessageAsync($"🎛️ {done}");
                return;
            }

            if (args.Count != 2) throw new BadSyntaxException();
            if (!settings.TrySet(args[0], args[1], out string result))
                throw new BadInputException(result);
            _convoCorpus.SaveSettings();
            await message.Channel.SendMessageAsync($"🎛️ {result}. The next `.convo` uses it.");
        }

        /// <summary>`.convoset allow @user`, `.convoset deny @user` and `.convoset users`.</summary>
        private async Task ConvoSetUsers(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var settings = _convoCorpus.Settings;
            string NameOf(ulong id) => client.GetUser(id)?.Username ?? id.ToString();

            if (args[0].Equals("users", StringComparison.OrdinalIgnoreCase))
            {
                var allowed = settings.AllowedList();
                await message.Channel.SendMessageAsync(allowed.Count == 0
                    ? "Only bot admins can use `.convoset`. `.convoset allow @someone` lets someone else."
                    : $"🎛️ Besides bot admins, these can use `.convoset`: {string.Join(", ", allowed.Select(NameOf))}.", allowedMentions: AllowedMentions.None);
                return;
            }

            //people tagged, or typed as a user id
            var targets = message.MentionedUsers.Where(u => !u.IsBot).Select(u => u.Id).ToList();
            foreach (string word in args.Skip(1))
                if (ulong.TryParse(word, out ulong id) && id > 1_000_000 && !targets.Contains(id))
                    targets.Add(id);
            if (targets.Count == 0)
                throw new BadInputException($"Who? `.convoset {args[0].ToLowerInvariant()} @someone`.");

            bool allow = args[0].Equals("allow", StringComparison.OrdinalIgnoreCase);
            var changed = new List<string>();
            var unchanged = new List<string>();
            foreach (ulong id in targets)
            {
                bool did = allow ? settings.Allow(id) : settings.Deny(id);
                (did ? changed : unchanged).Add(NameOf(id));
            }
            _convoCorpus.SaveSettings();

            var lines = new List<string>();
            if (changed.Count > 0)
                lines.Add(allow ? $"🎛️ {string.Join(", ", changed)} can now use `.convoset`." : $"🎛️ {string.Join(", ", changed)} can no longer use `.convoset`.");
            if (unchanged.Count > 0)
                lines.Add(allow ? $"{string.Join(", ", unchanged)} could already." : $"{string.Join(", ", unchanged)} wasn't on the list.");
            await message.Channel.SendMessageAsync(string.Join("\n", lines), allowedMentions: AllowedMentions.None);
        }
    }
}
