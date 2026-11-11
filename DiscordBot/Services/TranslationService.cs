﻿using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    /// <summary>The mangled text and the languages it went through, in order, ending with English.</summary>
    public class TranslationResult
    {
        public string Text { get; set; }
        public List<string> Route { get; set; } = new List<string>();
    }

    /// <summary>Google refused the requests, so there's no mangled text to show.</summary>
    public class TranslationUnavailableException : Exception
    {
        public TranslationUnavailableException() : base("Google Translate isn't answering right now.") { }
    }

    /// <summary>
    /// Bounces text through a chain of languages and back to English, for .lang. The pool is languages far from English
    /// and from each other, with little training data, so meaning drifts more at every hop. Each hop translates straight
    /// from the previous language, never back through English.
    /// </summary>
    public class TranslationService
    {
        /// <summary>Google Translate codes and names. All have been on Google Translate for years.</summary>
        public static readonly IReadOnlyDictionary<string, string> Languages = new Dictionary<string, string>
        {
            ["yo"] = "Yoruba", ["zu"] = "Zulu", ["xh"] = "Xhosa", ["ig"] = "Igbo", ["ha"] = "Hausa", ["so"] = "Somali",
            ["sn"] = "Shona", ["ny"] = "Chichewa", ["st"] = "Sesotho", ["mg"] = "Malagasy", ["am"] = "Amharic",
            ["mi"] = "Maori", ["sm"] = "Samoan", ["haw"] = "Hawaiian", ["ceb"] = "Cebuano", ["hmn"] = "Hmong",
            ["km"] = "Khmer", ["lo"] = "Lao", ["my"] = "Burmese", ["mn"] = "Mongolian", ["ky"] = "Kyrgyz", ["tg"] = "Tajik",
            ["ug"] = "Uyghur", ["ps"] = "Pashto", ["si"] = "Sinhala", ["ka"] = "Georgian", ["hy"] = "Armenian",
            ["eu"] = "Basque", ["la"] = "Latin", ["yi"] = "Yiddish", ["ht"] = "Haitian Creole", ["ku"] = "Kurdish",
        };

        public const int HopCount = 8;
        /// <summary>Tries at each hop before it gives up on that language and moves to another.</summary>
        public const int AttemptsPerHop = 2;
        /// <summary>Failed requests allowed in one chain, so a Google outage can't hold the command up for long.</summary>
        public const int MaxFailures = 8;

        private readonly HttpClient _client;
        private readonly ILogger<TranslationService> _logger;

        /// <summary>
        /// Only one constructor: this is a typed HttpClient, and dependency injection refuses to start the bot when it
        /// can't pick between constructors. Tests set <see cref="Random"/> for a fixed route and zero the delays.
        /// </summary>
        public TranslationService(HttpClient client, ILogger<TranslationService> logger)
        {
            _client = client;
            _logger = logger;
        }

        public Random Random { get; set; } = new Random();
        /// <summary>Pause between hops. Google's free endpoint refuses quick bursts, so the 9 requests are spaced out.</summary>
        public TimeSpan HopDelay { get; set; } = TimeSpan.FromMilliseconds(300);
        /// <summary>Pause before trying a failed request again.</summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);

        /// <summary>
        /// Translates through <see cref="HopCount"/> different languages, then back to English. A request that fails is
        /// tried again after a pause; a language that still fails is swapped for another from the pool, so the chain keeps
        /// its full length unless Google is refusing almost everything.
        /// </summary>
        public async Task<TranslationResult> TranslateThroughLanguages(string text)
        {
            var pool = new Queue<string>(Languages.Keys.OrderBy(_ => Random.Next()));

            var result = new TranslationResult { Text = text };
            string source = "en";
            int failures = 0;
            while (result.Route.Count < HopCount && pool.Count > 0 && failures < MaxFailures)
            {
                string target = pool.Dequeue();
                string translated = null;
                for (int attempt = 0; attempt < AttemptsPerHop && translated == null && failures < MaxFailures; attempt++)
                {
                    if (result.Route.Count > 0 || attempt > 0)
                        await Task.Delay(attempt > 0 ? RetryDelay : HopDelay);
                    translated = await TryTranslate(result.Text, source, target);
                    if (translated == null) failures++;
                }
                if (translated == null)
                    continue; //the text is still in the source language; try the next language in the pool

                result.Text = translated;
                result.Route.Add(target);
                source = target;
            }

            if (result.Route.Count < HopCount)
                _logger.LogWarning("Translation chain only managed {Hops} of {HopCount} hops ({Failures} failed requests).", result.Route.Count, HopCount, failures);

            //with no hop at all the text would come back word for word, which isn't the joke
            if (result.Route.Count == 0)
                throw new TranslationUnavailableException();

            //the last hop has to work, or there's nothing readable to show
            string english = null;
            for (int attempt = 0; attempt < 3 && english == null; attempt++)
            {
                await Task.Delay(attempt > 0 ? RetryDelay : HopDelay);
                english = await TryTranslate(result.Text, source, "en");
            }
            result.Text = english ?? throw new TranslationUnavailableException();
            result.Route.Add("en");
            return result;
        }

        public static string NameOf(string code) => code == "en" ? "English" : Languages.TryGetValue(code, out string name) ? name : code;

        /// <summary>One request. Returns null and logs when it fails (refused, rate limited, or an answer that isn't a translation).</summary>
        private async Task<string> TryTranslate(string text, string from, string to)
        {
            try
            {
                return await Translate(text, from, to);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Translating {From} to {To} failed: {Error}", from, to, ex.Message);
                return null;
            }
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
            string translated = string.Concat(segments.Select(segment => (string)segment[0]));
            if (string.IsNullOrWhiteSpace(translated))
                throw new InvalidOperationException($"Empty translation from {from} to {to}.");
            return translated;
        }
    }
}
