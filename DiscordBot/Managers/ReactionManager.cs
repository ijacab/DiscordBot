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
    public class ReactionManager
    {
        private List<ReactionCommand> _commands;
        private readonly ILogger<CommandManager> _logger;
        private readonly AppSettings _appSettings;
        private readonly CoinService _coinService;
        private readonly GameManager _gameManager;
        private readonly BetManager _betManager;
        private Dictionary<string, string> _customMappings;
        private ulong[] _adminIds = new ulong[] { 166477511469957120, 195207667902316544 };
        private int _argCharLimit = 950;
        private int _messageCharLimit = 2000;
        private double _startingAmount = 10000;

        private bool _stopped;
        private bool _imageSearchStopped = true;
        public ReactionManager(ILogger<CommandManager> logger, AppSettings appSettings, CoinService coinService, GameManager gameManager)
        {
            _logger = logger;
            _appSettings = appSettings;
            _coinService = coinService;
            _gameManager = gameManager;

            //need to add new commands in here as they are created
            _commands = new List<ReactionCommand>();
            _commands.Add(new ReactionCommand("bj", _gameManager.GameBlackjack) );
        }

        public async Task RunCommand(DiscordSocketClient client, Cacheable<IUserMessage, ulong> cachedMessage, ISocketMessageChannel originChannel, SocketReaction reaction)
        {
            if (_stopped)
            {
                _logger.LogDebug("Bot is stopped. Not running reaction event.");
                return;
            }

            if (client.GetUser(reaction.UserId).IsBot) //This ignores all reactions from bots
                return;

            if (reaction.Message.IsSpecified
                && reaction.Message.Value.Author.Id != Constants.BotId) //ignore reactions to non pepsi dog bot messages
                return;

            var message = await cachedMessage.GetOrDownloadAsync();
            if (message != null && reaction.User.IsSpecified)
            {
                ReactionCommand commandToExecute = null;
                foreach (var command in _commands)
                {
                    if (messageCommand.Equals(command.Name))
                    {
                        commandToExecute = command;
                    }
                }

                if (commandToExecute != null)
                {
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
            }

        }

    }
}
