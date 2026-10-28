using Microsoft.AspNetCore.WebUtilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DiscordBot.Models.DuckDuckGoService;

namespace DiscordBot.Services
{
    public class DuckDuckGoService
    {
        private static HttpClient _httpClient = new HttpClient();

        public string VQD { get; private set; }

        public DuckDuckGoService() { }

        /// <summary>
        /// Used to get the VQD value for searching.
        /// The initial value MUST match the image query you are searching for.
        /// Ex: If you are searching for "Dragon Ball Z", that query must be sent over during initialization.
        /// </summary>
        /// <param name="query"></param>
        /// <returns></returns>
        public async Task InitializeAsync(string query)
        {
            //using "!" in a search query causes a redirect to happen,
            //so we need to catch that earlier so initialization is able to occur normally

            var req = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri($"https://duckduckgo.com/?q={query.Replace(' ', '+')}&t=h_"),
            };

            var res = await _httpClient.SendAsync(req);
            var content = await res.Content.ReadAsStringAsync();
            //DDG has served the value with both single and double quotes (vqd='...' / vqd="..."), so accept either
            var vqd = Regex.Match(content, @"vqd=[""']([^""']+)[""']").Groups[1].Value;

            if (string.IsNullOrWhiteSpace(vqd)) throw new Exception("Could not find a value for 'vqd'");

            VQD = vqd;
        }

        public async Task<List<ImageResult>> GetImages(string query)
        {
            var safeOff = query.ToLower().Contains("safeoff");
            query = new string(query.Replace("safeoff", "").Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();

            if (string.IsNullOrWhiteSpace(VQD)) await InitializeAsync(query);

            var retryCount = 5;
            do
            {
                var querystringParams = new Dictionary<string, string>
                {
                    { "o", "json" },
                    { "q", query },
                    { "vqd", VQD },
                    { "f", ",,,,," },
                };
                if (safeOff) querystringParams.Add("p", "-1");
                var url = QueryHelpers.AddQueryString("https://duckduckgo.com/i.js", querystringParams);
                var req = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri(url),
                };
                req.Headers.Add("Host", "duckduckgo.com");
                var res = await _httpClient.SendAsync(req);
                var c = await res.Content.ReadAsStringAsync();
                if (res.StatusCode == HttpStatusCode.Forbidden) await InitializeAsync(query);
                else if (res.StatusCode != HttpStatusCode.OK) throw new Exception($"Request returned error code '{res.StatusCode}'");
                else return JsonSerializer.Deserialize<ImageSearch>(await res.Content.ReadAsStringAsync())?.ImageResults;
            } while (--retryCount > 1);

            return new List<ImageResult>();
        }
    }
}
