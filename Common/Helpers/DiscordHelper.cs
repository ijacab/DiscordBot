using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Common.Helpers
{
    public static class DiscordHelper
    {
        public static async Task SendMessageToEachChannel(this IEnumerable<Tuple<ulong, ulong>> distinctServerChannelMappings, string messageText, DiscordSocketClient client)
        {
            foreach (var serverChannelMapping in distinctServerChannelMappings)
            {
                ulong serverId = serverChannelMapping.Item1;
                ulong channelId = serverChannelMapping.Item2;
                var messageChannel = client.GetGuild(serverId).GetTextChannel(channelId);
                await messageChannel.SendMessageAsync(messageText);
            }
        }

        public static async Task SendRichEmbedMessage(this IMessage message, string title, string messageContent)
        {
            var embed = new EmbedBuilder();
            // Or with methods
            embed.AddField(title, messageContent)
                .WithColor(Color.DarkPurple);
            await message.Channel.SendMessageAsync(embed: embed.Build());
        }
    }
}
