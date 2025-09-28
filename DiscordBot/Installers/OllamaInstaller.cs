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

        /// <summary>
        /// Entry point: ensures Ollama is installed, service is running, and smollm model is pulled.
        /// </summary>
        public async Task EnsureOllamaAndModelInstalled()
        {
            await EnsureOllamaInstalledAsync();
            await EnsureOllamaServiceAsync();
            await EnsureSmollmModelAsync();
        }

        private async Task EnsureOllamaInstalledAsync()
        {
            if (await CommandExists("ollama"))
            {
                _logger.LogInformation("Ollama binary already installed.");
                return;
            }

            _logger.LogInformation("Installing Ollama...");
            await RunCommand("/bin/bash", "-c \"curl -fsSL https://ollama.com/install.sh | sh\"");
        }

        private async Task EnsureOllamaServiceAsync()
        {
            const string servicePath = "/etc/systemd/system/ollama.service";

            if (!File.Exists(servicePath))
            {
                _logger.LogInformation("Creating Ollama systemd service...");

                // Detect current user and home directory
                string user = Environment.UserName;
                string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                string serviceFile = $@"
[Unit]
Description=Ollama Service
After=network.target

[Service]
ExecStart=/usr/local/bin/ollama serve
Restart=always
User={user}
WorkingDirectory={homeDir}

[Install]
WantedBy=multi-user.target
";

                // Write to a temp file first
                string tmpPath = "/tmp/ollama.service";
                await File.WriteAllTextAsync(tmpPath, serviceFile);

                // Move into place with sudo
                await RunCommand("sudo", $"mv {tmpPath} {servicePath}");
                await RunCommand("sudo", "systemctl daemon-reexec");
                await RunCommand("sudo", "systemctl enable ollama");
            }
            else
            {
                _logger.LogInformation("Ollama systemd service already exists.");
            }

            _logger.LogInformation("Starting Ollama service...");
            await RunCommand("sudo", "systemctl start ollama");
        }

        private async Task EnsureSmollmModelAsync()
        {
            _logger.LogInformation("Ensuring smollm:135m model is pulled...");
            await RunCommand("ollama", "pull smollm:135m");
        }

        private async Task<bool> CommandExists(string command)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi);
            string output = await proc.StandardOutput.ReadToEndAsync();
            proc.WaitForExit();
            return !string.IsNullOrWhiteSpace(output);
        }

        private async Task RunCommand(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi);
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            if (!string.IsNullOrEmpty(stdout))
                _logger.LogInformation(stdout.Trim());
            if (!string.IsNullOrEmpty(stderr))
                _logger.LogError(stderr.Trim());
        }
    }
}