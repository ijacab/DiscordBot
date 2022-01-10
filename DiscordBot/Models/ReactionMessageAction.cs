using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot.Models
{
    public class ReactionMessageAction
    {
        public readonly IUserMessage Message;
        public readonly Guid MessageGroupGuid;
        public readonly ReactionMethodMappings ReactionMethodMappings;

        public ReactionMessageAction(IUserMessage message, Guid messageGroupGuid, ReactionMethodMappings reactionMethodMappings)
        {
            Message = message;
            MessageGroupGuid = messageGroupGuid;
            ReactionMethodMappings = reactionMethodMappings;
        }
    }
}
