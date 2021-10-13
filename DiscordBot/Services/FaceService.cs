using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    public class FaceService
    {
        private readonly ILogger<FaceService> _logger;
        private readonly HttpClient _client;


        public FaceService(ILogger<FaceService> logger, HttpClient client)
        {
            _logger = logger;
            _client = client;
        }

        public async Task<Stream> Run()
        {
            var requestMsg = new HttpRequestMessage(HttpMethod.Get, "image");
            var response = await _client.SendAsync(requestMsg);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync();
            return stream;
        }
    }
}
