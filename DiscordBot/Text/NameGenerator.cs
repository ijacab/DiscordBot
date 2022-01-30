using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Common.Helpers;

namespace DiscordBot.Text
{
    public static class NameGenerator
    {
        public static string GetGeneratedName(int numberOfAdjectives)
        {
            string output = "";
            var adjectives = File.ReadAllLines("Text/adjectives.txt");
            var names = File.ReadAllLines("Text/names.txt");

            var rand = new Random();
            for (int i = 0; i < numberOfAdjectives; i++)
            {
                output += $"{adjectives[rand.Next(0, adjectives.Length)]} ";
            }

            output += names[rand.Next(0, names.Length)];
            output = output.ToTitleCase();
            return output;
        }
    }
}
