using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using Common.Services;
using Common.Models;
using Common.Helpers;
using Discord.WebSocket;
using Discord;
using Microsoft.Extensions.Logging;
using Moq;
using WebAlerter;

namespace Tests
{
    public class WebAlterterTests
    {
        [Fact]
        public async Task Test1()
        {
            var settings = new GistSettings();
            settings.UserName = EnvironmentHelper.GetEnvironmentVariableOrThrow("GITHUB_USERNAME");
            settings.Id = EnvironmentHelper.GetEnvironmentVariableOrThrow("GITHUB_GIST_ID");

            using (var client = await GetDiscordSocketClient())
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Pepsi-Dog-Bot");
                httpClient.DefaultRequestHeaders.Add("Authorization", $"token { EnvironmentHelper.GetEnvironmentVariableOrThrow("GITHUB_PAT_TOKEN")}");
                var checker = new StrawmanChecker(new Mock<ILogger<StrawmanChecker>>().Object, httpClient, new GistService(httpClient, settings));
                await checker.NotifyTrades("ArrowTrades", client);
            }
        }

        private async Task<DiscordSocketClient> GetDiscordSocketClient()
        {
            var client = new DiscordSocketClient();
            string token = EnvironmentHelper.GetEnvironmentVariableOrThrow("DiscordBotToken");
            await client.LoginAsync(TokenType.Bot, token);
            await client.StartAsync();
            return client;
        }
    }
}
