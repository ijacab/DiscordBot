using System;

namespace Common.Helpers
{
    public static class StringHelper
    {
        public static bool EqualsCaseInsensitive(this string string1, string string2)
        {
            return String.Equals(string1, string2, StringComparison.OrdinalIgnoreCase);
        }
    }
}
