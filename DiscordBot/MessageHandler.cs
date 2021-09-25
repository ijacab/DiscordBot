using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using DiscordBot.Managers;
using Common.Helpers;
using System.Linq;

namespace DiscordBot
{
    public class MessageHandler
    {

        private readonly DiscordSocketClient _client;
        private readonly ILogger<MessageHandler> _logger;
        private readonly CommandManager _commandManager;

        public MessageHandler(ILogger<MessageHandler> logger, CommandManager commandManager, DiscordSocketClient client)
        {
            _logger = logger;
            _commandManager = commandManager;
            _client = client;
        }

        public async Task StartAsync()
        {
            _client.MessageReceived += CommandHandler;
            _client.Log += Log;
            _client.Ready += DownloadChannels;

            //  You can assign your bot token to a string, and pass that in to connect.
            //  This is, however, insecure, particularly if you plan to have your code hosted in a public repository.
            string token = EnvironmentHelper.GetEnvironmentVariableOrThrow("DiscordBotToken");

            // Some alternative options would be to keep your token in an Environment Variable or a standalone file.
            // var token = Environment.GetEnvironmentVariable("NameOfYourEnvironmentVariable");
            // var token = File.ReadAllText("token.txt");
            // var token = JsonConvert.DeserializeObject<AConfigurationClass>(File.ReadAllText("config.json")).Token;
            if (_client.LoginState == LoginState.LoggedOut)
            {
                await _client.LoginAsync(TokenType.Bot, token);
                await _client.StartAsync();
            }
        }

        public async Task StopAsync()
        {
            await _client.StopAsync();
            _client.Dispose();
        }

        private Task Log(LogMessage msg)
        {
            _logger.LogDebug($"Discord log message: {msg}");
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        }

        private async Task DownloadChannels()
        {
            var guilds = _client.Guilds.ToList();
            await _client.DownloadUsersAsync(guilds);
        }

        private Task CommandHandler(SocketMessage message)
        {
            try
            {
                _commandManager.RunCommand(_client, message).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in {nameof(MessageHandler)}: {ex.Message}");

                try
                {
                    message.Channel.SendMessageAsync($"HOLY FUCK! I just encountered an error... Check my FUCKING logs");
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, $"Error sending channel message about error: {ex.Message}");
                }
            }

            return Task.CompletedTask;
        }

        public async Task RunBackgroundTasks()
        {
            try
            {
                await _commandManager.RunBackgroundTasks(_client);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error while running background tasks {nameof(MessageHandler)}: {ex.Message}");
            }
        }

    }
}
