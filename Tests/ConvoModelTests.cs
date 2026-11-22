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
        public void Temperature_MakesRareFollowUpsLikelierThanTheyReallyWere()
        {
            //after "go", "common" was said 19 times and "rare" once: 5% of the time for real
            var messages = new List<ConvoMessage>();
            ulong id = 1;
            foreach (var who in new[] { "ann", "ben" })
                for (int i = 0; i < 20; i++)
                    messages.Add(new ConvoMessage { Id = id++, Speaker = who, Text = i == 0 ? "go rare" : "go common" });
            var model = ConvoModel.Build(new[] { messages });

            int rare = 0, common = 0;
            for (int seed = 0; seed < 3000; seed++)
                foreach (var line in model.Generate(new Random(seed)))
                    foreach (var word in line.Text.Split(' '))
                    {
                        if (word == "rare") rare++;
                        else if (word == "common") common++;
                    }
            double share = rare / (double)(rare + common);
            _output.WriteLine($"rare share {share:P1} (really 5%)");
            Assert.InRange(share, 0.08, 0.40);
        }

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

        private static List<ConvoMessage> ChannelOf(int count, ulong firstId, bool favoured, Func<int, (string Speaker, string Text)> make) =>
            Enumerable.Range(0, count).Select(i =>
            {
                var (speaker, text) = make(i);
                return new ConvoMessage { Id = firstId + (ulong)i, Speaker = speaker, Text = text, Favoured = favoured };
            }).ToList();

        private double FavouredSpeakerShare(int favouredCount, int plainCount)
        {
            var favoured = ChannelOf(favouredCount, 1, true, i => (i % 2 == 0 ? "fav1" : "fav2", "hello there friend"));
            var plain = ChannelOf(plainCount, 100_000, false, i => (i % 2 == 0 ? "plain1" : "plain2", "hello there friend"));
            var model = ConvoModel.Build(new[] { favoured, plain });
            int fromFavoured = 0, convos = 2000;
            for (int seed = 0; seed < convos; seed++)
                if (model.Generate(new Random(seed))[0].Speaker.StartsWith("fav")) fromFavoured++;
            double share = (double)fromFavoured / convos;
            _output.WriteLine($"favoured share {share:P1} ({favouredCount} vs {plainCount} messages)");
            return share;
        }

        [Fact]
        public void FavouredChannels_GiveAbout95PercentOfWhoTalks()
        {
            Assert.InRange(FavouredSpeakerShare(300, 300), 0.92, 0.98);
        }

        [Fact]
        public void FavouredChannels_KeepTheirShareEvenWhenMuchSmaller()
        {
            Assert.InRange(FavouredSpeakerShare(100, 1000), 0.92, 0.98);
        }

        [Fact]
        public void FavouredChannels_GiveAbout95PercentOfWordChoices()
        {
            //one person says "go x" in a favoured channel and "go y" in another, equally often
            var favoured = ChannelOf(30, 1, true, i => ("solo", "go x"));
            var plain = ChannelOf(30, 10_000, false, i => ("solo", "go y"));
            var model = ConvoModel.Build(new[] { favoured, plain });
            int x = 0, y = 0;
            for (int seed = 0; seed < 2000; seed++)
            {
                //"go" is the topic, so the first line is "go" and then the word picked after it
                var words = ConvoModel.Tokenize(model.Generate(new Random(seed), "go")[0].Text);
                if (words.Contains("x")) x++;
                else if (words.Contains("y")) y++;
            }
            double share = (double)x / (x + y);
            _output.WriteLine($"x share {share:P1} of {x + y}");
            Assert.InRange(share, 0.92, 0.98);
        }

        [Fact]
        public void LoadFrom_MarksTheFavouredChannelsAndKeepsThemWholeUnderTheCap()
        {
            string dir = Path.Combine(Path.GetTempPath(), "convo_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, ConvoCorpusService.MessagesDir));
                ulong favouredId = ConvoCorpusService.FavouredChannelIds.First();
                Assert.True(ConvoCorpusService.IsFavoured($"{favouredId}.tsv"));
                Assert.False(ConvoCorpusService.IsFavoured("12345.tsv"));
                File.WriteAllLines(Path.Combine(dir, ConvoCorpusService.MessagesDir, $"{favouredId}.tsv"),
                    Enumerable.Range(1, 10).Select(i => ConvoCorpusService.ToLine((ulong)i, "kris", "favoured words")));
                File.WriteAllLines(Path.Combine(dir, ConvoCorpusService.MessagesDir, "12345.tsv"),
                    Enumerable.Range(100, 100).Select(i => ConvoCorpusService.ToLine((ulong)i, "john123", "plain words")));

                Assert.Equal(110, ConvoCorpusService.LoadFrom(dir, 1000, new Random(1)).MessageCount);
                //over the cap: all 10 favoured messages stay and the rest fill the other 10 places (about)
                Assert.InRange(ConvoCorpusService.LoadFrom(dir, 20, new Random(1)).MessageCount, 12, 30);
                //a cap the favoured channel fills on its own leaves no room for the rest
                var favouredOnly = ConvoCorpusService.LoadFrom(dir, 10, new Random(1));
                Assert.Equal(10, favouredOnly.MessageCount);
                Assert.All(favouredOnly.Generate(new Random(1)), l => Assert.Equal("kris", l.Speaker));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void LongLines_AreRarelyWordForWordCopiesOfRealMessages()
        {
            var model = Model();
            int lines = 0, copies = 0;
            for (int seed = 0; seed < 300; seed++)
            {
                foreach (var line in model.Generate(new Random(seed)))
                {
                    if (ConvoModel.Tokenize(line.Text).Length <= ConvoModel.MaxCopiedWords) continue;
                    lines++;
                    if (model.IsCopy(line.Text)) copies++;
                }
            }
            _output.WriteLine($"{copies} copies in {lines} longer lines");
            //this tiny corpus has few other ways to say things, so a copy is only kept after six tries
            Assert.True(copies < lines / 10, $"{copies} copies in {lines} lines");
            Assert.True(model.IsCopy("pineapple on pizza is a crime"));
            Assert.False(model.IsCopy("eating what"));
        }

        private static double TopicShare(ConvoModel model, string seed, string word, Action<List<ConvoLine>> check = null)
        {
            int lines = 0, about = 0;
            for (int i = 0; i < 300; i++)
            {
                var convo = model.Generate(new Random(i), seed);
                check?.Invoke(convo);
                lines += convo.Count;
                about += convo.Count(l => l.Text.Contains(word, StringComparison.OrdinalIgnoreCase));
            }
            return (double)about / lines;
        }

        [Fact]
        public void SeedWords_AreTheTopicOfTheWholeConvo()
        {
            var model = Model();
            double without = TopicShare(model, null, "pizza");
            //the first line always brings the topic in, and most of the rest mention it too
            double with = TopicShare(model, "pizza", "pizza", convo => Assert.Contains("pizza", convo[0].Text));
            _output.WriteLine($"lines mentioning pizza: {without:P0} without a topic, {with:P0} with");
            Assert.True(with > 0.6, $"{with:P0}");
            Assert.True(with > without * 2);
        }

        [Fact]
        public void SeedWords_SkipFillerAndIgnoreWordsNobodyUsed()
        {
            var model = Model();
            //"the" is filler, so "the cake" is about cake
            Assert.True(TopicShare(model, "the cake", "cake") > 0.6);
            //a word nobody ever said is ignored and never shown, and the convo is still made
            TopicShare(model, "zorblax", "zorblax", convo =>
            {
                Assert.InRange(convo.Count, ConvoModel.MinLines, ConvoModel.MaxLines);
                Assert.All(convo, l => Assert.DoesNotContain("zorblax", l.Text));
            });
        }

        [Fact]
        public void TopicConvos_StillFollowTheFormatAndEachSpeakersOwnWords()
        {
            var model = Model();
            TopicShare(model, "league", "league", convo =>
                Assert.All(ConvoModel.Format(convo).Split('\n'), line => Assert.Matches(LineFormat, line)));
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
        //stateExists, unfinished, canSeeGuild, scraping, alreadyTried
        [InlineData(false, false, true, false, false, true)]   //never scraped: start
        [InlineData(true, true, true, false, false, true)]     //cut off by a restart: resume
        [InlineData(true, false, true, false, false, false)]   //everything saved: only `.scrapeconvo` refreshes
        [InlineData(false, false, false, false, false, false)] //can't see the server yet
        [InlineData(true, true, true, true, false, false)]     //already running
        [InlineData(true, true, true, false, true, false)]     //already tried this process
        public void AutoScrape_StartsFirstTimeAndResumesAfterARestart(bool stateExists, bool unfinished, bool canSeeGuild, bool scraping, bool alreadyTried, bool expected)
        {
            Assert.Equal(expected, ConvoCorpusService.ShouldAutoScrape(stateExists, unfinished, canSeeGuild, scraping, alreadyTried));
        }

        [Fact]
        public void UnfinishedChannels_IgnoreOnesThatAreDoneOrOutOfReach()
        {
            var done = new ConvoCorpusService.ChannelProgress { Done = true };
            var locked = new ConvoCorpusService.ChannelProgress { NoAccess = true };
            var partial = new ConvoCorpusService.ChannelProgress();
            Assert.False(ConvoCorpusService.HasUnfinishedChannels(new[] { done, locked }));
            Assert.True(ConvoCorpusService.HasUnfinishedChannels(new[] { done, partial }));
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
        public void LoadReport_SaysHowManyMessagesWereReadAndHowOldTheyAre()
        {
            string dir = Path.Combine(Path.GetTempPath(), "convo_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, ConvoCorpusService.MessagesDir));
                //snowflake ids: milliseconds since 2015 shifted left 22 bits
                ulong IdAt(DateTime t) => (ulong)(t - new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds << 22;
                var now = DateTime.UtcNow;
                var lines = new List<string>();
                for (int i = 0; i < 30; i++) lines.Add(ConvoCorpusService.ToLine(IdAt(now.AddDays(-2).AddSeconds(i)), "kris", "recent words"));
                for (int i = 0; i < 70; i++) lines.Add(ConvoCorpusService.ToLine(IdAt(now.AddDays(-1000).AddSeconds(i)), "john123", "old words"));
                File.WriteAllLines(Path.Combine(dir, ConvoCorpusService.MessagesDir, "12345.tsv"), lines);

                var report = new ConvoCorpusService.LoadReport();
                var model = ConvoCorpusService.LoadFrom(dir, 1000, new Random(1), report);

                Assert.Equal(100, model.MessageCount);
                Assert.Equal(100, report.Messages);
                Assert.Equal(100, report.OnDisk);
                Assert.Equal(1, report.Files);
                Assert.Equal(0, report.SampleFiles);
                Assert.True(report.KeptEverything);
                Assert.Equal(0.30, report.ShareLast30Days, 2);
                Assert.Equal(0.30, report.ShareLastYear, 2);
                Assert.True(report.Oldest < now.AddDays(-900));
                Assert.Contains("100 of 100", report.ToString());
                Assert.Contains("30%", report.ToString());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
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

        private static string TempCorpus(out string messages, out string samples)
        {
            string dir = Path.Combine(Path.GetTempPath(), "convo_test_" + Guid.NewGuid().ToString("N"));
            messages = Path.Combine(dir, ConvoCorpusService.MessagesDir);
            samples = Path.Combine(dir, ConvoCorpusService.SamplesDir);
            Directory.CreateDirectory(messages);
            Directory.CreateDirectory(samples);
            return dir;
        }

        private static void WriteState(string dir, params (ulong Id, bool Done)[] channels) =>
            File.WriteAllText(Path.Combine(dir, ConvoCorpusService.StateFile),
                Newtonsoft.Json.JsonConvert.SerializeObject(channels.ToDictionary(c => c.Id, c => new ConvoCorpusService.ChannelProgress { Done = c.Done })));

        [Fact]
        public void AChannelStillBeingScraped_UsesItsRandomSample_NotItsNewestTail()
        {
            string dir = TempCorpus(out string messages, out string samples);
            try
            {
                //the scrape reads newest first, so the partial file holds only the newest messages (ids 900-999)...
                File.WriteAllLines(Path.Combine(messages, "7.tsv"), Enumerable.Range(900, 100).Select(i => ConvoCorpusService.ToLine((ulong)i, "recent", "the latest chatter")));
                //...while the sample was taken from across the whole history
                File.WriteAllLines(Path.Combine(samples, "7.tsv"), Enumerable.Range(1, 5).Select(i => ConvoCorpusService.ToLine((ulong)(i * 200), "old", "from way back")));
                WriteState(dir, (7, false));

                var model = ConvoCorpusService.LoadFrom(dir, 1000, new Random(1));
                Assert.Equal(5, model.MessageCount);
                Assert.All(model.Generate(new Random(1)), l => Assert.Equal("old", l.Speaker));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void AFinishedChannel_UsesAllItsMessages_AndIgnoresItsSample()
        {
            string dir = TempCorpus(out string messages, out string samples);
            try
            {
                File.WriteAllLines(Path.Combine(messages, "7.tsv"), Enumerable.Range(1, 100).Select(i => ConvoCorpusService.ToLine((ulong)i, "everyone", "all of history")));
                File.WriteAllLines(Path.Combine(samples, "7.tsv"), Enumerable.Range(1, 5).Select(i => ConvoCorpusService.ToLine((ulong)i, "everyone", "all of history")));
                WriteState(dir, (7, true));
                Assert.Equal(100, ConvoCorpusService.LoadFrom(dir, 1000, new Random(1)).MessageCount);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ASampleOnlyChannel_IsUsed_AndAPartialChannelWithNoSampleYetFallsBackToItsFile()
        {
            string dir = TempCorpus(out string messages, out string samples);
            try
            {
                File.WriteAllLines(Path.Combine(samples, "8.tsv"), new[] { ConvoCorpusService.ToLine(5, "kris", "sampled only") });  //not started yet: no full file
                File.WriteAllLines(Path.Combine(messages, "9.tsv"), new[] { ConvoCorpusService.ToLine(6, "bruce77", "from an older scrape") }); //partial, never sampled
                WriteState(dir, (8, false), (9, false));
                var files = ConvoCorpusService.SourceFiles(dir).Select(f => Path.GetFileName(Path.GetDirectoryName(f)) + "/" + Path.GetFileName(f)).ToArray();
                Assert.Equal(new[] { "samples/8.tsv", "messages/9.tsv" }, files);
                Assert.Equal(2, ConvoCorpusService.LoadFrom(dir, 1000, new Random(1)).MessageCount);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void RandomMessageIds_SpreadAcrossTheWholeHistory_NotTheNewestEnd()
        {
            var random = new Random(3);
            const ulong low = 1_000_000, high = 2_000_000;
            var ids = Enumerable.Range(0, 20_000).Select(_ => ConvoCorpusService.RandomMessageId(low, high, random)).ToList();
            Assert.All(ids, id => Assert.InRange(id, low, high));
            for (int tenth = 0; tenth < 10; tenth++)
            {
                int inTenth = ids.Count(id => id >= low + (ulong)tenth * 100_000 && id < low + (ulong)(tenth + 1) * 100_000);
                Assert.InRange(inTenth, 1_800, 2_200); //each tenth of the history gets about a tenth of the windows
            }
            Assert.Equal(low, ConvoCorpusService.RandomMessageId(low, low, random));
        }
    }
}
