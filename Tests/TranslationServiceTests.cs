using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Xunit;

namespace Tests
{
    public class TranslationServiceTests
    {
        /// <summary>Answers like the translate endpoint, tagging the text with each hop, and records every (from, to) asked for.</summary>
        private sealed class FakeTranslator : HttpMessageHandler
        {
            public readonly List<(string From, string To)> Calls = new List<(string, string)>();
            public Func<string, bool> Fails = _ => false;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
                string from = query["sl"], to = query["tl"];
                Calls.Add((from, to));
                if (Fails(to))
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);

                string text = HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync())["q"];
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(new object[] { new object[] { new object[] { $"{text}>{to}", text } } });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
        }

        private static (TranslationService Service, FakeTranslator Fake) Create(int seed = 1)
        {
            var fake = new FakeTranslator();
            var client = new HttpClient(fake) { BaseAddress = new Uri("https://translate.example/") };
            return (new TranslationService(client) { Random = new Random(seed) }, fake);
        }

        [Fact]
        public async Task Route_IsEightDifferentFarFlungLanguagesThenEnglish()
        {
            var (service, _) = Create();

            TranslationResult result = await service.TranslateThroughLanguages("hello");

            Assert.Equal(TranslationService.HopCount + 1, result.Route.Count);
            Assert.Equal("en", result.Route.Last());
            var hops = result.Route.Take(TranslationService.HopCount).ToList();
            Assert.Equal(hops.Count, hops.Distinct().Count());
            Assert.All(hops, code => Assert.True(TranslationService.Languages.ContainsKey(code)));
        }

        [Fact]
        public async Task EachHop_TranslatesFromThePreviousLanguage()
        {
            var (service, fake) = Create();

            TranslationResult result = await service.TranslateThroughLanguages("hello");

            Assert.Equal("en", fake.Calls[0].From);
            for (int i = 1; i < fake.Calls.Count; i++)
                Assert.Equal(fake.Calls[i - 1].To, fake.Calls[i].From);
            Assert.Equal("hello>" + string.Join(">", result.Route), result.Text);
        }

        [Fact]
        public async Task AFailedHop_IsSkippedAndTheChainCarriesOn()
        {
            var (service, fake) = Create();
            string broken = null;
            fake.Fails = to => to != "en" && (broken ??= to) == to;

            TranslationResult result = await service.TranslateThroughLanguages("hello");

            Assert.DoesNotContain(broken, result.Route);
            Assert.Equal(TranslationService.HopCount, result.Route.Count);
            int skipped = fake.Calls.FindIndex(c => c.To == broken);
            Assert.Equal(fake.Calls[skipped].From, fake.Calls[skipped + 1].From);
        }

        [Fact]
        public async Task Routes_VaryBetweenCalls()
        {
            var (service, _) = Create();

            var first = (await service.TranslateThroughLanguages("hi")).Route;
            var second = (await service.TranslateThroughLanguages("hi")).Route;

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void EveryLanguage_HasAName()
        {
            Assert.All(TranslationService.Languages, l => Assert.False(string.IsNullOrWhiteSpace(l.Value)));
            Assert.Equal("English", TranslationService.NameOf("en"));
            Assert.Equal("Yoruba", TranslationService.NameOf("yo"));
        }
    }
}
