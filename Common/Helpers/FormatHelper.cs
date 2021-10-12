using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Common.Helpers
{
    public static class FormatHelper
    {
        public static string GetCommaNumber(object numberObj, bool roundDown = true) //can be string or int
        {
            if (numberObj is double) 
            {
                double numberDouble = (double)numberObj;
                if (roundDown)
                    numberObj = Math.Floor(numberDouble);
                else
                    numberObj = Math.Ceiling(numberDouble);
            }
            return String.Format("{0:n0}", numberObj);
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
