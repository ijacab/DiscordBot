﻿using Common.Helpers;
using Common.Models;
using Common.Services;
using Discord;
using Discord.WebSocket;
using DiscordBot.Games;
using DiscordBot.Games.Managers;
using DiscordBot.Managers;
using DiscordBot.Models;
using DiscordBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http.Headers;
using WebAlerter;

namespace DiscordBot
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddHttpsRedirection(options => { options.HttpsPort = 443; });

            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var appSettings = config.GetSection("AppSettings").Get<AppSettings>();
            services.AddSingleton<AppSettings>(appSettings);

            services.AddSingleton<DiscordSocketClient>(sp => 
                {
                    var config = new DiscordSocketConfig()
                    {
                        AlwaysDownloadUsers = true
                    };
                    var client = new DiscordSocketClient(config);
                    return client;
                }
            );
            services.AddTransient<MappingService>();
            services.AddTransient<ReminderService>();
            services.AddSingleton<CoinService>();
            services.AddSingleton<CommandManager>();
            services.AddSingleton<MessageHandler>();
            services.AddTransient<StrawmanChecker>();
            services.AddSingleton<BlackjackManager>();
            services.AddSingleton<BattleArenaManager>();
            services.AddScoped<DuckDuckGoService>();
            services.AddSingleton<BetManager>();
            services.AddSingleton<LocalFileService>();
            services.AddSingleton<GPTService>();

            services.AddControllers();
            services.AddCors(options =>
            {
                options.AddPolicy("CorsPolicy",
                    builder => builder.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader());
            });


            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                           ForwardedHeaders.XForwardedProto;
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();


            });

            services.AddTransient<GistSettings>(sp =>
            {
                var settings = new GistSettings();
                settings.UserName = EnvironmentHelper.GetEnvironmentVariable("GITHUB_USERNAME", false);
                settings.Id = EnvironmentHelper.GetEnvironmentVariable("GITHUB_GIST_ID", false);
                return settings;
            });

            services.AddHttpClient<GistService>(h =>
            {
                h.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                h.DefaultRequestHeaders.Add("User-Agent", "Pepsi-Dog-Bot");
                h.DefaultRequestHeaders.Add("Authorization", $"token { EnvironmentHelper.GetEnvironmentVariable("GITHUB_PAT_TOKEN", false)}");
            });

            services.AddHttpClient<StrawmanChecker>(h =>
            {
                h.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                h.DefaultRequestHeaders.Add("User-Agent", "Pepsi-Dog-Bot-2");
                h.DefaultRequestHeaders.Add("Authorization", $"token { EnvironmentHelper.GetEnvironmentVariable("GITHUB_PAT_TOKEN", false)}");
            });

            services.AddHttpClient<FaceService>(h =>
            {
                h.BaseAddress = new Uri(@"https://thispersondoesnotexist.com");
                h.DefaultRequestHeaders.Add("User-Agent", "Pepsi-Dog-Bot-2");
            });

            services.AddHostedService<ChatWorker>();
            services.AddHostedService<WebAlerterWorker>();

        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseForwardedHeaders();

            if (!string.IsNullOrEmpty(EnvironmentHelper.GetEnvironmentVariable("IS_HEROKU", false)))
            {
                Console.WriteLine("Use https redirection");
                app.UseHttpsRedirection();
            }


            app
                .UseRouting()
                .UseDefaultFiles()
                .UseStaticFiles()
                .UseCors("CorsPolicy")
                .UseEndpoints(endpoints =>
                {
                    endpoints.MapDefaultControllerRoute();
                });
        }
    }
}
