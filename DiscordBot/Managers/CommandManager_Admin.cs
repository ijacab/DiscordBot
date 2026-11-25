using Microsoft.Extensions.Logging;
using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Games.Models;
using Common.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static DiscordBot.Models.CoinAccounts;
using System.IO;
using Newtonsoft.Json;
using Common.Services;
using System.Reflection;
using DiscordBot.Models;
using DiscordBot.Services;
using DiscordBot.Text;
using DiscordBot.Games.Models.Cards;

namespace DiscordBot.Managers
{
    /// <summary>Bot admin commands: `.clear`, `.start`, `.stop`, bans, disk space.</summary>
    public partial class CommandManager
    {

        private async Task Clear(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count == 0)
                throw new BadInputException("Clear WHAT? `.clear mapping`, `.clear remind` or `.clear coin` (a new season).");
            if (args[0].StartsWith("mapping"))
            {
                await _mappingService.ClearAll();
                _customMappings = await _mappingService.GetAll();
            }
            else if (args[0].StartsWith("remind"))
            {
                await _reminderService.ClearAll();
            }
            else if (args[0].StartsWith("coin"))
            {
                _coinService.AllTimeLeaders(_worth.TotalWorth, DateTime.UtcNow); //the season's peaks go on the all-time board before the reset
                await _coinService.ClearAll();
                await _stockService.ClearAll();
            }
            else
            {
                return;
            }

            await message.Channel.SendMessageAsync($"Cleared all custom {args[0]}");

        }

        private async Task Start(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 0 && args[0].StartsWith("im"))
            {
                _imageSearchStopped = false;
                await message.Channel.SendMessageAsync("Image search enabled.");
                return;
            }

            await message.Channel.SendMessageAsync("I WAKE");
            _stopped = false;
        }

        private async Task Stop(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count > 0 && args[0].StartsWith("im"))
            {
                _imageSearchStopped = true;
                await message.Channel.SendMessageAsync("Image search disabled.");
                return;
            }

            await message.Channel.SendMessageAsync("I SLEEP");
            _stopped = true;
        }

        private async Task GetFreeSpace(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            string path = AppContext.BaseDirectory;
            var drive = new DriveInfo(path);
            double freeGB = drive.AvailableFreeSpace / 1024 / 1024 / 1024;
            await message.Channel.SendMessageAsync($"Free space: {freeGB} GB");
        }

        private async Task Ban(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var target = message.MentionedUsers.FirstOrDefault();
            if (target == null)
                throw new BadSyntaxException("you have to mention someone to ban them");

            if (_adminIds.Contains(target.Id))
                throw new BadSyntaxException("you can't ban an admin");

            if (_bannedUserIds.Contains(target.Id))
            {
                await message.Channel.SendMessageAsync($"{target.Mention} is already banned");
                return;
            }

            await _banService.Save(_bannedUserIds.Concat(new[] { target.Id }));
            _bannedUserIds.Add(target.Id);
            await message.Channel.SendMessageAsync($"Banned {target.Mention}");
        }

        private async Task Unban(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var target = message.MentionedUsers.FirstOrDefault();
            if (target == null)
                throw new BadSyntaxException("you have to mention someone to unban them");

            if (!_bannedUserIds.Contains(target.Id))
            {
                await message.Channel.SendMessageAsync($"{target.Mention} is not banned");
                return;
            }

            await _banService.Save(_bannedUserIds.Where(id => id != target.Id));
            _bannedUserIds.Remove(target.Id);
            await message.Channel.SendMessageAsync($"Unbanned {target.Mention}");
        }
    }
}
