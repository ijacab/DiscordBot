﻿using Discord.WebSocket;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot.Managers
{
    public partial class CommandManager
    {
        private List<ulong> _permittedIdsForBotCommands = new List<ulong> { 664279429844959243, 166477511469957120 };
        public async Task InitiateBet(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (!_permittedIdsForBotCommands.Contains(message.Author.Id))
                return;

            ulong userId = ulong.Parse(args[0]);
            string userName = args[1];
            double betAmount = double.Parse(args[2]);
            await _betManager.InitiateBet(userId, userName, betAmount);
        }

        public async Task ResolveBet(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (!_permittedIdsForBotCommands.Contains(message.Author.Id))
                return;

            ulong userId = ulong.Parse(args[0]);
            string userName = args[1];
            double betAmount = double.Parse(args[2]);
            double baseWinnings = double.Parse(args[3]);

            var betResults = await _betManager.ResolveBet(userId, userName, betAmount, baseWinnings);
            await message.Channel.SendMessageAsync($"!TotalWinnings:{betResults.TotalWinnings},BonusWinnings:{betResults.BonusWinnings},NetWinnings:{betResults.NetWinnings},WasBonusGranted:{betResults.WasBonusGranted}");
        }

        public async Task GetLeaderboardJson(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (!_permittedIdsForBotCommands.Contains(message.Author.Id))
                return;
            var coinAccounts = _coinService.GetAll();
            string json = JsonConvert.SerializeObject(coinAccounts, Formatting.None);

            if (json.Length < 2000)
            {
                await message.Channel.SendMessageAsync(json);
            }
            else
            {
                string path = Path.Combine(Directory.GetCurrentDirectory(), $"lb_json_{DateTime.Now.ToString("yyyyMMddHHmmss")}_{Guid.NewGuid()}.json");
                try
                {
                    File.WriteAllText(path, json);
                    await message.Channel.SendFileAsync(path);
                }
                finally
                {
                    File.Delete(path);
                }

            }
        }
    }
}
