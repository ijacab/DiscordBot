using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;
using Common.Services;
using DiscordBot.Models.ConvoService;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using Microsoft.VisualBasic;
using System.Linq;

namespace DiscordBot.Services
{
    public class ConvoService
    {
        private const string _fileName = "convo_options.json";
        private readonly HttpClient _http;
        private readonly ILogger<ConvoService> _logger;
        private readonly LocalFileService _localFileService;
        private CancellationTokenSource _cts;

        public ConvoService(ILogger<ConvoService> logger, LocalFileService localFileService)
        {
            _http = new HttpClient { BaseAddress = new Uri("http://localhost:11434/") };
            _logger = logger;
            _localFileService = localFileService;
        }

        public async Task<string> ProduceConvo(List<string> formattedMessages)
        {
            _cts = new CancellationTokenSource();
            var llmOptions = await _localFileService.GetContent<LLMOptions>(_fileName);

            var history = string.Join("\n", formattedMessages);

            var systemPrompt =
@"Output ONLY chat lines in the format ""username: message"".
Do NOT add explanations, commentary, or code.
Look at the below conversation messages as context:
";

            var fullPrompt = $"{systemPrompt}\n\n{history}\n\n";

            var payload = new
            {
                model = "smollm:135m",
                prompt = fullPrompt,
                options = new { 
                    temperature = llmOptions.Temperature, 
                    num_predict = llmOptions.NumPredict,
                    top_p = llmOptions.TopP,
                    stop = new[] { "\n\n" } // stop after a blank line
                }
            };

            // Serialize manually (since PostAsJsonAsync isn’t available in .NET Core 3.1)
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = await _http.PostAsync("api/generate", content, _cts.Token);
            resp.EnsureSuccessStatusCode();

            var resultJson = await resp.Content.ReadAsStringAsync();
            _logger.LogInformation("Generated response: {Response}", resultJson);
            // The API returns a stream of JSON objects, one per line.
            // Split by newlines and parse each.
            var lines = resultJson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();

            foreach (var line in lines)
            {
                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(line);
                if (obj != null && obj.TryGetValue("response", out var chunk))
                {
                    sb.Append(chunk.ToString());
                }
            }

            var finalText = sb.ToString();

            // Remove any leading explanation lines that don't match "username: message"
            var finalLines = finalText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains(":")) // keep only lines with a colon (username: message)
                .ToList();

            finalText = string.Join("\n", finalLines);


            if (finalText.Length > 1000)
                finalText = finalText[..1000];

            _logger.LogInformation("Generated text: {Text}", finalText);
            return finalText;
        }

        public void Stop()
        {
            _logger.LogInformation("Cancelling response");
            _cts?.Cancel();
        }
    }
}
