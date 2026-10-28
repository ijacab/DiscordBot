using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    public class TranslationService
    {
        // Languages the text can be bounced through before it comes back to English
        private static readonly string[] _languages = { "ja", "ru", "ar", "hi", "sw", "th", "de", "ko", "el", "tr", "vi", "pl" };
        private const int _hopCount = 6;
        private readonly HttpClient _client;
        private readonly Random _random = new Random();

        public TranslationService(HttpClient client)
        {
            _client = client;
        }

        public async Task<string> TranslateThroughLanguages(string text)
        {
            // Languages can repeat across hops, but never back to back
            var hops = new List<string>();
            string previousHop = null;
            for (int i = 0; i < _hopCount; i++)
            {
                string hop;
                do
                {
                    hop = _languages[_random.Next(_languages.Length)];
                } while (hop == previousHop);

                hops.Add(hop);
                previousHop = hop;
            }
            hops.Add("en");

            string current = text;
            string source = "en";
            foreach (string target in hops)
            {
                current = await Translate(current, source, target);
                source = target;
            }

            return current;
        }

        private async Task<string> Translate(string text, string from, string to)
        {
            // Unofficial Google Translate endpoint, no API key needed
            string url = $"translate_a/single?client=gtx&dt=t&sl={from}&tl={to}";
            var content = new FormUrlEncodedContent(new Dictionary<string, string> { { "q", text } });

            var response = await _client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            // Response shape: [[["translated part", "original part", ...], ...], ...]
            var json = JArray.Parse(await response.Content.ReadAsStringAsync());
            var segments = (JArray)json[0];
            return string.Concat(segments.Select(segment => (string)segment[0]));
        }
    }
}
