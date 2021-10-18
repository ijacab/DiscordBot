using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot.Managers
{
    public partial class CommandManager
    {

        public async Task InitiateBet(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            ulong jacanId = 664279429844959243;
            ulong jacabId = 166477511469957120;
            if (!(message.Author.Id == jacanId || message.Author.Id == jacabId))
                return;

            ulong userId = ulong.Parse(args[0]);
            string userName = args[1];
            double betAmount = double.Parse(args[2]);
            var bet = await _betManager.InitiateBet(userId, userName, betAmount);
            await message.Channel.SendMessageAsync($"!WasBonusGranted:{bet.WasBonusGranted},IsFirstGameOfTheDay:{bet.IsFirstGameOfTheDay}");
        }

        public async Task ResolveBet(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            ulong jacanId = 664279429844959243;
            ulong jacabId = 166477511469957120;
            if (!(message.Author.Id == jacanId || message.Author.Id == jacabId))
                return;

            ulong userId = ulong.Parse(args[0]);
            string userName = args[1];
            double betAmount = double.Parse(args[2]);
            double baseWinnings = double.Parse(args[3]);
            bool isFirstGameOfTheDay = false;
            if (args.Count > 4)
            {
                isFirstGameOfTheDay = bool.Parse(args[4]);
            }
            var betResults = await _betManager.ResolveBet(userId, userName, betAmount, baseWinnings, isFirstGameOfTheDay);
            await message.Channel.SendMessageAsync($"!TotalsWinnings:{betResults.TotalWinnings},BonusWinnings:{betResults.BonusWinnings},NetWinnings:{betResults.NetWinnings}");
        }
    }
}
