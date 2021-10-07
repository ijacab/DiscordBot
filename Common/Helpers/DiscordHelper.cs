using Discord.WebSocket;
using System;
using System.Collections.Generic;
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
    }
}
