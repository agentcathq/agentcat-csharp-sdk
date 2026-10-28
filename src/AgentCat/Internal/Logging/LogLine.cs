using System;
using System.Globalization;
using System.Reflection;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Logging;

internal static class LogLine
{
    private const string Unknown = "unknown";

    /// <summary>The informational version of ModelContextProtocol.Core without "+…", or <c>unknown</c>.</summary>
    internal static string McpVersion { get; } = ReadMcpVersion();

    /// <summary>The bracketed version prefix, built once.</summary>
    internal static string Prefix { get; } = BuildPrefix();

    /// <summary>Formats one log line, without the line break.</summary>
    internal static string Format(DateTimeOffset utcNow, string message)
        => "[" + utcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "] " + Prefix + " " + message;

    private static string BuildPrefix()
        => "[sdk=" + SdkInfo.Version + " dotnet=" + SdkInfo.RuntimeVersion + " mcp=" + McpVersion + "]";

    private static string ReadMcpVersion()
    {
        try
        {
            var version = typeof(McpServerOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(version))
            {
                return Unknown;
            }

            var plus = version!.IndexOf('+');
            var trimmed = plus >= 0 ? version.Substring(0, plus) : version;
            return trimmed.Length == 0 ? Unknown : trimmed;
        }
        catch (Exception)
        {
            return Unknown;
        }
    }
}
