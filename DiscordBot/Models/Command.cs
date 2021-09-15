using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot.Models
{
    public class Command
    {
        private Func<DiscordSocketClient, SocketMessage, List<string>, Task> _commandFunction;
        public bool Hidden;
        public bool RequiresAdmin;
        public string Name { get; set; }
        public string Description { get; set; }
        public string Syntax { get; set; }

        public Command(string name, Func<DiscordSocketClient, SocketMessage, List<string>, Task> commandFunction, bool hidden = false, bool requiresAdmin = false)
        {
            Name = name;
            _commandFunction = commandFunction;
            Hidden = hidden;
            RequiresAdmin = requiresAdmin;
        }

        public async Task ExecuteAsync(DiscordSocketClient client, SocketMessage message, List<string> args)
        {
            await _commandFunction.Invoke(client, message, args);
        }
    }
}
