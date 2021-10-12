using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Common.Services
{
    public interface IFileService
    {

        public Task UpdateContent(string fileName, string content);
        public Task<T> GetContent<T>(string fileName) where T : new();
    }
}
