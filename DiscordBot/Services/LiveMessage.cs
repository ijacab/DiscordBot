using Discord;
using Discord.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DiscordBot.Services
{
    /// <summary>
    /// One game dashboard: a message with images, embeds and buttons that is edited in place.
    /// Build a <see cref="Frame"/>, add its images (each gets a fresh file name, so clients never show a cached picture),
    /// point the embeds at <see cref="Frame.Add"/>'s urls, then publish it.
    /// The first publish posts the message; later ones edit it. If the message was deleted, the next publish posts a new one.
    /// </summary>
    public class LiveMessage
    {
        private int _frameNumber;

        public LiveMessage(IMessageChannel channel)
        {
            Channel = channel;
        }

        public IMessageChannel Channel { get; }
        /// <summary>The dashboard message, or null before the first publish.</summary>
        public IUserMessage Message { get; private set; }

        /// <summary>True when the message id is this dashboard. Clicks on any other (older) message are stale.</summary>
        public bool Owns(ulong messageId) => Message != null && Message.Id == messageId;

        public Frame NewFrame() => new Frame(++_frameNumber);

        /// <summary>Edits the dashboard to show the frame, or posts it if there is no dashboard yet. Returns false if Discord refused.</summary>
        public async Task<bool> PublishAsync(Frame frame, Embed[] embeds, MessageComponent components)
        {
            if (Message == null)
                return await PostAsync(frame, embeds, components);

            List<FileAttachment> files = frame.ToAttachments();
            try
            {
                await Message.ModifyAsync(m =>
                {
                    m.Content = "";
                    m.Attachments = files;
                    m.Embeds = embeds;
                    m.Components = components ?? new ComponentBuilder().Build();
                });
                return true;
            }
            catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.UnknownMessage)
            {
                //someone deleted the dashboard, so post a fresh one
                Message = null;
                return await PostAsync(frame, embeds, components);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                Dispose(files);
            }
        }

        /// <summary>Posts the frame as a new message at the bottom of the channel and deletes the old dashboard.</summary>
        public async Task<bool> BumpAsync(Frame frame, Embed[] embeds, MessageComponent components)
        {
            IUserMessage previous = Message;
            Message = null;
            bool posted = await PostAsync(frame, embeds, components);
            if (!posted)
            {
                Message = previous;
                return false;
            }
            if (previous != null)
            {
                try
                {
                    await previous.DeleteAsync();
                }
                catch (Exception)
                {
                    //the old dashboard may already be gone
                }
            }
            return true;
        }

        private async Task<bool> PostAsync(Frame frame, Embed[] embeds, MessageComponent components)
        {
            List<FileAttachment> files = frame.ToAttachments();
            try
            {
                Message = await Channel.SendFilesAsync(files, embeds: embeds, components: components);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                Dispose(files);
            }
        }

        private static void Dispose(List<FileAttachment> files)
        {
            foreach (FileAttachment file in files)
                file.Dispose();
        }

        /// <summary>The images for one publish.</summary>
        public sealed class Frame
        {
            private readonly List<(string FileName, byte[] Png)> _images = new List<(string, byte[])>();

            internal Frame(int number)
            {
                Number = number;
            }

            public int Number { get; }
            public IReadOnlyList<string> FileNames => _images.Select(i => i.FileName).ToList();

            /// <summary>Adds a PNG and returns the url an embed uses to show it, like attachment://scene-12.png.</summary>
            public string Add(string baseName, byte[] png)
            {
                string fileName = $"{baseName}-{Number}.png";
                _images.Add((fileName, png));
                return $"attachment://{fileName}";
            }

            internal List<FileAttachment> ToAttachments()
            {
                return _images.Select(i => new FileAttachment(new MemoryStream(i.Png), i.FileName)).ToList();
            }
        }
    }
}
