using DiscordBot.Models.DuckDuckGoService;
using DiscordBot.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Tests
{
    public class DuckDuckGoServiceTests
    {
        private sealed class FakeWeb : HttpMessageHandler
        {
            public List<string> Requested { get; } = new List<string>();
            public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new Dictionary<string, Func<HttpResponseMessage>>();
            public Func<Uri, HttpResponseMessage> Fallback { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requested.Add(request.RequestUri.AbsoluteUri);
                if (Routes.TryGetValue(request.RequestUri.AbsoluteUri, out var make)) return Task.FromResult(make());
                return Task.FromResult(Fallback?.Invoke(request.RequestUri) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private static HttpResponseMessage Image(string type, int size = 100)
        {
            var content = new ByteArrayContent(new byte[size]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private static HttpResponseMessage Page(string html) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };

        [Theory]
        [InlineData("<script>vqd=\"4-123456789\";</script>", "4-123456789")]
        [InlineData("x vqd='4-abc-def' y", "4-abc-def")]
        [InlineData("{\"vqd\":\"4-999\"}", "4-999")]
        [InlineData("https://duckduckgo.com/i.js?q=x&vqd=4-555-1&f=", "4-555-1")]
        public void Vqd_IsFoundInEveryShapeItHasBeenServed(string content, string expected) =>
            Assert.Equal(expected, DuckDuckGoService.ExtractVqd(content));

        [Fact]
        public void Vqd_IsNullWhenThereIsNone()
        {
            Assert.Null(DuckDuckGoService.ExtractVqd("<html>nothing here</html>"));
            Assert.Null(DuckDuckGoService.ExtractVqd(null));
        }

        [Fact]
        public void Candidates_DropJunkAndAreCapped()
        {
            var results = Enumerable.Range(0, 30).Select(i => new ImageResult { Image = $"https://example.com/{i}.jpg" })
                .Concat(new[] { new ImageResult { Image = "ftp://nope/1.jpg" }, new ImageResult { Image = "javascript:alert(1)" }, null })
                .ToList();
            var picked = DuckDuckGoService.Candidates(results, new Random(1));
            Assert.Equal(DuckDuckGoService.MaxCandidates, picked.Count);
            Assert.All(picked, p => Assert.StartsWith("https://", p.Image));
            Assert.Empty(DuckDuckGoService.Candidates(null, new Random(1)));
        }

        [Fact]
        public async Task Fetch_SkipsDeadLinksPagesAndHugeFiles_AndReturnsTheFirstRealImage()
        {
            var web = new FakeWeb();
            web.Routes["https://a.example/dead.jpg"] = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
            web.Routes["https://b.example/page.jpg"] = () => Page("<html>not an image</html>");
            web.Routes["https://c.example/huge.png"] = () => Image("image/png", DuckDuckGoService.MaxImageBytes + 1);
            web.Routes["https://d.example/goku.png"] = () => Image("image/png", 500);
            var service = new DuckDuckGoService(web);

            var image = await service.FetchFirstWorkingAsync(new[]
            {
                new ImageResult { Image = "https://a.example/dead.jpg" },
                new ImageResult { Image = "https://b.example/page.jpg" },
                new ImageResult { Image = "https://c.example/huge.png" },
                new ImageResult { Image = "https://d.example/goku.png" },
                new ImageResult { Image = "https://e.example/never-reached.jpg" },
            });

            Assert.NotNull(image);
            Assert.Equal("image.png", image.FileName);
            Assert.Equal(500, image.Bytes.Length);
            Assert.DoesNotContain("https://e.example/never-reached.jpg", web.Requested);
        }

        [Fact]
        public async Task Fetch_FallsBackToTheThumbnailWhenTheOriginalIsBlocked()
        {
            var web = new FakeWeb();
            web.Routes["https://blocked.example/full.jpg"] = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
            web.Routes["https://external-content.duckduckgo.com/thumb.jpg"] = () => Image("image/jpeg", 50);
            var image = await new DuckDuckGoService(web).FetchFirstWorkingAsync(new[]
            {
                new ImageResult { Image = "https://blocked.example/full.jpg", Thumbnail = "https://external-content.duckduckgo.com/thumb.jpg" },
            });
            Assert.NotNull(image);
            Assert.Equal("image.jpg", image.FileName);
        }

        [Fact]
        public async Task Fetch_ReturnsNullWhenNothingWorks_AndRefusesSvgs()
        {
            var web = new FakeWeb();
            web.Routes["https://x.example/logo.svg"] = () => Image("image/svg+xml");
            var service = new DuckDuckGoService(web);
            Assert.Null(await service.FetchFirstWorkingAsync(new[] { new ImageResult { Image = "https://x.example/logo.svg" }, new ImageResult { Image = "https://y.example/404.jpg" } }));
            Assert.Null(await service.FetchFirstWorkingAsync(new List<ImageResult>()));
        }

        [Fact]
        public async Task Search_AsksForATokenThenImages_AndReusesTheTokenForTheSameQuery()
        {
            var web = new FakeWeb();
            int pages = 0;
            web.Routes["https://duckduckgo.com/?q=dragon%20ball%20z&t=h_&iar=images&iax=images&ia=images"] = () => { pages++; return Page("vqd=\"4-777\""); };
            web.Fallback = uri => uri.AbsoluteUri.StartsWith("https://duckduckgo.com/i.js") && uri.Query.Contains("vqd=4-777") && uri.Query.Contains("dragon")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[{\"image\":\"https://img.example/1.jpg\"}]}") }
                : null;
            var service = new DuckDuckGoService(web);

            var first = await service.GetImages("dragon ball z");
            var second = await service.GetImages("dragon ball z");

            Assert.Equal(1, pages);
            Assert.Equal("https://img.example/1.jpg", Assert.Single(first).Image);
            Assert.Single(second);
        }
    }
}
