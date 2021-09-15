using System;
using System.Collections.Generic;
using System.Text;

namespace Common.Helpers
{
    public static class EnvironmentHelper
    {
        public static string GetEnvironmentVariableOrThrow(string environmentVariableName)
        {
            string envVarValue = Environment.GetEnvironmentVariable(environmentVariableName, EnvironmentVariableTarget.Process) ??
                Environment.GetEnvironmentVariable(environmentVariableName, EnvironmentVariableTarget.User) ??
                Environment.GetEnvironmentVariable(environmentVariableName, EnvironmentVariableTarget.Machine);

            if (string.IsNullOrEmpty(envVarValue))
                throw new Exception($"No environment variable found for {environmentVariableName}");
            else
                return envVarValue;
        }
    }
}
