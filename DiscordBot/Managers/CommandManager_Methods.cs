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

namespace DiscordBot.Managers
{
    public partial class CommandManager
    {
        private async Task Add(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count() != 2)
                throw new BadSyntaxException();

            if (args[0].StartsWith('.'))
                throw new BadSyntaxException("you can't FUCKING create a custom command that starts with a '.'. STOP TRYING BREAK ME MY BRIAN TOO BIG FOR YOU");

            if (args[0].Length <= 1)
                throw new BadSyntaxException("key must be length of 2 or greater.");

            await _mappingService.Add(args[0], args[1]);
            await message.Channel.SendMessageAsync($"Added mapping {args[0]} : {args[1]}");
            _customMappings = await _mappingService.GetAll();
        }

        private async Task Remove(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args.Count() != 1)
            {
                throw new BadSyntaxException();
            }
            else
            {
                await _mappingService.Remove(args[0]);
                await message.Channel.SendMessageAsync($"Removed mapping {args[0]}");
            }
        }

        private async Task Clear(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
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
                await _coinService.ClearAll();
            }
            else
            {
                return;
            }

            await message.Channel.SendMessageAsync($"Cleared all custom {args[0]}");

        }

        private async Task Age(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            string info = $@"Your account was created at {message.Author.CreatedAt.DateTime.ToString("yyyy-MM-dd hh:mm:ss")}";
            var age = DateTimeOffset.Now - message.Author.CreatedAt.DateTime;
            info += $"\n\nYou are {Convert.ToInt32(age.TotalDays)} days old.";
            await message.Channel.SendMessageAsync(info);
        }

        private async Task Test(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var users = await message.Channel.GetUsersAsync().Flatten().ToListAsync();
            var user = users.First(u => u.Username == "Jacab");
            await message.Channel.SendMessageAsync($"testing {user.Mention}");
            await message.Channel.SendMessageAsync($@"Hello {message.Author.Mention}");
        }

        private async Task AddReminder(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args[0] == "show")
            {
                string msg = "`Reminders`";
                var reminders = await _reminderService.GetAll();
                var remindersOrdered = reminders.OrderBy(r => r.TimeToRemind);

                foreach (var reminder in remindersOrdered)
                {
                    msg += $"{reminder.TimeToRemind.ToString("yyyy-MM-dd hh:mm:ss")}: {reminder.Message}";
                }
                await message.Channel.SendMessageAsync($"{msg}");
                return;
            }


            if (args.Count != 3)
                throw new BadSyntaxException();
            else
            {
                string reminderMessage = args[0].Trim('"');
                int numberOfTime = Convert.ToInt32(args[1]);
                numberOfTime = numberOfTime < 0 ? 0 : numberOfTime;
                string timeType = args[2].ToLower();
                var timeToRemind = timeType.StartsWith("second") ? DateTimeOffset.Now.AddSeconds(numberOfTime)
                    : timeType.StartsWith("minute") ? DateTimeOffset.Now.AddMinutes(numberOfTime)
                    : timeType.StartsWith("hour") ? DateTimeOffset.Now.AddHours(numberOfTime)
                    : timeType.StartsWith("day") ? DateTimeOffset.Now.AddDays(numberOfTime)
                    : DateTimeOffset.Now;

                await _reminderService.Add(message.Author.Mention, reminderMessage, timeToRemind, message.Channel.Id);
                await message.Channel.SendMessageAsync($@"Added reminder for UTC time: {timeToRemind.UtcDateTime}");
            }
        }

        private async Task Help(DiscordSocketClient client, SocketMessage message, List<string> args)
        {

            var rand = new Random().Next(15);
            if (rand == 0)
            {
                await message.Channel.SendMessageAsync("FUCK you I'm not helping you");
                await Task.Delay(TimeSpan.FromSeconds(3));
                await message.Channel.SendMessageAsync("just kidding");
            }

            string helpMessage = "`List of commands available:`\n";
            foreach (var command in _commands.Where(c => c.Hidden == false))
            {
                string desc = string.IsNullOrEmpty(command.Description) ? string.Empty : $" Desc: {command.Description}";
                string syntax = string.IsNullOrEmpty(command.Description) ? string.Empty : $" Syntax: {command.Syntax}";
                helpMessage += $"**.{command.Name}** \t{desc}{syntax}\n";
            }

            var helpTask = message.Channel.SendMessageAsync(helpMessage);

            string customCommandsMessage = $"`Custom commands:`\n";
            var customCommands = await _mappingService.GetAll();

            foreach (var command in customCommands)
            {
                string textToAdd = $"{command.Key}, ";
                if ((customCommandsMessage + textToAdd).Length >= _messageCharLimit)
                {
                    await message.Channel.SendMessageAsync(customCommandsMessage);
                    customCommandsMessage = $"`More custom commands:`\n";
                }
                else
                {
                    customCommandsMessage += textToAdd;
                }
            }

            await message.Channel.SendMessageAsync(customCommandsMessage.TrimEnd(' ').TrimEnd(','));
            await helpTask;
        }

        private async Task Roll(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            int rand;
            if (args.Count == 0)
            {
                rand = new Random().Next(0, 101);
                await message.Channel.SendMessageAsync($"You rolled {rand}");
                return;
            }

            if (int.TryParse(args[0], out int num1) == false)
                throw new BadSyntaxException();
            if (int.TryParse(args[1], out int num2) == false)
                throw new BadSyntaxException();
            if (num1 > num2)
                throw new BadSyntaxException();

            rand = new Random().Next(num1, num2 + 1);

            await message.Channel.SendMessageAsync($"You rolled {rand}");
        }

        private async Task Leaderboard(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var accounts = await _coinService.GetAll();
            string output = "`Leaderboard:`\n";


            int? prestige = null;
            foreach (var account in accounts.Accounts.OrderByDescending(a => a.NetWorth).OrderByDescending(a => a.PrestigeLevel))
            {
                if (prestige != null && account.PrestigeLevel < prestige)
                    output += "\n"; //line between each prestige lvl

                prestige = account.PrestigeLevel;

                string prestigeDisplay = prestige == 0 ? "" : $"**[P{prestige}]**";
                output += $"{account.Name}: {prestigeDisplay} ${FormatHelper.GetCommaNumber(account.NetWorth)}";

                if (account.DateHourlyBonusPaidFor == DateTime.UtcNow.ToString("yyyyMMdd"))
                    output += "   \\*";
                output += "\n";
            }
            output += "\n*Each day you will get $1000 \\* P level. If you bet over 50% in a single bet that day, you will also get an hourly bonus $1000 \\* P level + (up to) 10% net worth each hour for the rest of the day (UTC). Your leaderboard entry will show \\* symbol if you are currently receiving the bonus.*\n";
            output += $"\n*Type .prestige to level up your account if you have enough money (it will reset your money to ${FormatHelper.GetCommaNumber(_startingAmount)}). People who are lower prestige than you cannot donate to you.*";

            await message.Channel.SendMessageAsync(output);
        }

        private async Task Prestige(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            double amountForNextLevel = account.GetAmountRequiredForNextLevel();

            if (account.NetWorth < amountForNextLevel)
                throw new BadInputException($"You need at least ${FormatHelper.GetCommaNumber(amountForNextLevel)} to prestige to level {account.PrestigeLevel + 1}. You have ${FormatHelper.GetCommaNumber(account.NetWorth)}.");

            account.PrestigeLevel += 1;
            account.NetWorth = _startingAmount;
            await _coinService.Update(account.UserId, account.NetWorth, account.Name);

            await message.Channel.SendMessageAsync($"{message.Author.Mention} you have prestiged to level {account.PrestigeLevel} and your networth has been reset to {_startingAmount}.");
        }


        private async Task Donate(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var regex = new Regex(@"^<@(!)?(\d)*>$");
            if (args.Count != 2 || !regex.IsMatch(args[0]))
                throw new BadSyntaxException();


            ulong userId = message.Author.Id;
            ulong userIdToDonateTo = Convert.ToUInt64(args[0].TrimStart('<').TrimStart('@').TrimStart('!').TrimEnd('>')); //strip off <@! from the start and > from the end

            if (userId == userIdToDonateTo)
                throw new BadInputException("You can't **FUCKING** donate to yourself you stupid PIECE OF SHIT");

            double donationAmount = 0;
            try
            {
                donationAmount = Convert.ToDouble(args[1]);

                if (donationAmount <= 0)
                    throw new BadInputException("Can't donate 0 or less than 0 DUMBASS");
            }
            catch (Exception ex)
            {
                if (ex is BadInputException) throw ex;
                throw new BadSyntaxException();
            }

            CoinAccount account = await _coinService.Get(userId, message.Author.Username);
            CoinAccount donationAccount = await _coinService.Get(userIdToDonateTo, client.GetUser(userIdToDonateTo).Username);

            if (donationAccount.PrestigeLevel > account.PrestigeLevel)
                throw new BadInputException($"Can't donate to someone with a higher prestige level than you. You are level {account.PrestigeLevel} and they are {donationAccount.PrestigeLevel}.");

            if (donationAmount > account.NetWorth)
                throw new BadInputException($"Can't donate more than you have ({FormatHelper.GetCommaNumber(account.NetWorth)})... SCHYUPID IDIOT");

            account.NetWorth -= donationAmount;
            donationAccount.NetWorth += donationAmount * 0.8;


            string output = "";
            int chance = 11, multiplier = 6;
            int rand = new Random().Next(0, chance);
            if (rand == 0)
            {
                double returnAmount = multiplier * donationAmount;
                output += $"*You encountered a rare bonus reward for donating. You get back ${FormatHelper.GetCommaNumber(returnAmount)}.*\n";
                account.NetWorth += returnAmount;
            }

            output += $"{message.Author.Mention} you donated {FormatHelper.GetCommaNumber(donationAmount)} to {args[0]} (minus 20% tax)." +
            $"\n`Net worth:`" +
            $"\n{account.Name}: ${FormatHelper.GetCommaNumber(account.NetWorth)}" +
            $"\n{donationAccount.Name}: ${FormatHelper.GetCommaNumber(donationAccount.NetWorth)}";

            await _coinService.Update(account.UserId, account.NetWorth, message.Author.Username);
            await _coinService.Update(donationAccount.UserId, donationAccount.NetWorth, donationAccount.Name);

            await message.Channel.SendMessageAsync(output);
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

        private async Task Time(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (args[0] == "pst")
                await message.Channel.SendMessageAsync($"PST: {DateTime.UtcNow.AddHours(-7).ToString("yyyy-MM-dd HH:mm:ss")}");
            else if (args[0] == "aest")
                await message.Channel.SendMessageAsync($"AEST: {DateTime.UtcNow.AddHours(10).ToString("yyyy-MM-dd HH:mm:ss")}");
            else
                await message.Channel.SendMessageAsync($"UTC: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")}");
        }

        public async Task ImageSearch(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            if (_imageSearchStopped)
                return;

            ThrowIfBlackListed(message);

            var searchQuery = string.Join(' ', args);

            if (string.IsNullOrWhiteSpace(searchQuery))
                throw new BadInputException("You didn't provide an argument");

            try
            {
                var images = await _duckDuckGoService.GetImages(searchQuery);
                string output = images.Count > 0 ? images[new Random().Next(0, images.Count)]?.Image : "No image found";

                var msg = await message.Channel.SendMessageAsync(output);

                if (searchQuery.Contains("safeoff"))
                {
                    //delete nsfw messages after delay
                    _ = Task.Delay(TimeSpan.FromSeconds(90)).ContinueWith(async t =>
                    {
                        await msg.DeleteAsync();
                    });

                }
            }
            catch (Exception ex)
            {
                await message.Channel.SendMessageAsync(ex.Message);
            }
        }

        public void ThrowIfBlackListed(SocketMessage message)
        {
            if (_appSettings.BlackListedIds.Contains(message.Author.Id))
                throw new BadInputException("No");

            _appSettings.BlackListedWords.ForEach(bw =>
            {
                if (message.Content.Contains(bw)) throw new BadInputException("No");
            });
        }

        public async Task FaceGenerate(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            using var stream = await _faceService.Run();
            await message.Channel.SendFileAsync(stream: stream, "image.jpg");
        }
    }
}
