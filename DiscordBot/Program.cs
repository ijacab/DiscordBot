using System;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordBot
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateWebHostBuilder(args).Build().Run();
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            //we are also using https://kaffeine.herokuapp.com/ to keep app from idling after 30 minutes


            var hostBuilder = Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Debug))
                .ConfigureServices((hostContext, services) =>
                {
                });

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IS_HEROKU"))) //don't use this setting if running on heroku
                    hostBuilder.UseSystemd(); //linux service
            }

            return hostBuilder;

        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args)
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port))
                port = 5000;

            return WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>()
                .UseKestrel(options =>
                {
                    options.Listen(IPAddress.Any, port);
                });
        }
    }
}
