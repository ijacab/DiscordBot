using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Common.Helpers
{
    public static class RandomHelper
    {
        //https://stackoverflow.com/a/1262619

        [ThreadStatic] private static Random _local;

        public static Random ThisThreadsRandom
        {
            get { return _local ??= new Random(unchecked(Environment.TickCount * 31 + Thread.CurrentThread.ManagedThreadId)); }
        }
    }
}
