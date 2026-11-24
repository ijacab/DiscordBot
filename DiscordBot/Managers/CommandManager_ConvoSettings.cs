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
    /// <summary>`.convoset` (bot admins): change how wild `.convo` is without a deploy.</summary>
    public partial class CommandManager
    {
        /// <summary>`.convoset` lists the settings, `.convoset chaos 0.9` changes one, `.convoset reset [name]` puts them back.</summary>
        private async Task ConvoSet(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var settings = _convoCorpus.Settings;

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
    }
}
