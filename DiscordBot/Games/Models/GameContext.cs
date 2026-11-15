using Common.Helpers;
using Discord;
using Discord.WebSocket;
using System.Threading.Tasks;

namespace DiscordBot.Games.Models
{
    /// <summary>
    /// Who is acting in a multiplayer game and where to answer: built from a typed command or from a button click, so the
    /// game managers don't depend on a <see cref="SocketMessage"/>.
    /// </summary>
    public sealed class GameContext
    {
        public ulong UserId { get; init; }
        public string Username { get; init; }
        public IMessageChannel Channel { get; init; }
        public ulong GuildId { get; init; }

        public static GameContext From(SocketMessage message) => new GameContext
        {
            UserId = message.Author.Id,
            Username = message.Author.Username,
            Channel = message.Channel,
            GuildId = (message.Channel as SocketGuildChannel)?.Guild.Id ?? 0,
        };

        public static GameContext From(SocketInteraction interaction) => new GameContext
        {
            UserId = interaction.User.Id,
            Username = interaction.User.Username,
            Channel = interaction.Channel,
            GuildId = interaction.GuildId ?? 0,
        };

        /// <summary>Posts an embed in the channel, with buttons if given.</summary>
        public Task SendEmbed(string title, string text, MessageComponent components = null) =>
            Channel.SendMessageAsync(embed: DiscordHelper.GetEmbedBuilder(title, text).Build(), components: components);
    }
}
