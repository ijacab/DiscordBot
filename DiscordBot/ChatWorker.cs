using System;
using System.Threading;
using System.Threading.Tasks;
using DiscordBot.Installers;
using DiscordBot.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordBot
{
    public class ChatWorker : BackgroundService
    {
        private readonly ILogger<ChatWorker> _logger;
        private readonly MessageHandler _messageHandler;
        private readonly OllamaInstaller _ollamaInstaller;
        private readonly HangWatchdog _watchdog;
        private bool _isStartupComplete = false;

        public ChatWorker(ILogger<ChatWorker> logger, MessageHandler messageHandler, OllamaInstaller ollamaInstaller, HangWatchdog watchdog)
        {
            _logger = logger;
            _messageHandler = messageHandler;
            _ollamaInstaller = ollamaInstaller;
            _watchdog = watchdog;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if(!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);

                if (!_isStartupComplete)
                {
                    try
                    {
                        await _ollamaInstaller.EnsureOllamaAndModelInstalled();
                    }
                    catch(Exception ex)
                    {
                        _logger.LogError(ex, "Failed to install ollama and the model: {ExMsg}", ex.Message);
                    }

                    _isStartupComplete = true;
                }


                await _messageHandler.StartAsync();
                //restarts the bot if it stops answering Discord or this loop gets stuck
                _watchdog.Start();

                while (!stoppingToken.IsCancellationRequested)
                {
                    await _messageHandler.RunBackgroundTasks();
                    _watchdog.MarkLoopPass();
                    await Task.Delay(TimeSpan.FromMinutes(1));
                }
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            await _messageHandler.StopAsync();
            await base.StopAsync(stoppingToken);
        }
    }
}
