using System;
using Microsoft.Extensions.Logging;

namespace AgentCat.Internal.Logging;

internal static class HostLogLevel
{
    /// <summary>A line that starts with <c>WARN:</c> is Warning. Any other line takes the shared rule of the diagnostics.</summary>
    internal static LogLevel Of(string message)
        => message.StartsWith("WARN:", StringComparison.Ordinal) ? LogLevel.Warning : Of(LogSeverityRule.Of(message));

    internal static LogLevel Of(LogSeverity severity) => severity switch
    {
        LogSeverity.Error => LogLevel.Error,
        LogSeverity.Warning => LogLevel.Warning,
        _ => LogLevel.Information,
    };
}
