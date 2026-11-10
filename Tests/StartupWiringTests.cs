using DiscordBot.Services;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using Xunit;

namespace Tests
{
    /// <summary>Catches wiring mistakes that only show up when the bot starts on the Pi.</summary>
    public class StartupWiringTests
    {
        /// <summary>
        /// Classes registered with AddHttpClient are built by dependency injection, which refuses to start the whole bot
        /// if it can't pick one constructor. Every class that takes an HttpClient must have exactly one public constructor.
        /// </summary>
        [Fact]
        public void TypedHttpClients_HaveExactlyOnePublicConstructor()
        {
            var typedClients = typeof(TranslationService).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(HttpClient))))
                .ToList();

            Assert.Contains(typeof(TranslationService), typedClients);
            Assert.All(typedClients, t => Assert.True(t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 1,
                $"{t.Name} has more than one public constructor, so dependency injection can't build it and the bot won't start."));
        }
    }
}
