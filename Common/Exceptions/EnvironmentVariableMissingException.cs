using System;
using System.Collections.Generic;
using System.Text;

namespace Common.Exceptions
{
    public class EnvironmentVariableMissingException : Exception
    {
        public EnvironmentVariableMissingException() : base("Environment variable is missing.")
        {
        }

        public EnvironmentVariableMissingException(string message) : base(message)
        {
        }
    }
}
