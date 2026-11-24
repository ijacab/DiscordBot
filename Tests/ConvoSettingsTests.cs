using DiscordBot.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Tests
{
    /// <summary>The `.convoset` knobs: parsing, limits, saving, and that the model really follows them.</summary>
    public class ConvoSettingsTests
    {
        private readonly ITestOutputHelper _output;
        public ConvoSettingsTests(ITestOutputHelper output) => _output = output;

        private static string TempFile() => Path.Combine(Path.GetTempPath(), "convoset_test_" + Guid.NewGuid().ToString("N") + ".json");

        [Fact]
        public void TheDefaults_AreTheModelsConstants()
        {
            var s = new ConvoSettings();
            Assert.Equal(ConvoModel.Chaos, s.Chaos);
            Assert.Equal(ConvoModel.Temperature, s.Temperature);
            Assert.Equal(ConvoModel.DefaultFavouredShare, s.FavouredShare);
            Assert.Equal(ConvoModel.MinLines, s.MinLines);
            Assert.Equal(ConvoModel.MaxLines, s.MaxLines);
            Assert.Equal(ConvoModel.MaxCopiedWords, s.MaxCopiedWords);
            Assert.Equal(ConvoSettings.Specs.Count, s.Describe().Count);
        }

        [Theory]
        [InlineData("chaos", "0.9", 0.9)]
        [InlineData("CHAOS", "90%", 0.9)]
        [InlineData("Temperature", "2", 2.0)]
        [InlineData("topicpull", "25%", 0.25)]
        [InlineData("favoured", "50%", 0.5)]
        public void TrySet_ReadsNumbersAndPercentages(string name, string text, double expected)
        {
            var s = new ConvoSettings();
            Assert.True(s.TrySet(name, text, out string message));
            Assert.Equal(expected, s.Get(ConvoSettings.Find(name)), 6);
            Assert.Contains("→", message);
        }

        [Theory]
        [InlineData("chaos", "1.5")]      //above 100%
        [InlineData("chaos", "-0.1")]
        [InlineData("temperature", "0")]  //below the minimum
        [InlineData("temperature", "abc")]
        [InlineData("temperature", "NaN")]
        [InlineData("maxlines", "2.5")]   //not whole
        [InlineData("maxlines", "99")]
        [InlineData("minlines", "11")]    //above maxlines (10)
        [InlineData("nonsense", "1")]
        public void TrySet_RefusesBadValues_AndChangesNothing(string name, string text)
        {
            var s = new ConvoSettings();
            Assert.False(s.TrySet(name, text, out string message));
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.Equal(ConvoModel.Chaos, s.Chaos);
            Assert.Equal(ConvoModel.Temperature, s.Temperature);
            Assert.Equal(ConvoModel.MinLines, s.MinLines);
            Assert.Equal(ConvoModel.MaxLines, s.MaxLines);
        }

        [Fact]
        public void MaxLines_CannotGoBelowMinLines()
        {
            var s = new ConvoSettings();
            Assert.True(s.TrySet("minlines", "6", out _));
            Assert.False(s.TrySet("maxlines", "5", out _));
            Assert.True(s.TrySet("maxlines", "6", out _));
            Assert.Equal((6, 6), (s.MinLines, s.MaxLines));
        }

        [Fact]
        public void Reset_PutsOneOrAllBack()
        {
            var s = new ConvoSettings();
            s.TrySet("chaos", "0.1", out _);
            s.TrySet("temperature", "3", out _);
            Assert.NotNull(s.Reset("chaos"));
            Assert.Equal(ConvoModel.Chaos, s.Chaos);
            Assert.Equal(3.0, s.Temperature);
            Assert.Null(s.Reset("nonsense"));
            s.Reset(null);
            Assert.Equal(ConvoModel.Temperature, s.Temperature);
        }

        [Fact]
        public void Settings_SurviveASaveAndLoad_AndABadFileFallsBackToDefaults()
        {
            string path = TempFile();
            try
            {
                var s = new ConvoSettings();
                s.TrySet("chaos", "0.4", out _);
                s.TrySet("minlines", "4", out _);
                s.Save(path);
                var again = ConvoSettings.Load(path);
                Assert.Equal(0.4, again.Chaos, 6);
                Assert.Equal(4, again.MinLines);

                File.WriteAllText(path, "{ not json");
                Assert.Equal(ConvoModel.Chaos, ConvoSettings.Load(path).Chaos);
                Assert.Equal(ConvoModel.Chaos, ConvoSettings.Load(path + ".missing").Chaos);

                //a hand-edited value out of range is put back to its default, the rest is kept
                File.WriteAllText(path, "{\"Chaos\": 7, \"Temperature\": 2}");
                var fixedUp = ConvoSettings.Load(path);
                Assert.Equal(ConvoModel.Chaos, fixedUp.Chaos);
                Assert.Equal(2.0, fixedUp.Temperature);
            }
            finally { File.Delete(path); File.Delete(path + ".tmp"); }
        }

        private static ConvoModel RareWordModel()
        {
            //after "go", "common" was said 19 times and "rare" once: 5% of the time for real
            var messages = new List<ConvoMessage>();
            ulong id = 1;
            foreach (var who in new[] { "ann", "ben" })
                for (int i = 0; i < 20; i++)
                    messages.Add(new ConvoMessage { Id = id++, Speaker = who, Text = i == 0 ? "go rare" : "go common" });
            return ConvoModel.Build(new[] { (IReadOnlyList<ConvoMessage>)messages });
        }

        private static double RareShare(ConvoModel model)
        {
            int rare = 0, common = 0;
            for (int seed = 0; seed < 2000; seed++)
                foreach (var line in model.Generate(new Random(seed)))
                    foreach (var word in line.Text.Split(' '))
                    {
                        if (word == "rare") rare++;
                        else if (word == "common") common++;
                    }
            return rare / (double)(rare + common);
        }

        [Fact]
        public void TheModelFollowsTheTemperatureItIsGiven_Live()
        {
            var model = RareWordModel();
            model.Settings.Temperature = 1.0;
            double plain = RareShare(model);
            model.Settings.Temperature = 3.0;
            double hot = RareShare(model);
            _output.WriteLine($"rare share: temperature 1 = {plain:P1}, temperature 3 = {hot:P1}");
            Assert.InRange(plain, 0.02, 0.08);  //as said: about 5%
            Assert.True(hot > plain * 2, "a hotter model should pick the rare word far more often");
        }

        [Fact]
        public void TheModelFollowsTheLineCountItIsGiven_Live()
        {
            var model = RareWordModel();
            model.Settings.MinLines = model.Settings.MaxLines = 5;
            for (int seed = 0; seed < 100; seed++)
                Assert.Equal(5, model.Generate(new Random(seed)).Count);
            model.Settings.MinLines = 3;
            model.Settings.MaxLines = 3;
            Assert.Equal(3, model.Generate(new Random(1)).Count);
        }
    }
}
