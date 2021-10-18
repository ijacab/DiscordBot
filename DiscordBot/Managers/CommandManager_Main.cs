using Discord;
using Discord.WebSocket;
using DiscordBot.Exceptions;
using DiscordBot.Games;
using DiscordBot.Models;
using DiscordBot.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Managers
{
    public partial class CommandManager
    {
        private List<Command> _commands;
        private readonly ILogger<CommandManager> _logger;
        private readonly AppSettings _appSettings;
        private readonly MappingService _mappingService;
        private readonly ReminderService _reminderService;
        private readonly CoinService _coinService;
        private readonly DuckDuckGoService _duckDuckGoService;
        private readonly FaceService _faceService;
        private readonly BlackjackManager _blackjackManager;
        private readonly BetManager _betManager;
        private Dictionary<string, string> _customMappings;
        private ulong[] _adminIds = new ulong[] { 166477511469957120, 195207667902316544 };
        private int _argCharLimit = 950;
        private int _messageCharLimit = 2000;
        private double _startingAmount = 10000;

        private bool _stopped;
        private bool _imageSearchStopped = true;
        public CommandManager(ILogger<CommandManager> logger, AppSettings appSettings,
            MappingService mappingService, ReminderService reminderService, CoinService coinService, DuckDuckGoService duckDuckGoService, FaceService faceService,
            BlackjackManager blackjackManager, BetManager betManager)
        {
            _logger = logger;
            _appSettings = appSettings;
            _mappingService = mappingService;
            _reminderService = reminderService;
            _coinService = coinService;
            _duckDuckGoService = duckDuckGoService;
            _faceService = faceService;
            _blackjackManager = blackjackManager;
            _betManager = betManager;
            _customMappings = mappingService.GetAll().GetAwaiter().GetResult();

            //need to add new commands in here as they are created
            _commands = new List<Command>();

            _commands.Add(new Command("img", ImageSearch) { Description = "Searches DuckDuckGo for a random image related to a given search query" });
            _commands.Add(new Command("face", FaceGenerate) { Syntax = "`.face`", Description = "Gets an AI generated face from thispersondoesnotexist.com and displays it." });

            _commands.Add(new Command("hellotest", Test));
            _commands.Add(new Command("age", Age) { Description = "Displays age of your discord account", Syntax = ".age" });

            _commands.Add(new Command("add", Add) { Description = "Adds (or overwrites if exists) a custom command. When 'key' is typed, 'value' will be displayed.", Syntax = ".add \"key\" \"value\"" });
            _commands.Add(new Command("remove", Remove) { Description = "Removes a custom command that has been added using .add command.", Syntax = ".remove \"key\"" });
            _commands.Add(new Command("clear", Clear, hidden: true, requiresAdmin: true));

            _commands.Add(new Command("help", Help));
            _commands.Add(new Command("remindme", AddReminder) { Syntax = "`.remindme \"reminder for something\" 6 hours`"});
            _commands.Add(new Command("roll", Roll) { Syntax = ".roll 1 100" });

            _commands.Add(new Command("roulette", GameRoulette) { Description = "Plays a roulette game using virtual money. Type '.leaderboard' to see how much money you have", Syntax = ".roulette 00-100,0-300,1-10,36-100,Red-100,Black-200,FirstColumn-50,SecondColumn-300,ThirdColumn-20,Odd-100,Even-50,FirstDozen-10,SecondDozen-10,ThirdDozen-100" });
            _commands.Add(new Command("r", GameRoulette) { Hidden = true, Syntax = ".roulette 00-100,0-300,1-10,36-100,Red-100,Black-200,FirstColumn-50,SecondColumn-300,ThirdColumn-20,Odd-100,Even-50,FirstDozen-10,SecondDozen-10,ThirdDozen-100" });

            _commands.Add(new Command("blackjack", GameBlackjack) { Description = "Plays a multiplayer blackjack game using virtual money.Type '.leaderboard' to see how much money you have", Syntax = "`.bj betAmount` to start where betAmount is the amount you want to bet. For example `.bj 1000`" });
            _commands.Add(new Command("bj", GameBlackjack) { Hidden = true, Syntax = "`.bj betAmount` to start where betAmount is the amount you want to bet. For example `.bj 1000`" });

            _commands.Add(new Command("leaderboard", Leaderboard) { Description = "Shows the money leaderboard.", Syntax = ".leaderboard" });
            _commands.Add(new Command("lb", Leaderboard) { Description = "Shows the money leaderboard.", Syntax = ".leaderboard", Hidden = true });
            _commands.Add(new Command("archiveleaderboard", ArchiveLeaderboard) { Description = "Archives the leaderboard and resets the current one.", Syntax = ".leaderboard", RequiresAdmin = true, Hidden = true });
            _commands.Add(new Command("prestige", Prestige) { Description = "Levels up your account and resets your money to the starting amount.", Syntax = ".prestige" });

            _commands.Add(new Command("donate", Donate) { Description = "Donate some of your money to someone else.", Syntax = ".donate @person 1000" });
            _commands.Add(new Command("time", Time) { Syntax = "`.time utc`"});

            _commands.Add(new Command("start", Start, hidden: true, requiresAdmin: true));
            _commands.Add(new Command("stop", Stop, hidden: true, requiresAdmin: true));

            _commands.Add(new Command("initiatebet", Start, hidden: true));
            _commands.Add(new Command("resolvebet", Stop, hidden: true));

        }

        public async Task RunCommand(DiscordSocketClient client, SocketMessage message)
        {
            if (_stopped)
            {
                if (message.Content != ".start")
                {
                    _logger.LogDebug("Bot is stopped at the moment. Run .start to start it up again.");
                    return;
                }
            }

            if (message.Author.IsBot) //This ignores all commands from bots
                return;

            //custom commands
            string messageCommand;

            if (_customMappings.ContainsKey(message.Content))
            {
                await message.Channel.SendMessageAsync(_customMappings[message.Content]);
            }

            //filtering messages begin here
            if (!message.Content.StartsWith('.') || message.Content == ".") //this is your prefix
                return;

            //todo get rid of this after a short while
            if (message.Content.StartsWith(".game r"))
            {
                await message.Channel.SendMessageAsync($"{message.Author.Mention} this command has been changed to '.r'. You can use it like '.r firstColumn-min'");
                return;
            }

            try
            {
                EnsureInputContainsOnlyValidCharacters(message.Content);
            }
            catch (BadInputException ex)
            {
                await message.Channel.SendMessageAsync($"{message.Author.Mention} {ex.Message}");
                return;
            }

            int lengthOfCommand;
            if (message.Content.Contains(' '))
                lengthOfCommand = message.Content.IndexOf(' ');
            else
                lengthOfCommand = message.Content.Length;

            messageCommand = message.Content.Substring(1, lengthOfCommand - 1).ToLower();


            //normal commands begin here
            Command commandToExecute = null;
            foreach (var command in _commands)
            {
                if (messageCommand.Equals(command.Name))
                {
                    commandToExecute = command;
                }
            }

            if (commandToExecute != null)
            {
                if (commandToExecute.RequiresAdmin && _adminIds.Contains(message.Author.Id) == false)
                {
                    await message.Channel.SendMessageAsync($"You not admin. FUCK OFF");
                    return;
                }

                try
                {
                    var args = ExtractArguments(message.Content.Substring(lengthOfCommand));
                    foreach (string arg in args)
                    {
                        if (arg.Length > _argCharLimit)
                        {
                            await message.Channel.SendMessageAsync($"{message.Author.Mention} u CAN NOTA RUN COMMAN D THAT LONGER THAN {_argCharLimit} CHARACTER OKAY???? U TRY BREAK ME??? HUH??? try again neck time... nothin pesonnel kiddo");
                            return;
                        }
                    }
                    //execute command
                    await commandToExecute.ExecuteAsync(client, message, args);
                }
                catch (Exception ex)
                {
                    string errorMsg = ex.Message.StartsWith("Exception of type") ? "Something went wrong." : ex.Message;

                    if (ex is BadInputException)
                    {
                        await message.Channel.SendMessageAsync($"{message.Author.Mention} {errorMsg}");
                        return;
                    }

                    if (ex is BadSyntaxException)
                    {
                        await message.Channel.SendMessageAsync($"{message.Author.Mention} {errorMsg}\nSyntax is {commandToExecute.Syntax}");
                        return;
                    }

                    throw;
                }
            }
            else
            {
                await message.Channel.SendMessageAsync($"I don't FUCKING know what this command does!");
            }

        }

        public async Task RunBackgroundTasks(DiscordSocketClient client)
        {
            //reminder tasks
            Task reminderTask = Task.CompletedTask;
            try
            {
                reminderTask = SendReminders(client);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error while sending reminders {nameof(CommandManager)}: {ex.Message}");
            }

            //coin tasks
            Task coinServiceTask = _coinService.AddInterest();
            await Task.WhenAll(reminderTask, coinServiceTask);
        }

        private async Task SendReminders(DiscordSocketClient client)
        {
            var reminders = await _reminderService.GetAll();
            var remindersToAction = reminders.Where(r => DateTime.Now >= r.TimeToRemind);

            if (remindersToAction.Count() > 0)
            {
                foreach (var reminder in remindersToAction)
                {
                    string reminderMessage = $"Reminder: {reminder.AuthorMention} {reminder.Message}";
                    var messageChannel = client.GetChannel(reminder.ChannelId) as IMessageChannel;

                    if (messageChannel == null)
                    {
                        var dmChannel = client.GetDMChannelAsync(reminder.ChannelId);
                        await messageChannel.SendMessageAsync(reminderMessage);
                    }
                    else
                    {
                        await messageChannel.SendMessageAsync(reminderMessage);
                    }

                    await _reminderService.Remove(reminder.Id);
                }
            }
        }

        private void EnsureInputContainsOnlyValidCharacters(string input)
        {
            foreach (char c in input)
            {
                if (c >= 128)
                {
                    throw new BadInputException();
                }
            }
        }

        private List<string> ExtractArguments(string commandArgumentString)
        {
            //https://stackoverflow.com/a/14655199
            var result = commandArgumentString.Split('"')
                                 .Select((element, index) => index % 2 == 0  // If even index
                                                       ? element.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)  // Split the item
                                                       : new string[] { element })  // Keep the entire item
                                 .SelectMany(element => element).ToList();
            return result;
        }
    }
}
