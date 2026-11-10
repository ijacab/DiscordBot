﻿using Newtonsoft.Json.Linq;
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
        private readonly HttpClient _client;
        private readonly Random _random;

        public TranslationService(HttpClient client) : this(client, new Random())
        {
        }

        public TranslationService(HttpClient client, Random random)
        {
            _client = client;
            _random = random;
        }

        /// <summary>
        /// Translates through <see cref="HopCount"/> different languages, then back to English. A hop that fails
        /// (a language Google drops, or a hiccup) is skipped and the chain carries on from the last language that worked.
        /// </summary>
        public async Task<TranslationResult> TranslateThroughLanguages(string text)
        {
            List<string> hops = Languages.Keys.OrderBy(_ => _random.Next()).Take(HopCount).ToList();

            var result = new TranslationResult { Text = text };
            string source = "en";
            foreach (string target in hops)
            {
                try
                {
                    result.Text = await Translate(result.Text, source, target);
                    result.Route.Add(target);
                    source = target;
                }
                catch (Exception)
                {
                    //skip this language; the text is still in the source language
                }
            }

            //the last hop has to work, or there's nothing readable to show
            result.Text = await Translate(result.Text, source, "en");
            result.Route.Add("en");
            return result;
        }

        public static string NameOf(string code) => code == "en" ? "English" : Languages.TryGetValue(code, out string name) ? name : code;

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
