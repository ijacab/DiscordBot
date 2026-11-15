using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;

namespace DiscordBot.Services
{
    /// <summary>
    /// Restarts the bot if it hangs. Commands run on Discord.Net's gateway task, so a command that never finishes stops the
    /// heartbeats and the bot goes offline while the process stays up, which systemd doesn't notice. This checks once a minute,
    /// on its own timer thread, and if the bot hasn't been connected to Discord, or the minute loop hasn't finished a pass, for
    /// <see cref="Limit"/>, it fails fast so systemd (Restart=always) starts it again.
    /// </summary>
    public class HangWatchdog : IDisposable
    {
        public static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

        private readonly DiscordSocketClient _client;
        private readonly ILogger<HangWatchdog> _logger;
        private Timer _timer;
        private long _startedTicks;
        private long _lastConnectedTicks;
        private long _lastPassTicks;

        public HangWatchdog(DiscordSocketClient client, ILogger<HangWatchdog> logger)
        {
            _client = client;
            _logger = logger;
        }

        /// <summary>Starts watching. Call once, just before the minute loop starts.</summary>
        public void Start()
        {
            long now = DateTime.UtcNow.Ticks;
            _startedTicks = _lastConnectedTicks = _lastPassTicks = now;
            _timer = new Timer(_ => Check(), null, CheckEvery, CheckEvery);
        }

        /// <summary>The minute loop finished a pass.</summary>
        public void MarkLoopPass() => Interlocked.Exchange(ref _lastPassTicks, DateTime.UtcNow.Ticks);

        private void Check()
        {
            var now = DateTime.UtcNow;
            if (_client.ConnectionState == ConnectionState.Connected)
                Interlocked.Exchange(ref _lastConnectedTicks, now.Ticks);

            var started = new DateTime(Interlocked.Read(ref _startedTicks), DateTimeKind.Utc);
            var lastConnected = new DateTime(Interlocked.Read(ref _lastConnectedTicks), DateTimeKind.Utc);
            var lastPass = new DateTime(Interlocked.Read(ref _lastPassTicks), DateTimeKind.Utc);
            string reason = ShouldRestart(now, started, lastConnected, lastPass);
            if (reason == null) return;

            _logger.LogCritical($"Watchdog: {reason}. Exiting so systemd restarts the bot.");
            Environment.FailFast($"Watchdog: {reason}.");
        }

        /// <summary>Why the bot should restart, or null when it's healthy. The first <see cref="Limit"/> after starting is never a hang.</summary>
        public static string ShouldRestart(DateTime now, DateTime started, DateTime lastConnected, DateTime lastPass)
        {
            if (now - started < Limit) return null;
            if (now - lastConnected >= Limit) return $"not connected to Discord for {(int)(now - lastConnected).TotalMinutes} minutes";
            if (now - lastPass >= Limit) return $"the minute loop hasn't finished for {(int)(now - lastPass).TotalMinutes} minutes";
            return null;
        }

        public void Dispose() => _timer?.Dispose();
    }
}
