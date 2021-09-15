using System;
using System.Collections.Generic;
using System.Text;

namespace Common.Helpers
{
    public static class FormatHelper
    {
        public static string GetCommaNumber(object numberObj) //can be string or int
        {
            return String.Format("{0:n0}", numberObj);
        }
    }
}
