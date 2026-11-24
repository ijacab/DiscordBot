using DiscordBot.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Tests
{
    /// <summary>Raffle messages that couldn't be sent wait and are tried again.</summary>
    public class AnnouncementQueueTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task AFailedMessage_StaysQueued_AndGoesOutOnceSendingWorks()
        {
            var queue = new AnnouncementQueue();
            queue.Enqueue("winner", null, Now);
            Assert.Equal(0, await queue.Flush(_ => Task.FromResult(false), Now.AddMinutes(1)));
            Assert.Equal(1, queue.Count);
            Assert.Equal(0, await queue.Flush(_ => throw new InvalidOperationException("no permission"), Now.AddMinutes(2)));
            Assert.Equal(1, queue.Count);

            var sent = new System.Collections.Generic.List<string>();
            Assert.Equal(1, await queue.Flush(item => { sent.Add(item.Text); return Task.FromResult(true); }, Now.AddMinutes(3)));
            Assert.Equal(new[] { "winner" }, sent);
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public async Task MessagesGoOutInOrder_AndOnlyTheOnesThatWorkedAreRemoved()
        {
            var queue = new AnnouncementQueue();
            foreach (var text in new[] { "a", "b", "c" }) queue.Enqueue(text, null, Now);
            var sent = await queue.Flush(item => Task.FromResult(item.Text != "b"), Now.AddMinutes(1));
            Assert.Equal(2, sent);
            Assert.Equal(1, queue.Count);
            var left = new System.Collections.Generic.List<string>();
            await queue.Flush(item => { left.Add(item.Text); return Task.FromResult(false); }, Now.AddMinutes(2));
            Assert.Equal(new[] { "b" }, left);
        }

        [Fact]
        public async Task OldMessagesAreDroppedAndTheQueueIsCapped()
        {
            var queue = new AnnouncementQueue();
            queue.Enqueue("stale", null, Now);
            Assert.Equal(0, await queue.Flush(_ => Task.FromResult(true), Now + AnnouncementQueue.Life + TimeSpan.FromMinutes(1)));
            Assert.Equal(0, queue.Count);

            for (int i = 0; i < AnnouncementQueue.MaxWaiting + 10; i++) queue.Enqueue("m" + i, null, Now);
            Assert.Equal(AnnouncementQueue.MaxWaiting, queue.Count);
            var first = new System.Collections.Generic.List<string>();
            await queue.Flush(item => { first.Add(item.Text); return Task.FromResult(false); }, Now);
            Assert.Equal("m10", first.First());
        }
    }
}
