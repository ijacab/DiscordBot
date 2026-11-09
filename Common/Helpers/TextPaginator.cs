using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Common.Helpers
{
    /// <summary>
    /// Packs lines of text into embed-sized pages. Discord rejects an embed whose
    /// total text is over 6000 characters, a field value over 1024, or more than 25 fields,
    /// so long lists have to be sent as several embeds.
    /// </summary>
    public static class TextPaginator
    {
        public const int FieldLimit = 1000;
        public const int EmbedTextLimit = 5000;
        public const int FieldsPerEmbed = 5;

        /// <summary>
        /// Returns pages, each page being a list of field values. Lines are kept whole
        /// where they fit in a field, and only broken at spaces when they are longer than a field.
        /// </summary>
        public static List<List<string>> Paginate(IEnumerable<string> lines)
        {
            var embeds = new List<List<string>>();
            var currentEmbed = new List<string>();
            int currentEmbedChars = 0;
            var field = new StringBuilder();

            void AddField(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                if (currentEmbed.Count == FieldsPerEmbed || currentEmbedChars + text.Length > EmbedTextLimit)
                {
                    embeds.Add(currentEmbed);
                    currentEmbed = new List<string>();
                    currentEmbedChars = 0;
                }
                currentEmbed.Add(text);
                currentEmbedChars += text.Length;
            }

            foreach (var line in lines)
            {
                foreach (var piece in Wrap(line ?? string.Empty, FieldLimit))
                {
                    if (field.Length > 0 && field.Length + piece.Length + 1 > FieldLimit)
                    {
                        AddField(field.ToString().TrimEnd('\n'));
                        field.Clear();
                    }
                    field.Append(piece).Append('\n');
                }
            }

            AddField(field.ToString().TrimEnd('\n'));
            if (currentEmbed.Count > 0) embeds.Add(currentEmbed);
            return embeds;
        }

        private static IEnumerable<string> Wrap(string line, int max)
        {
            while (line.Length > max)
            {
                int cut = line.LastIndexOf(' ', max - 1);
                if (cut <= 0) cut = max;
                yield return line.Substring(0, cut);
                line = line.Substring(cut).TrimStart(' ');
            }
            yield return line;
        }
    }
}
