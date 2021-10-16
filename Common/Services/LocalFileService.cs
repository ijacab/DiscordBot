using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Common.Services
{
    public class LocalFileService : IFileService
    {
        public Task UpdateContent(string fileName, string content)
        {
            string path = Path.Join(Directory.GetCurrentDirectory(), fileName);
            File.WriteAllText(path, content);
            return Task.CompletedTask;
        }

        public Task<T> GetContent<T>(string fileName) where T : new()
        {
            string path = Path.Join(Directory.GetCurrentDirectory(), fileName);
            if(!File.Exists(path))
            {
                T initializedObj = new T();
                UpdateContent(fileName, JsonConvert.SerializeObject(initializedObj));
                return Task.FromResult(initializedObj);
            }

            string fileContent = File.ReadAllText(path);
            return Task.FromResult(JsonConvert.DeserializeObject<T>(fileContent));
        }
    }
}
