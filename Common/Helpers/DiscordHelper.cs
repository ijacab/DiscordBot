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
        private const int _fieldCharLimit = 1023;
        public static async Task SendMessageToEachChannel(this IEnumerable<Tuple<ulong, ulong>> distinctServerChannelMappings, string title, string messageText, DiscordSocketClient client)
        {
            foreach (var serverChannelMapping in distinctServerChannelMappings)
            {
                ulong serverId = serverChannelMapping.Item1;
                ulong channelId = serverChannelMapping.Item2;
                var messageChannel = client.GetGuild(serverId).GetTextChannel(channelId);
                await messageChannel.SendRichEmbedMessage(title, messageText);
            }
        }

        public static async Task SendRichEmbedMessage(this IMessage message, string title, string messageContent)
        {
            var embed = GetEmbedBuilder(title, messageContent);
            await message.Channel.SendMessageAsync(embed: embed.Build());
        }

        public static async Task SendRichEmbedMessage(this IMessage message, string messageContent)
        {
            await SendRichEmbedMessage(message, "~", messageContent);
        }

        public static async Task SendRichEmbedMessage(this SocketTextChannel channel, string title, string messageContent)
        {
            var embed = GetEmbedBuilder(title, messageContent);
            await channel.SendMessageAsync(embed: embed.Build());
        }

        private static EmbedBuilder GetEmbedBuilder(string title, string messageContent)
        {
            var embed = new EmbedBuilder();
            // Or with methods
            if (messageContent.Length <= _fieldCharLimit)
            {
                embed.AddField(title, messageContent)
                    .WithColor(Color.DarkPurple);
            }
            else
            {
                int endOfSubstringIndex;
                for (int i = 0; i < messageContent.Length; i += endOfSubstringIndex)
                {
                    endOfSubstringIndex = _fieldCharLimit;
                    string fieldLimitSubstring;
                    try
                    {
                        fieldLimitSubstring = messageContent.Substring(i, endOfSubstringIndex);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        //the remaining part of the string is less that _fieldCharLimit, we should just add that
                        fieldLimitSubstring = messageContent.Substring(i);
                        if (i > 0) title = "⠀";
                        embed.AddField(title, fieldLimitSubstring)
                            .WithColor(Color.DarkPurple);
                        break;
                    }

                    int lastNewLineIndex = fieldLimitSubstring.LastIndexOf('\n');
                    int lastCommaIndex = fieldLimitSubstring.LastIndexOf(',');

                    string outputSubstring;
                    if (lastCommaIndex > lastNewLineIndex)
                    {
                        endOfSubstringIndex = lastCommaIndex;
                        outputSubstring = fieldLimitSubstring.Substring(0, endOfSubstringIndex);
                    }
                    else if (lastCommaIndex < lastNewLineIndex)
                    {
                        endOfSubstringIndex = lastNewLineIndex;
                        outputSubstring = fieldLimitSubstring.Substring(0, endOfSubstringIndex);
                    }
                    else 
                    {
                        //they are both -1 i.e. not found
                        outputSubstring = fieldLimitSubstring;
                    }

                    if (i > 0) title = "⠀";
                    embed.AddField(title, outputSubstring)
                        .WithColor(Color.DarkPurple);
                }
            }

            return embed;
        }
    }
}
