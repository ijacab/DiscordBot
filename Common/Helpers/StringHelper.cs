using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Common.Helpers
{
    public static class StringHelper
    {
        public static bool EqualsCaseInsensitive(this string string1, string string2)
        {
            return String.Equals(string1, string2, StringComparison.OrdinalIgnoreCase);
        }

        public static string SplitCamelCaseWithSpace(this string camelCaseString)
        {
            var r = new Regex(@"
                (?<=[A-Z])(?=[A-Z][a-z]) |
                 (?<=[^A-Z])(?=[A-Z]) |
                 (?<=[A-Za-z])(?=[^A-Za-z])", RegexOptions.IgnorePatternWhitespace);

            return r.Replace(camelCaseString, " ");
        }

        public static string ToTitleCase(this string str)
        {
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(str.ToLower());
        }

        public static string TrimEnd(this string source, string value)
        {
            if (!source.EndsWith(value))
                return source;

            return source.Remove(source.LastIndexOf(value));
        }

        public static string CombineListToString(this IEnumerable<string> list, string separator, string wordPrefix = "", string wordSuffix = "", string wordSurrounder ="")
        {
            string output = "";
            foreach (string str in list)
            {
                output += $"{wordSurrounder}{wordPrefix}{str}{wordSuffix}{wordSurrounder}{separator}";
            }
            output.TrimEnd(separator);
            return output;
        }

        public static string MakeJsonSafe(String s)
        {
            var jsonEscaped = s.Replace("\\", "\\\\")
                               .Replace("\"", "\\\"")
                               .Replace("\b", "\\b")
                               .Replace("\f", "\\f")
                               .Replace("\n", "\\n")
                               .Replace("\r", "\\r")
                               .Replace("\t", "\\t");
            var nonAsciiEscaped = jsonEscaped.Select((c) => c >= 127 ? "\\u" + ((int)c).ToString("X").PadLeft(4, '0') : c.ToString());
            return string.Join("", jsonEscaped);
        }
    }
}
