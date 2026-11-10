using Discord;
using DiscordBot.Services;
using Moq;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Tests
{
    public class LiveMessageTests
    {
        private static (Mock<IMessageChannel> Channel, Mock<IUserMessage> Message) Fakes(List<List<string>> sentNames)
        {
            var message = new Mock<IUserMessage>();
            message.SetupGet(m => m.Id).Returns(42UL);
            var channel = new Mock<IMessageChannel>();
            channel.Setup(c => c.SendFilesAsync(It.IsAny<IEnumerable<FileAttachment>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Embed>(),
                    It.IsAny<RequestOptions>(), It.IsAny<AllowedMentions>(), It.IsAny<MessageReference>(), It.IsAny<MessageComponent>(),
                    It.IsAny<ISticker[]>(), It.IsAny<Embed[]>(), It.IsAny<MessageFlags>(), It.IsAny<PollProperties>()))
                .Callback((IEnumerable<FileAttachment> files, string _, bool _, Embed _, RequestOptions _, AllowedMentions _, MessageReference _,
                    MessageComponent _, ISticker[] _, Embed[] _, MessageFlags _, PollProperties _) => sentNames.Add(files.Select(f => f.FileName).ToList()))
                .ReturnsAsync(message.Object);
            return (channel, message);
        }

        private static LiveMessage.Frame FrameWithScene(LiveMessage live, out string url)
        {
            LiveMessage.Frame frame = live.NewFrame();
            url = frame.Add("scene", new byte[] { 1, 2, 3 });
            return frame;
        }

        [Fact]
        public async Task FirstPublish_PostsTheMessage()
        {
            var sent = new List<List<string>>();
            var (channel, message) = Fakes(sent);
            var live = new LiveMessage(channel.Object);

            LiveMessage.Frame frame = FrameWithScene(live, out string url);
            Assert.True(await live.PublishAsync(frame, new Embed[0], null));

            Assert.Single(sent);
            Assert.Equal("scene-1.png", sent[0].Single());
            Assert.Equal("attachment://scene-1.png", url);
            Assert.True(live.Owns(42));
            Assert.False(live.Owns(7));
        }

        [Fact]
        public async Task LaterPublishes_EditInPlaceWithFreshFileNames()
        {
            var sent = new List<List<string>>();
            var (channel, message) = Fakes(sent);
            var edits = new List<MessageProperties>();
            message.Setup(m => m.ModifyAsync(It.IsAny<System.Action<MessageProperties>>(), It.IsAny<RequestOptions>()))
                .Callback((System.Action<MessageProperties> change, RequestOptions _) =>
                {
                    var properties = new MessageProperties();
                    change(properties);
                    edits.Add(properties);
                })
                .Returns(Task.CompletedTask);
            var live = new LiveMessage(channel.Object);

            await live.PublishAsync(FrameWithScene(live, out _), new Embed[0], null);
            await live.PublishAsync(FrameWithScene(live, out string secondUrl), new Embed[0], null);

            Assert.Single(sent);
            Assert.Single(edits);
            Assert.Equal("scene-2.png", edits[0].Attachments.Value.Single().FileName);
            Assert.Equal("attachment://scene-2.png", secondUrl);
            Assert.True(edits[0].Components.IsSpecified);
        }

        [Fact]
        public async Task Bump_PostsANewMessageAndDeletesTheOld()
        {
            var sent = new List<List<string>>();
            var (channel, message) = Fakes(sent);
            var live = new LiveMessage(channel.Object);

            await live.PublishAsync(FrameWithScene(live, out _), new Embed[0], null);
            await live.BumpAsync(FrameWithScene(live, out _), new Embed[0], null);

            Assert.Equal(2, sent.Count);
            message.Verify(m => m.DeleteAsync(It.IsAny<RequestOptions>()), Times.Once);
        }
    }
}
