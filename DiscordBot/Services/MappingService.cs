using Common.Services;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    public class MappingService
    {
        private const string _fileName = "mappings.json";
        private readonly GistService _gistService;

        public MappingService(GistService gistService)
        {
            _gistService = gistService;
        }

        public async Task<Dictionary<string, string>> GetAll()
        {
            return await _gistService.GetContent<Dictionary<string, string>>(_fileName);
        }

        public async Task Add(string key, string value)
        {
            var mappings = await GetAll();
            if (mappings.ContainsKey(key)) mappings.Remove(key);

            mappings.Add(key, value);
            string content = JsonConvert.SerializeObject(mappings);
            await _gistService.UpdateContent(_fileName, content);
        }

        public async Task Remove(string key)
        {
            var mappings = await GetAll();
            mappings.Remove(key);

            string content = JsonConvert.SerializeObject(mappings);
            await _gistService.UpdateContent(_fileName, content);
        }

        public async Task ClearAll()
        {
            var content = JsonConvert.SerializeObject(new Dictionary<string, string>());
            await _gistService.UpdateContent(_fileName, content);
        }
    }
}
