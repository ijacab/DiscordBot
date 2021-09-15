using Common.Helpers;
using Common.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Common.Services
{
    public class GistService
    {
        private readonly HttpClient _httpClient;
        private readonly GistSettings _settings;

        public GistService(HttpClient httpClient, GistSettings settings)
        {
            _httpClient = httpClient;
            _settings = settings;
        }


        public async Task UpdateContent(string fileName, string content)
        {
            string body = "{\"files\": { \"" + fileName + "\": { \"content\": \"" + content.Replace("\"", "\\\"") + "\" } } }";
            var request = new HttpRequestMessage(HttpMethod.Patch, $"https://api.github.com/gists/{_settings.Id}");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to get update gist content. Status code: {response.StatusCode}\t Error: {response.ReasonPhrase}");
            }

            return;
        }

        public async Task<T> GetContent<T>(string fileName) where T : new()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/gists/{_settings.Id}");//$"https://gist.githubusercontent.com/{_settings.UserName}/{_settings.Id}/raw/{fileName}");

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Failed to get gist content. Status code: {response.StatusCode}\t Error: {response.ReasonPhrase}");

            var responseString = await response.Content.ReadAsStringAsync();


            return await ExtractFileContent<T>(responseString, fileName);
        }

        private async Task<T> ExtractFileContent<T>(string httpReponseContent, string fileName) where T : new()
        {
            JObject contentJsonObj = JObject.Parse(httpReponseContent);

            // get JSON result objects into a list
            string result = (string)contentJsonObj["files"][fileName]["content"];

            //if the file is empty initialize it with an new instance of that class
            if (string.IsNullOrWhiteSpace(result)) 
            {
                T initializedObj = new T();
                await UpdateContent(fileName, JsonConvert.SerializeObject(initializedObj));
                return initializedObj;
            }

            // JToken.ToObject is a helper method that uses JsonSerializer internally
            var resultObj = JsonConvert.DeserializeObject<T>(result);
            return resultObj;
        }
    }
}
