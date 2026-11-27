using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    /// <summary>Faces from thispersondoesnotexist.com, for `.face` and gang cards.</summary>
    public class FaceService
    {
        private readonly ILogger<FaceService> _logger;
        private readonly HttpClient _client;

        public FaceService(ILogger<FaceService> logger, HttpClient client)
        {
            _logger = logger;
            _client = client;
        }

        /// <summary>Pause between faces in <see cref="FetchDistinct"/>: the site hands the same face to requests made at the same moment.</summary>
        public TimeSpan Spacing { get; set; } = TimeSpan.FromSeconds(1.2);
        /// <summary>Pause before fetching again when a face came back the same as one already had.</summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1.5);
        /// <summary>Tries per face before giving up on making it different.</summary>
        public const int TriesPerFace = 4;

        public async Task<Stream> Run()
        {
            //a fresh query and no-cache, so a cache between us and the site can't hand back the last face
            var requestMsg = new HttpRequestMessage(HttpMethod.Get, $"random-person.jpeg?{Guid.NewGuid():N}");
            requestMsg.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            var response = await _client.SendAsync(requestMsg);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync();
            return stream;
        }

        /// <summary>The hex SHA-256 of an image, to tell faces apart.</summary>
        public static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        /// <summary>
        /// <paramref name="count"/> faces, fetched one at a time with <see cref="Spacing"/> between them, each different from the others and from
        /// every hash in <paramref name="avoid"/> (a face that comes back the same is fetched again, up to <see cref="TriesPerFace"/> times).
        /// A face that failed or couldn't be made different is null. The hashes of the faces returned are added to <paramref name="avoid"/>.
        /// </summary>
        public async Task<List<byte[]>> FetchDistinct(int count, ISet<string> avoid)
        {
            avoid ??= new HashSet<string>();
            var faces = new List<byte[]>();
            for (int i = 0; i < count; i++)
            {
                if (i > 0 && Spacing > TimeSpan.Zero) await Task.Delay(Spacing);
                byte[] face = null;
                for (int attempt = 0; attempt < TriesPerFace && face == null; attempt++)
                {
                    if (attempt > 0 && RetryDelay > TimeSpan.Zero) await Task.Delay(RetryDelay);
                    byte[] bytes = await TryFetch();
                    if (bytes == null) continue;
                    if (avoid.Add(HashOf(bytes))) face = bytes;
                    else _logger.LogInformation("The face site sent a face we already have; fetching another.");
                }
                faces.Add(face);
            }
            return faces;
        }

        private async Task<byte[]> TryFetch()
        {
            try
            {
                using var stream = await Run();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                return buffer.Length > 0 ? buffer.ToArray() : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't fetch a face");
                return null;
            }
        }
    }
}
