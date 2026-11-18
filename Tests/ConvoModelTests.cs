using DiscordBot.Models;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Tests
{
    /// <summary>The `.convo` Markov model and the scraped history files it loads from.</summary>
    public class ConvoModelTests
    {
        private readonly ITestOutputHelper _output;
        public ConvoModelTests(ITestOutputHelper output) => _output = output;

        private static readonly string[][] Chat =
        {
            new[] { "john123", "yo im eating" },
            new[] { "bruce77", "eating what" },
            new[] { "kris", "watermelon juicy juicy YEAHH" },
            new[] { "john123", "nah im eating pizza with pineapple" },
            new[] { "bruce77", "pineapple on pizza is a crime" },
            new[] { "kris", "crime is my favourite food" },
            new[] { "john123", "who wants to play league tonight" },
            new[] { "bruce77", "league is for losers" },
            new[] { "kris", "im a loser so im in" },
            new[] { "john123", "kris you are the worst support ever" },
            new[] { "bruce77", "the worst pizza ever was in italy" },
            new[] { "kris", "italy is just spicy france" },
            new[] { "john123", "france has the best bread" },
            new[] { "kris", "bread is just dry cake" },
            new[] { "bruce77", "yo who ate my cake" },
        };

        private static List<ConvoMessage> Messages(int repeat = 1) =>
            Enumerable.Range(0, repeat).SelectMany(r => Chat.Select((c, i) => new ConvoMessage
            {
                Id = (ulong)(r * 1000 + i + 1),
                Speaker = c[0],
                Text = c[1],
            })).ToList();

        private static ConvoModel Model() => ConvoModel.Build(new[] { Messages(3) });

        private static readonly Regex LineFormat = new Regex(@"^[^:\n]+: \S[^\n]*$");

        [Fact]
        public void EveryConvo_IsThreeToTenLinesOfUserColonSentence()
        {
            var model = Model();
            var speakers = Chat.Select(c => c[0]).ToHashSet();
            for (int seed = 0; seed < 500; seed++)
            {
                var lines = model.Generate(new Random(seed));
                Assert.InRange(lines.Count, ConvoModel.MinLines, ConvoModel.MaxLines);
                Assert.All(lines, l => Assert.Contains(l.Speaker, speakers));
                Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Text)));
                string text = ConvoModel.Format(lines);
                Assert.True(text.Length <= 2000);
                Assert.All(text.Split('\n'), line => Assert.Matches(LineFormat, line));
            }
        }

        [Fact]
        public void Convos_HaveAtLeastTwoPeopleAndVary()
        {
            var model = Model();
            var texts = new HashSet<string>();
            for (int seed = 0; seed < 50; seed++)
            {
                var lines = model.Generate(new Random(seed));
                Assert.True(lines.Select(l => l.Speaker).Distinct().Count() >= 2);
                texts.Add(ConvoModel.Format(lines));
            }
            Assert.True(texts.Count > 40);
            _output.WriteLine(string.Join("\n\n", texts.Take(5)));
        }

        [Fact]
        public void EachLine_IsBuiltFromItsSpeakersOwnWords()
        {
            //alice and bob share no words, so any line mixing them would show it
            var alice = new[] { "omg", "the", "cat", "is", "so", "fluffy", "today", "and", "sleepy", "lol" };
            var bob = new[] { "bro", "my", "car", "needs", "new", "tyres", "mate", "fuel", "costs", "heaps" };
            var random = new Random(5);
            var messages = new List<ConvoMessage>();
            for (int i = 0; i < 200; i++)
            {
                bool isAlice = i % 2 == 0;
                var words = isAlice ? alice : bob;
                messages.Add(new ConvoMessage
                {
                    Id = (ulong)(i + 1),
                    Speaker = isAlice ? "alice" : "bob",
                    Text = string.Join(" ", Enumerable.Range(0, random.Next(2, 8)).Select(_ => words[random.Next(words.Length)])),
                });
            }
            var model = ConvoModel.Build(new[] { messages });
            for (int seed = 0; seed < 300; seed++)
            {
                foreach (var line in model.Generate(new Random(seed)))
                {
                    var own = line.Speaker == "alice" ? alice : bob;
                    Assert.All(ConvoModel.Tokenize(line.Text), w => Assert.Contains(w, own));
                }
            }
        }

        [Fact]
        public void SeedWords_SteerTheFirstLineButArentShown()
        {
            var model = Model();
            int carriedOn = 0;
            for (int seed = 0; seed < 50; seed++)
            {
                var first = model.Generate(new Random(seed), "pineapple on")[0].Text;
                Assert.False(first.StartsWith("pineapple on"));
                Assert.False(string.IsNullOrWhiteSpace(first));
                //"pineapple on" is only ever followed by "pizza", so the line usually carries on from it
                if (first.StartsWith("pizza")) carriedOn++;
                //a word nobody ever said is left out too, and the line still has words
                var odd = model.Generate(new Random(seed), "zorblax")[0].Text;
                Assert.DoesNotContain("zorblax", odd);
                Assert.False(string.IsNullOrWhiteSpace(odd));
            }
            Assert.True(carriedOn > 25, $"carried on {carriedOn}/50");
        }

        [Fact]
        public void SameRandomSeed_GivesTheSameConvo()
        {
            var model = Model();
            Assert.Equal(ConvoModel.Format(model.Generate(new Random(7))), ConvoModel.Format(model.Generate(new Random(7))));
        }

        [Fact]
        public void EmptyModel_GivesNothing()
        {
            var model = ConvoModel.Build(new[] { new List<ConvoMessage>() });
            Assert.Null(model.Generate(new Random(1)));
        }

        [Fact]
        public void Usernames_AreEscapedSoMarkdownCantMangleThem()
        {
            var model = ConvoModel.Build(new[] { new List<ConvoMessage>
            {
                new ConvoMessage { Id = 1, Speaker = "big_dog_", Text = "hello there" },
                new ConvoMessage { Id = 2, Speaker = "kris", Text = "hello back" },
            } });
            string text = ConvoModel.Format(model.Generate(new Random(3)));
            Assert.DoesNotContain("big_dog_:", text.Replace("big\\_dog\\_:", ""));
        }

        [Theory]
        [InlineData("Here's how I would iterate through the conversation messages to extract the details of a message, formatted with appropriate explanation for each message, such as:")]
        [InlineData("Here is the output of the chatbot in a format called \"text message \":")]
        [InlineData("The key to a complete and accurate email address can be seen in the format `username: message`, where `username` and `message` are enclosed with angle brackets.")]
        [InlineData("username:")]
        [InlineData("a: too short a name")]
        public void KeepChatLines_ThrowsAwayProse(string text)
        {
            Assert.Empty(ConvoService.KeepChatLines(text));
        }

        [Fact]
        public void KeepChatLines_KeepsUserColonSentenceLinesOnly()
        {
            var lines = ConvoService.KeepChatLines("Here is a convo:\njohn123: yo im eating\n  bruce77: eating what  \nkris.k: watermelon juicy juicy YEAHH\nand that is the end of the chat");
            Assert.Equal(new[] { "john123: yo im eating", "bruce77: eating what", "kris.k: watermelon juicy juicy YEAHH" }, lines);
            Assert.Equal(ConvoService.MaxLines, ConvoService.KeepChatLines(string.Join("\n", Enumerable.Repeat("kris: hi", 20))).Count);
        }

        [Theory]
        [InlineData(false, true, false, false, true)]
        [InlineData(true, true, false, false, false)]
        [InlineData(false, false, false, false, false)]
        [InlineData(false, true, true, false, false)]
        [InlineData(false, true, false, true, false)]
        public void AutoScrape_StartsOnlyTheVeryFirstTime(bool stateExists, bool canSeeGuild, bool scraping, bool alreadyTried, bool expected)
        {
            Assert.Equal(expected, ConvoCorpusService.ShouldAutoScrape(stateExists, canSeeGuild, scraping, alreadyTried));
        }

        [Theory]
        [InlineData("yo im eating", "yo im eating")]
        [InlineData("look https://example.com/x lol", "look lol")]
        [InlineData("line one\nline two", "line one line two")]
        [InlineData("@everyone wake up", "wake up")]
        [InlineData(".bj 1000", null)]
        [InlineData("!play song", null)]
        [InlineData("...", "...")]
        [InlineData("```code```", null)]
        [InlineData("https://tenor.com/gif", null)]
        [InlineData("   ", null)]
        public void Clean_KeepsChatAndDropsCommandsLinksAndPings(string input, string expected)
        {
            Assert.Equal(expected, ConvoCorpusService.Clean(input));
        }

        [Fact]
        public void Clean_CutsLongMessagesAtAWord()
        {
            string text = ConvoCorpusService.Clean(string.Join(" ", Enumerable.Repeat("word", 200)));
            Assert.True(text.Length <= ConvoCorpusService.MaxMessageChars);
            Assert.EndsWith("word", text);
        }

        [Fact]
        public void SavedLines_RoundTripAndStayOnOneLine()
        {
            string line = ConvoCorpusService.ToLine(42, "kris", "tab\there and\nnewline");
            Assert.DoesNotContain("\n", line);
            var message = ConvoCorpusService.ParseLine(line);
            Assert.Equal(42UL, message.Id);
            Assert.Equal("kris", message.Speaker);
            Assert.Equal("tab here and newline", message.Text);
            Assert.Null(ConvoCorpusService.ParseLine("garbage"));
        }

        [Fact]
        public void Excluded_CoversTheChannelItsThreadsAndACategorysChannels()
        {
            ulong x = ConvoCorpusService.ExcludedChannelId;
            Assert.True(ConvoCorpusService.IsExcluded(x, null, null));
            Assert.True(ConvoCorpusService.IsExcluded(5, x, null));
            Assert.True(ConvoCorpusService.IsExcluded(5, 6, x));
            Assert.False(ConvoCorpusService.IsExcluded(5, 6, 7));
        }

        [Fact]
        public void LoadFrom_DropsRepeatsAndSortsEachChannelByTime()
        {
            string dir = Path.Combine(Path.GetTempPath(), "convo_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, ConvoCorpusService.MessagesDir));
                //pages are saved newest first, and a page can be saved twice if the bot restarts mid-scrape
                File.WriteAllLines(Path.Combine(dir, ConvoCorpusService.MessagesDir, "1.tsv"), new[]
                {
                    ConvoCorpusService.ToLine(3, "kris", "third"),
                    ConvoCorpusService.ToLine(2, "bruce77", "second"),
                    ConvoCorpusService.ToLine(3, "kris", "third"),
                    ConvoCorpusService.ToLine(1, "john123", "first"),
                    "broken line",
                });
                File.WriteAllLines(Path.Combine(dir, ConvoCorpusService.MessagesDir, "2.tsv"), new[]
                {
                    ConvoCorpusService.ToLine(10, "kris", "other channel"),
                });

                var model = ConvoCorpusService.LoadFrom(dir, 1000, new Random(1));
                Assert.Equal(4, model.MessageCount);
                Assert.Equal(3, model.SpeakerCount);

                var sampled = ConvoCorpusService.LoadFrom(dir, 2, new Random(1));
                Assert.True(sampled.MessageCount < 4);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
