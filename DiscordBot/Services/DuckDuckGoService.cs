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
    /// <summary>An image downloaded for posting as an attachment.</summary>
    public sealed class DownloadedImage
    {
        public byte[] Bytes { get; init; }
        public string FileName { get; init; }
        public ImageResult Source { get; init; }
    }

    /// <summary>
    /// Image search through DuckDuckGo's unofficial endpoint (`.img`, `.dbz`). It needs a per-query token (vqd) and browser-like
    /// headers, and the pictures it finds live on other people's servers, which often refuse to be linked from Discord. So
    /// <see cref="FetchFirstWorkingAsync"/> downloads a candidate and checks that it really is an image, and the command posts it as
    /// an attachment instead of a link.
    /// </summary>
    public class DuckDuckGoService
    {
        /// <summary>Discord's upload limit is higher, but this keeps things quick on a Pi.</summary>
        public const int MaxImageBytes = 8 * 1024 * 1024;
        /// <summary>How many search results are tried (downloaded) before giving up.</summary>
        public const int MaxCandidates = 8;

        private static readonly HttpMessageHandler _sharedHandler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, string> _vqds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _vqdLock = new object();

        public DuckDuckGoService() : this(_sharedHandler) { }

        /// <summary>For tests: use a fake handler instead of the network.</summary>
        public DuckDuckGoService(HttpMessageHandler handler)
        {
            _httpClient = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        }

        /// <summary>Pulls the search token out of DuckDuckGo's page. It has been served in several shapes over time.</summary>
        public static string ExtractVqd(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            foreach (var pattern in new[] { @"vqd=[""']([^""']+)[""']", @"""vqd""\s*:\s*""([^""]+)""", @"vqd\s*[:=]\s*[""']([^""']+)[""']", @"vqd=([\d-]+)" })
            {
                var match = Regex.Match(content, pattern);
                if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)) return match.Groups[1].Value;
            }
            return null;
        }

        private async Task<string> GetVqdAsync(string query, bool refresh)
        {
            lock (_vqdLock)
            {
                if (!refresh && _vqds.TryGetValue(query, out string cached)) return cached;
            }
            var req = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://duckduckgo.com/?q={Uri.EscapeDataString(query)}&t=h_&iar=images&iax=images&ia=images"));
            var res = await _httpClient.SendAsync(req);
            string vqd = ExtractVqd(await res.Content.ReadAsStringAsync());
            if (string.IsNullOrWhiteSpace(vqd)) throw new Exception("DuckDuckGo didn't give me a search token.");
            lock (_vqdLock) _vqds[query] = vqd;
            return vqd;
        }

        public async Task<List<ImageResult>> GetImages(string query)
        {
            var safeOff = query.ToLower().Contains("safeoff");
            query = new string(query.Replace("safeoff", "").Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                string vqd = await GetVqdAsync(query, refresh: attempt > 0);
                var querystringParams = new Dictionary<string, string>
                {
                    { "o", "json" },
                    { "q", query },
                    { "vqd", vqd },
                    { "f", ",,,,," },
                };
                if (safeOff) querystringParams.Add("p", "-1");
                var req = new HttpRequestMessage(HttpMethod.Get, new Uri(QueryHelpers.AddQueryString("https://duckduckgo.com/i.js", querystringParams)));
                req.Headers.Referrer = new Uri("https://duckduckgo.com/");
                var res = await _httpClient.SendAsync(req);

                //a stale or refused token: get a fresh one and go again
                if (res.StatusCode == HttpStatusCode.Forbidden || res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode == 418)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 + attempt));
                    continue;
                }
                if (res.StatusCode != HttpStatusCode.OK) throw new Exception($"DuckDuckGo said no ({(int)res.StatusCode}).");
                return JsonSerializer.Deserialize<ImageSearch>(await res.Content.ReadAsStringAsync())?.ImageResults ?? new List<ImageResult>();
            }
            throw new Exception("DuckDuckGo keeps refusing me right now.");
        }

        /// <summary>Results worth trying: real http(s) links, in a random order.</summary>
        public static List<ImageResult> Candidates(IEnumerable<ImageResult> results, Random random) =>
            (results ?? Enumerable.Empty<ImageResult>())
                .Where(r => r != null && (IsWeb(r.Image) || IsWeb(r.Thumbnail)))
                .OrderBy(_ => random.Next())
                .Take(MaxCandidates)
                .ToList();

        private static bool IsWeb(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        /// <summary>
        /// Downloads candidates one by one (the full image first, then its DuckDuckGo thumbnail) and returns the first that really is
        /// an image under <see cref="MaxImageBytes"/>. Null when none of them work.
        /// </summary>
        public async Task<DownloadedImage> FetchFirstWorkingAsync(IEnumerable<ImageResult> candidates)
        {
            foreach (var candidate in candidates)
            {
                foreach (string url in new[] { candidate.Image, candidate.Thumbnail })
                {
                    if (!IsWeb(url)) continue;
                    var image = await TryDownloadAsync(url, candidate);
                    if (image != null) return image;
                }
            }
            return null;
        }

        private async Task<DownloadedImage> TryDownloadAsync(string url, ImageResult source)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
                //many hosts only serve a picture to a visitor who came from their own site
                if (Uri.TryCreate(source.Url, UriKind.Absolute, out var referrer)) req.Headers.Referrer = referrer;
                using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!res.IsSuccessStatusCode) return null;
                string type = res.Content.Headers.ContentType?.MediaType ?? "";
                if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || type.Contains("svg", StringComparison.OrdinalIgnoreCase)) return null;
                if (res.Content.Headers.ContentLength > MaxImageBytes) return null;
                byte[] bytes = await res.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;
                return new DownloadedImage { Bytes = bytes, FileName = "image" + ExtensionFor(type), Source = source };
            }
            catch (Exception)
            {
                return null; //dead link, timeout, refused: try the next one
            }
        }

        private static string ExtensionFor(string mediaType)
        {
            switch (mediaType.ToLowerInvariant())
            {
                case "image/png": return ".png";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
                case "image/bmp": return ".bmp";
                default: return ".jpg";
            }
        }
    }
}
