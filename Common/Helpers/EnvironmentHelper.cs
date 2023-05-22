using Common.Exceptions;
using System;
using System.Collections.Generic;
using System.Text;

namespace Common.Helpers
{
    public static class EnvironmentHelper
    {
        public static string GetEnvironmentVariable(string environmentVariableName, bool throwIfNotFound = true)
        {
            string envVarValue = Environment.GetEnvironmentVariable(environmentVariableName, EnvironmentVariableTarget.Process) ??
                Environment.GetEnvironmentVariable(environmentVariableName, EnvironmentVariableTarget.User) ??
                Environment.GetEnvironmentVariable(environmentVariableName, EnvironmentVariableTarget.Machine);

            if (string.IsNullOrEmpty(envVarValue))
            {
                if(throwIfNotFound)
                    throw new EnvironmentVariableMissingException($"No environment variable found for {environmentVariableName}");
            }

            return envVarValue;
        }
    }
}
