using AngleSharp;
using Microsoft.AspNetCore.WebUtilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
            var req = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri($"https://duckduckgo.com/?q={query.Replace(' ', '+')}&t=h_"),
            };

            var res = await _httpClient.SendAsync(req);
            var content = await res.Content.ReadAsStringAsync();
            var config = Configuration.Default.WithDefaultLoader();
            var browsingContext = BrowsingContext.New(config);
            var document = await browsingContext.OpenAsync(r => r.Content(content));
            var vqdScript = document?.Head.GetElementsByTagName("script").Where(x => x.TextContent.Contains("vqd=")).SingleOrDefault();
            var vqdScriptContent = vqdScript?.TextContent;
            var vqdStart = vqdScriptContent?.Substring(vqdScriptContent.IndexOf("vqd="));
            var remainingVqdScriptContentArray = vqdStart?.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var vqdDict = remainingVqdScriptContentArray?.Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);

            var vqd = vqdDict.TryGetValue("vqd", out var v) ? new string(v.Where(c => c != '\'').ToArray()) : null;

            if (string.IsNullOrWhiteSpace(vqd)) throw new Exception("Could not find a value for 'vqd'");

            VQD = vqd;
        }

        public async Task<List<ImageResult>> GetImages(string query)
        {
            try
            {
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
            }
            catch (Exception ex)
            {
                throw new Exception(ex.ToString());
            }
            return new List<ImageResult>();
        }
    }
}