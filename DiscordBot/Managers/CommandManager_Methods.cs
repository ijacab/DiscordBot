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
            var accounts = _coinService.GetAll();
            string output = "`Leaderboard:`\n";


            int? prestige = null;
            foreach (var account in accounts.Accounts.OrderByDescending(a => a.NetWorth).OrderByDescending(a => a.PrestigeLevel))
            {
                if (prestige != null && account.PrestigeLevel < prestige)
                    output += "\n"; //line between each prestige lvl

                prestige = account.PrestigeLevel;

                string prestigeDisplay = prestige == 0 ? "" : $"**[P{prestige}]**";
                output += $"{account.Name}: {prestigeDisplay} ${FormatHelper.GetCommaNumber(account.NetWorth)}";

                if (account.MostRecentDatePlayed == DateTime.UtcNow.ToString("yyyyMMdd"))
                    output += "   \\*";
                output += "\n";
            }
            output += "\n*Each day you will get $1000 x P level. If you bet over 50% in a single bet that day, you will also get an hourly bonus $1000 x P level + (up to) 6% net worth each hour for the rest of the day (UTC). Your leaderboard entry will show \\* symbol if you are currently receiving the bonus.*\n";
            output += "\n*The more money you win from playing in a day, the more money you make from further wins (via a bonus), up to a maxmium of 3x winnings bonus.*\n";
            output += $"\n*Type .prestige to level up your account if you have enough money (it will reset your money to ${FormatHelper.GetCommaNumber(_startingAmount)}). People who are lower prestige than you cannot donate to you.*";

            await message.Channel.SendMessageAsync(output);
        }

        private async Task ArchiveLeaderboard(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            string fileNamePrefix = "leaderboard_season", fileNameExtension = ".json";
            string fileNamePattern = "leaderboard_season*.json";
            var fileNames = Directory.GetFiles(Directory.GetCurrentDirectory(), fileNamePattern);
            int i = 1;
            if (fileNames.Any())
            {
                string latestFileNameFullPath = fileNames.OrderByDescending(fn => fn).First();
                string latestFileName = Path.GetFileName(latestFileNameFullPath);
                i = 1 + int.Parse(latestFileName.Replace(fileNamePrefix, "").Replace(fileNameExtension, ""));
            }
            string fileName = fileNamePattern.Replace("*", i.ToString());
            string path = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            var accounts = _coinService.GetAll();
            File.WriteAllText(path, JsonConvert.SerializeObject(accounts));

            await message.Channel.SendMessageAsync($"Season {i} archived.");
        }

        private async Task Prestige(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            CoinAccount account = await _coinService.Get(message.Author.Id, message.Author.Username);
            double amountForNextLevel = account.GetAmountRequiredForNextLevel();

            if (account.NetWorth < amountForNextLevel)
                throw new BadInputException($"You need at least ${FormatHelper.GetCommaNumber(amountForNextLevel)} to prestige to level {account.PrestigeLevel + 1}. You have ${FormatHelper.GetCommaNumber(account.NetWorth)}.");

            account.PrestigeLevel += 1;
            account.NetWorth = _startingAmount;
            account.NetWinningsToday = 0;
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

            if (client.GetUser(userIdToDonateTo).IsBot)
                throw new BadInputException("You STUPID *CUNT*! YOU CAN'T DONATE TO A FUCKING BOT!");


            if (!double.TryParse(args[1], out double donationAmount))
                throw new BadSyntaxException();

            if (donationAmount <= 0)
                throw new BadInputException("Can't donate 0 or less than 0 DUMBASS");

            CoinAccount account = await _coinService.Get(userId, message.Author.Username);
            CoinAccount donationReceiverAccount = await _coinService.Get(userIdToDonateTo, client.GetUser(userIdToDonateTo).Username);

            if (donationReceiverAccount.PrestigeLevel > account.PrestigeLevel)
                throw new BadInputException($"Can't donate to someone with a higher prestige level than you. You are level {account.PrestigeLevel} and they are {donationReceiverAccount.PrestigeLevel}.");

            if (donationAmount > account.NetWorth)
                throw new BadInputException($"Can't donate more than you have ({FormatHelper.GetCommaNumber(account.NetWorth)})... SCHYUPID IDIOT");

            account.NetWorth -= donationAmount;
            donationReceiverAccount.NetWorth += donationAmount * 0.8;

            //statsfor donater
            account.Stats.TotalMoneyDonated += donationAmount;
            if (donationAmount > account.Stats.MaxMoneyDonatedAtOnce)
                account.Stats.MaxMoneyDonatedAtOnce = donationAmount;

            if (account.Stats.DonationAmountsToDict.ContainsKey(userIdToDonateTo))
                account.Stats.DonationAmountsToDict[userIdToDonateTo] += donationAmount;
            else
                account.Stats.DonationAmountsToDict.Add(userIdToDonateTo, donationAmount);

            //stats for donatee
            donationReceiverAccount.Stats.TotalMoneyReceivedFromDonations += donationAmount;
            if (donationAmount > account.Stats.MaxMoneyReceivedFromDonationAtOnce)
                donationReceiverAccount.Stats.MaxMoneyReceivedFromDonationAtOnce = donationAmount;

            if (donationReceiverAccount.Stats.DonationAmountsFromDict.ContainsKey(userId))
                donationReceiverAccount.Stats.DonationAmountsFromDict[userId] += donationAmount;
            else
                donationReceiverAccount.Stats.DonationAmountsFromDict.Add(userId, donationAmount);

            string output = "";
            int chance = 12, multiplier = 6;
            int rand = new Random().Next(0, chance);
            if (rand == 0)
            {
                double returnAmount = multiplier * donationAmount;
                output += $"*You encountered a rare bonus reward for donating. You get back ${FormatHelper.GetCommaNumber(returnAmount)}.*\n";
                account.NetWorth += returnAmount;

                //stats for donation bonus
                account.Stats.DonationBonusesEncountered += 1;
            }

            output += $"{message.Author.Mention} you donated {FormatHelper.GetCommaNumber(donationAmount)} to {args[0]} (minus 20% tax)." +
            $"\n`Net worth:`" +
            $"\n{account.Name}: ${FormatHelper.GetCommaNumber(account.NetWorth)}" +
            $"\n{donationReceiverAccount.Name}: ${FormatHelper.GetCommaNumber(donationReceiverAccount.NetWorth)}";

            await _coinService.Update(account.UserId, account.NetWorth, message.Author.Username);
            await _coinService.Update(donationReceiverAccount.UserId, donationReceiverAccount.NetWorth, donationReceiverAccount.Name);

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

                if (searchQuery.Contains("safeoff", StringComparison.OrdinalIgnoreCase))
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
                if (message.Content.Contains(bw)) 
                {
                    _appSettings.BlackListedIds.Add(message.Author.Id);
                    string json = File.ReadAllText("appsettings.json");
                    dynamic jsonObj = JsonConvert.DeserializeObject(json);
                    jsonObj["AppSettings"]["BlackListedIds"] = JsonConvert.SerializeObject(_appSettings.BlackListedIds);
                    string output = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
                    File.WriteAllText("appsettings.json", output);
                    throw new BadInputException("No"); 
                }
            });
        }

        public async Task FaceGenerate(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            using var stream = await _faceService.Run();
            await message.Channel.SendFileAsync(stream: stream, "image.jpg");
        }

        private async Task Stats(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            var accounts = _coinService.GetAll().Accounts;
            string title = "Stats";

            var stats = accounts.Select(a => a.Stats);

            Type type = typeof(CoinAccountStats);
            PropertyInfo[] properties = type.GetProperties();

            foreach (PropertyInfo property in properties)
            {
                var maxBetStat = stats.OrderByDescending(s => s.GetType().GetProperty(property.Name)).First();
                var maxBetAccount = accounts.First(a => a.Stats == maxBetStat);

                await message.Channel.SendMessageAsync($"{property.Name}: {property.GetValue(maxBetStat, null)}, {maxBetAccount.Name}");
            }

            //var maxBetStat = stats.OrderByDescending(s => s.MaxMoneyBetAtOnce).First();
            //var maxBetAccount = accounts.Where(a => a.Stats == maxBetStat);

            //var maxLossStat = stats.OrderByDescending(s => s.MaxMoneyLostAtOnce).First();
            //var maxLossAccount = accounts.Where(a => a.Stats == maxLossStat);

            //var maxWinStat = stats.OrderByDescending(s => s.MaxMoneyWonAtOnce).First();
            //var maxWinAccount = accounts.Where(a => a.Stats == maxWinStat);

            //var maxWinStreakStat = stats.OrderByDescending(s => s.MaxWinStreak).First();
            //var maxWinStreakAccount = accounts.Where(a => a.Stats == maxWinStreakStat);

            //var maxDonationStat = stats.OrderByDescending(s => s.MaxMoneyDonatedAtOnce).First();
            //var maxDonationAccount = accounts.Where(a => a.Stats == maxDonationStat);

            //var maxDonationReceivedStat = stats.OrderByDescending(s => s.MaxMoneyReceivedFromDonationAtOnce).First();
            //var maxDonationReceivedAccount = accounts.Where(a => a.Stats == maxDonationReceivedStat);

            //var maxWinStreakStat = stats.OrderByDescending(s => s.).First();
            //var maxWinStreakAccount = accounts.Where(a => a.Stats == maxWinStreakStat);

            //var maxWinStreakStat = stats.OrderByDescending(s => s.MaxWinStreak).First();
            //var maxWinStreakAccount = accounts.Where(a => a.Stats == maxWinStreakStat);


        }
    }
}
