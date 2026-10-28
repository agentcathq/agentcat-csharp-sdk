using System;
using System.IO;
namespace AgentCat.Internal.Logging;

internal static class LogHome
{
    /// <summary>
    /// Returns the directory that holds the log file, or null when the process has no usable home: the profile
    /// is unknown, empty, not an absolute path, or the root directory that .NET reports for an unknown user.
    /// </summary>
    internal static string? Resolve(Func<string?> userProfile)
    {
        try
        {
            var home = userProfile();
            if (string.IsNullOrEmpty(home) || home == "/" || !Path.IsPathRooted(home))
            {
                return null;
            }

            return home;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
