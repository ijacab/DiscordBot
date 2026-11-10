using Discord;
using DiscordBot;
using Xunit;

namespace Tests
{
    public class DiscordConfigTests
    {
        [Fact]
        public void Config_AsksForMessageText()
        {
            Assert.True(Startup.CreateDiscordConfig().GatewayIntents.HasFlag(GatewayIntents.MessageContent));
        }

        [Fact]
        public void Config_AsksForTheMemberListItDownloads()
        {
            var config = Startup.CreateDiscordConfig();

            Assert.True(config.AlwaysDownloadUsers);
            Assert.True(config.GatewayIntents.HasFlag(GatewayIntents.GuildMembers));
        }

        [Fact]
        public void Config_KeepsTheEverydayIntents()
        {
            var intents = Startup.CreateDiscordConfig().GatewayIntents;

            Assert.True(intents.HasFlag(GatewayIntents.Guilds));
            Assert.True(intents.HasFlag(GatewayIntents.GuildMessages));
            Assert.True(intents.HasFlag(GatewayIntents.DirectMessages));
            Assert.True(intents.HasFlag(GatewayIntents.GuildMessageReactions));
        }

        [Fact]
        public void Config_DoesNotAskForPresences()
        {
            // Presence is a third privileged intent that is not switched on in the portal; asking for it
            // would make Discord refuse the connection.
            Assert.False(Startup.CreateDiscordConfig().GatewayIntents.HasFlag(GatewayIntents.GuildPresences));
        }
    }
}
