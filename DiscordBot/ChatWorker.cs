using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordBot
{
    public class ChatWorker : BackgroundService
    {
        private readonly ILogger<ChatWorker> _logger;
        private readonly MessageHandler _messageHandler;

        public ChatWorker(ILogger<ChatWorker> logger, MessageHandler messageHandler)
        {
            _logger = logger;
            _messageHandler = messageHandler;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if(!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await _messageHandler.StartAsync();

                while (true)
                {
                    await _messageHandler.RunBackgroundTasks();
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
