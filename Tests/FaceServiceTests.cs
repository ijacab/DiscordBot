using DiscordBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
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
    /// <summary>Packs must not hand out the same face twice: the site serves one face to requests made together.</summary>
    public class FaceServiceTests
    {
        /// <summary>Answers each request with the next of <paramref name="bodies"/> (the last one again once they run out).</summary>
        private sealed class Replies : HttpMessageHandler
        {
            private readonly byte[][] _bodies;
            private int _next;
            public int Requests => _next;
            public List<string> Urls { get; } = new List<string>();
            public Replies(params byte[][] bodies) { _bodies = bodies; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Urls.Add(request.RequestUri.ToString());
                var body = _bodies[Math.Min(_next++, _bodies.Length - 1)];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
            }
        }

        private static (FaceService Service, Replies Handler) Make(params byte[][] bodies)
        {
            var handler = new Replies(bodies);
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://faces.test") };
            return (new FaceService(NullLogger<FaceService>.Instance, client) { Spacing = TimeSpan.Zero, RetryDelay = TimeSpan.Zero }, handler);
        }

        [Fact]
        public async Task FetchDistinct_RetriesAFaceThatCameBackTheSame()
        {
            byte[] a = { 1 }, b = { 2 }, c = { 3 };
            var (service, handler) = Make(a, a, b, b, c);
            var faces = await service.FetchDistinct(3, new HashSet<string>());
            Assert.Equal(new[] { a, b, c }, faces);
            Assert.Equal(5, handler.Requests);
            Assert.Equal(handler.Urls.Count, handler.Urls.Distinct().Count()); //every request is cache-busted
        }

        [Fact]
        public async Task FetchDistinct_AvoidsFacesTheGangAlreadyHas_AndGivesUpWithNull()
        {
            byte[] a = { 1 };
            var (service, handler) = Make(a);
            var avoid = new HashSet<string> { FaceService.HashOf(a) };
            var faces = await service.FetchDistinct(2, avoid);
            Assert.All(faces, Assert.Null);
            Assert.Equal(2 * FaceService.TriesPerFace, handler.Requests);
        }
    }
}
