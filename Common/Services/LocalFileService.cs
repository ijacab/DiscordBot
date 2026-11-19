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
        private static readonly object _writeLock = new object();

        /// <summary>
        /// Writes the whole file. Atomic: it goes to a temp file first and then replaces the old one, so a crash mid-write
        /// never leaves a half-written save, and writers take turns.
        /// </summary>
        public Task UpdateContent(string fileName, string content)
        {
            string path = Path.Join(Directory.GetCurrentDirectory(), fileName);
            lock (_writeLock)
            {
                string temp = path + ".tmp";
                File.WriteAllText(temp, content);
                File.Move(temp, path, overwrite: true);
            }
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
