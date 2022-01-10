using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Models
{
    public class ReactionCommand : Command
    {
        private Dictionary<string, string> _emoteCommandNameMappings;
        public ReactionCommand(string name, 
            Func<DiscordSocketClient, SocketMessage, List<string>, Task> commandFunction,
            Dictionary<string,string> emoteCommandNameMappings
            )
            : base(name, commandFunction, false, false)
        {
            _emoteCommandNameMappings = emoteCommandNameMappings;
        }

        public async Task ExecuteAsync(DiscordSocketClient client, IUserMessage message, SocketReaction reaction)
        {
            var args = GetArgsFromMappings(reaction);
            await ExecuteAsync(client, (SocketMessage)message, args);
        }

        private List<string> GetArgsFromMappings(SocketReaction reaction)
        {
            string emote = reaction.Emote.Name;
            if (_emoteCommandNameMappings.ContainsKey(emote))
            {
                return _emoteCommandNameMappings[emote].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            }

            return new List<string>();
        }
    }
}
