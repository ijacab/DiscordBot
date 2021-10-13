using System;

namespace Common.Helpers
{
    public static class FormatHelper
    {
        public static string GetCommaNumber(object numberObj, bool roundDown = true) //can be string or int
        {
            if (numberObj is double) 
            {
                double numberDouble = (double)numberObj;
                if (roundDown)
                    numberObj = Math.Floor(numberDouble);
                else
                    numberObj = Math.Ceiling(numberDouble);
            }
            return String.Format("{0:n0}", numberObj);
        }
    }
}
