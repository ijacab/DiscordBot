using System;
using System.Text.RegularExpressions;

namespace Common.Helpers
{
    /// <summary>Parses short durations typed in chat, like 30m, 48h or 2d.</summary>
    public static class DurationParser
    {
        private static readonly Regex _pattern = new Regex(@"^(\d{1,5})\s*([mhd])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True for a positive whole number followed by m (minutes), h (hours) or d (days).</summary>
        public static bool TryParse(string text, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var match = _pattern.Match(text.Trim());
            if (!match.Success) return false;

            int amount = int.Parse(match.Groups[1].Value);
            if (amount < 1) return false;

            duration = char.ToLowerInvariant(match.Groups[2].Value[0]) switch
            {
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromDays(amount)
            };
            return true;
        }
    }
}
