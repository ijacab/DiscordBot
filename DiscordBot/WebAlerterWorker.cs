using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Helpers;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebAlerter;

namespace DiscordBot
{
    public class WebAlerterWorker : BackgroundService
    {
        private readonly ILogger<ChatWorker> _logger;
        private readonly StrawmanChecker _strawmanChecker;
        private readonly DiscordSocketClient _client;

        public WebAlerterWorker(ILogger<ChatWorker> logger, StrawmanChecker strawmanChecker, DiscordSocketClient client)
        {
            _logger = logger;
            _strawmanChecker = strawmanChecker;
            _client = client;
        }

        public async Task StartDiscordClient()
        {
            string token = EnvironmentHelper.GetEnvironmentVariableOrThrow("DiscordBotToken");

            if (_client.LoginState == LoginState.LoggedOut)
            {
                await _client.LoginAsync(TokenType.Bot, token);
                await _client.StartAsync();
            }
        }

        public async Task StopDiscordClient()
        {
            await _client.StopAsync();
            _client.Dispose();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if(!stoppingToken.IsCancellationRequested)
            {

                _logger.LogInformation("WebAlerterWorker running at: {time}", DateTimeOffset.Now);
                await StartDiscordClient();

                while (true)
                {
                    await _strawmanChecker.Run(_client);
                    await Task.Delay(TimeSpan.FromMinutes(15));
                }
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            await StopDiscordClient();
            await base.StopAsync(stoppingToken);
        }



    }
}
