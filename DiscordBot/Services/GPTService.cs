using Common.Helpers;
using Common.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    public class GPTService
    {
        private const string _fileName = "convos.txt";
        private const string _convoEndString = "_---------------_";
        private readonly GistService _gistService;
        private const string _endOfTextStr = "<|endoftext|>";

        public GPTService(GistService gistService)
        {
            _gistService = gistService;
        }

        public async Task<string> GetConvo(string? context = null)
        {
            //no context and convo not enabled (i.e. running on pi)
            bool runFromFile = string.IsNullOrEmpty(EnvironmentHelper.GetEnvironmentVariable("CONVO_MODEL_PATH", false));

            if (runFromFile)
            {
                string fileText = await _gistService.GetContent(_fileName);

                StringBuilder sb = new StringBuilder();
                foreach (var line in fileText.Split(new string[] { "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line == _convoEndString 
                        || sb.Length + line.Length > 1900) //discord char limit is 2000
                        break;

                    sb.AppendLine(line);
                }

                string convoText = sb.ToString();
                fileText = fileText.Substring(fileText.IndexOf(_convoEndString) + _convoEndString.Length + 1);
                //fileText = StringHelper.MakeJsonSafe(fileText);
                await _gistService.UpdateContent(_fileName, fileText);

                convoText = CleanUpOutput(convoText);
                return convoText;
            }
            else
            {
                string modelPath = EnvironmentHelper.GetEnvironmentVariable("CONVO_MODEL_PATH", true);
                string pythonFileDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_nanogpt");

                string contextArg = !string.IsNullOrWhiteSpace(context) ? $" --start=\"{context}\"" : "";
                string args = $"sample.py --out_dir={modelPath} --seed={RandomHelper.ThisThreadsRandom.Next(1, 100000)}{contextArg}";
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = pythonFileDir
                    }
                };

                process.Start();
                process.WaitForExit();
                //* Read the output (or the error)
                string output = process.StandardOutput.ReadToEnd();
                string errors = process.StandardError.ReadToEnd();
                string cutoffText = @"No meta.pkl found, assuming GPT-2 encodings...";
                output = output.Substring(output.IndexOf(cutoffText) + cutoffText.Length);
                
                output = CleanUpOutput(output);

                process.Refresh();
                process.Close();
                process.Dispose();

                return output;
            }
        }

        private string CleanUpOutput(string output)
        {
            output = output.Replace(":", ": ");
            output = output.Replace("\n\n", "\n");
            output = output.Replace("\r\n\r\n", "\r\n");
            if (output.StartsWith("\r\n"))
                output = output.Substring(2);

            output.Replace(_endOfTextStr, "");

            return output;
        }
    }
}
