using System;
using System.Collections.Generic;
using System.Globalization;
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
    }
}
