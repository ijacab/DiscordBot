using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using MoreLinq;
using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;

namespace Common.Helpers
{
    public class GPTHelper
    {
        private const string _endOfTextStr = "<|endoftext|>";
        public static ConcurrentDictionary<int, string> ProcessFile(string fullPath)
        {
            ConcurrentDictionary<int, string> rowMessages = new ConcurrentDictionary<int, string>();
            using var sr = new StreamReader(fullPath, Encoding.UTF8);
            int i = 0;
            while (!sr.EndOfStream)
            {
                string line = sr.ReadLine();
                rowMessages.TryAdd(i, line);
                i++;
            }
            return rowMessages;
        }

        public static async Task<ConcurrentDictionary<int, string>> GetCleanEntries(ConcurrentDictionary<int, string> rowMessages)
        {
            int i = 0;
            int batchCount = 1;
            foreach (var kvpBatch in rowMessages.Batch(batchCount))
            {
                List<Task> tasks = new List<Task>();
                foreach (var kvp in kvpBatch)
                {
                    var task = Task.Run(() =>
                    {
                        if (ShouldRemove(kvp.Value))
                        {
                            rowMessages.TryRemove(kvp.Key, out var _);
                        }
                        else
                        {
                            string cleanedString = GetCleanedString(kvp.Value);
                            if (cleanedString.EndsWith(':') || string.IsNullOrWhiteSpace(cleanedString))
                                rowMessages.TryRemove(kvp.Key, out var _);
                            else
                                rowMessages[kvp.Key] = cleanedString;
                        }
                    });

                    tasks.Add(task);
                }

                await Task.WhenAll(tasks);
            }

            return rowMessages;
        }

        public static string GetCleanedString(string line)
        {
            int? index1 = null;
            while (line.Contains('<') && line.Contains('>'))
            {
                if (index1 != null
                    && line.IndexOf('<') == index1)
                    break;

                index1 = line.IndexOf('<');
                int index2 = line.IndexOf('>', index1.Value);

                if (index1 == -1 || index2 == -1)
                    break;

                var textToRemove = line.Substring(index1.Value, index2 - index1.Value + 1);
                if(textToRemove != _endOfTextStr)
                    line = line.Remove(index1.Value, index2 - index1.Value + 1);
            }
            return line;
        }

        public static bool ShouldRemove(string line)
        {
            return line.StartsWith("Jacan:")
                || line.StartsWith("Echo:")
                || line.StartsWith("PepsiDog:")
                || line.Contains("http");
            //|| !message.Contains(':');
        }
    }
}
