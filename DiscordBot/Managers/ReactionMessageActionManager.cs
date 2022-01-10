using Discord;
using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot.Managers
{
    public static class ReactionMessageActionManager
    {
        public static List<ReactionMessageAction> ReactionMessageActions;

        public static void Add(IUserMessage message, Guid messageGroupGuid, ReactionMethodMappings reactionMethodMappings)
        {
            var reactionMessageAction = new ReactionMessageAction(message, messageGroupGuid, reactionMethodMappings);
            ReactionMessageActions.Add(reactionMessageAction);

            var emojis = reactionMessageAction.ReactionMethodMappings.GetEmojis();
            foreach (string emoji in emojis)
            {
                message.AddReactionAsync(new Emoji(emoji));
            }
        }

        public static void Remove(IUserMessage message)
        {
            ReactionMessageActions.RemoveAll(rma => rma.Message == message);
            try
            {
                message.RemoveAllReactionsAsync();
            }
            catch (Exception) { }
        }

        public static void RemoveAll(Guid messageGroupGuid)
        {
            var messagesToRemoveReactions = ReactionMessageActions.Where(rma => rma.MessageGroupGuid == messageGroupGuid).Select(rma => rma.Message);

            try
            {
                foreach (var message in messagesToRemoveReactions)
                {
                    message.RemoveAllReactionsAsync();
                }
            }
            catch (Exception) { }

            ReactionMessageActions.RemoveAll(rma => rma.MessageGroupGuid == messageGroupGuid);
        }
    }

}
