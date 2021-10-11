using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DiscordBot.Models.DuckDuckGoService
{
    public class ImageSearch
    {
        [JsonPropertyName("ads")]
        public object Ads { get; set; }

        [JsonPropertyName("next")]
        public string Next { get; set; }

        [JsonPropertyName("query")]
        public string Query { get; set; }

        [JsonPropertyName("queryEncoded")]
        public string QueryEncoded { get; set; }

        [JsonPropertyName("response_type")]
        public string ResponseType { get; set; }

        [JsonPropertyName("results")]
        public List<ImageResult> ImageResults { get; set; }
    }
}
