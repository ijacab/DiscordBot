using Common.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Tests
{
    public class GPTHelperTests
    {
        [Fact]
        public async Task TestCleaningFile()
        {
            var dict = GPTHelper.ProcessFile(@"C:\Git\nanoGPT\data\discord-ft\input2.txt");
            dict = await GPTHelper.GetCleanEntries(dict);
            using var sw = new StreamWriter(@"C:\Git\nanoGPT\data\discord-ft\inputCleaned.txt", false, Encoding.UTF8);
            foreach(var kvp in dict)
            {
                sw.WriteLine(kvp.Value);
            }
        }
    }
}
