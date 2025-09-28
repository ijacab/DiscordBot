using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
namespace DiscordBot.Installers
{
    public class OllamaInstaller
    {
        private readonly ILogger<OllamaInstaller> _logger;

        public OllamaInstaller(ILogger<OllamaInstaller> logger)
        {
            _logger = logger;
        }

        public async Task EnsureOllamaAndModelInstalled()
        {
            await EnsureOllamaInstalledAsync();
            await EnsureSmollmModelAsync();
        }

        private async Task EnsureOllamaInstalledAsync()
        {
            var check = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "ollama",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using (var proc = Process.Start(check))
            {
                string output = await proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit();

                if (!string.IsNullOrWhiteSpace(output))
                {
                    _logger.LogInformation("Ollama already installed.");
                    return;
                }
            }

            _logger.LogInformation("Installing Ollama...");
            var install = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "-c \"curl -fsSL https://ollama.com/install.sh | sh\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using (var proc = Process.Start(install))
            {
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();

                _logger.LogInformation(stdout);
                if (!string.IsNullOrEmpty(stderr))
                    _logger.LogError(stderr);
            }
        }

        private async Task EnsureSmollmModelAsync()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ollama",
                Arguments = "pull smollm:135m",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using (var proc = Process.Start(psi))
            {
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();

                _logger.LogInformation(stdout);
                if (!string.IsNullOrEmpty(stderr))
                    _logger.LogError(stderr);
            }
        }
    }

}