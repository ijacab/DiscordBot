using Common.Services;
using DiscordBot.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    public class ReminderService
    {
        private static string _fileName = "reminders.json";
        private readonly GistService _gistService;

        public ReminderService(GistService gistService)
        {
            _gistService = gistService;
        }
        public async Task<List<Reminder>> GetAll()
        {
            return await _gistService.GetContent<List<Reminder>>(_fileName);
        }

        public async Task Add(string authorMention, string message, DateTimeOffset timeToRemind, ulong channelId)
        {
            var reminders = await GetAll();
            reminders.Add(new Reminder()
            { 
                Id = Guid.NewGuid(),
                AuthorMention = authorMention, 
                Message = message, 
                TimeToRemind = timeToRemind,
                ChannelId = channelId
            });

            string content = JsonConvert.SerializeObject(reminders);
            await _gistService.UpdateContent(_fileName, content);
        }

        public async Task Remove(Guid idToRemove)
        {
            var reminders = await GetAll();
            reminders.RemoveAll(r => r.Id.CompareTo(idToRemove) == 0);

            string content = JsonConvert.SerializeObject(reminders);
            await _gistService.UpdateContent(_fileName, content);
        }

        public async Task ClearAll()
        {
            string content = JsonConvert.SerializeObject(new List<Reminder>());
            await _gistService.UpdateContent(_fileName, content);
        }
    }
}
