using System;
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
    }
}
