using Common.Services;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    public class BanService
    {
        private const string _fileName = "bans.json";
        private readonly GistService _gistService;

        public BanService(GistService gistService)
        {
            _gistService = gistService;
        }

        public async Task<List<ulong>> GetAll()
        {
            return await _gistService.GetContent<List<ulong>>(_fileName);
        }

        public async Task Save(IEnumerable<ulong> bannedUserIds)
        {
            string content = JsonConvert.SerializeObject(bannedUserIds.ToList());
            await _gistService.UpdateContent(_fileName, content);
        }
    }
}
