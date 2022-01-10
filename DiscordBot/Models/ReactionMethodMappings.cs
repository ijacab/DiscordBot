using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot.Models
{
    public class ReactionMethodMappings
    {
        private readonly Dictionary<string, Func<IMessage, Task>> _reactionMethodMappings;

        public void Add(string emoji, Func<IMessage, Task> methodToExecute)
        {
            _reactionMethodMappings.Add(emoji, methodToExecute);
        }

        public List<string> GetEmojis()
        {
            return new List<string>(_reactionMethodMappings.Keys);
        }

        public bool TryGetMethodFromEmoji(string emoji, out Func<IMessage, Task> methodToExecute)
        {
            if (_reactionMethodMappings.TryGetValue(emoji, out methodToExecute))
                return true;
            else
                return false;

        }
    }
}
