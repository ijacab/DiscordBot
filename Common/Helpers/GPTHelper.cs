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

        public static ConcurrentDictionary<int,string> ProcessFile(string fullPath)
        {
            ConcurrentDictionary<int,string> rowMessages = new ConcurrentDictionary<int,string>();
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
            int batchCount = 10;
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
                            if (cleanedString.EndsWith(':'))
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

        public static string GetCleanedString(string message)
        {
            while (message.Contains('<') && message.Contains('>'))
            {
                int index1 = message.IndexOf('<');
                int index2 = message.IndexOf('>', index1);

                if (index1 == -1 || index2 == -1)
                    break;

                message = message.Remove(index1, index2 - index1 + 1);
            }
            return message;
        }

        public static bool ShouldRemove(string message)
        {
            return message.StartsWith("Jacan:")
                || message.StartsWith("Echo:")
                || message.StartsWith("PepsiDog:")
                || message.Contains("http")
                || !message.Contains(':');
        }
    }
}
