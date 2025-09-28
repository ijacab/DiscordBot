using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordBot.Models.ConvoService
{
    public class LLMOptions
    {
        public double Temperature { get; set; } = 0.7; // higher = more creative/wacky
        public double TopP { get; set; } = 0.85; // sample from wider distribution
        public int NumPredict { get; set; } = 150; // enough tokens for ~10 lines
    }
}
