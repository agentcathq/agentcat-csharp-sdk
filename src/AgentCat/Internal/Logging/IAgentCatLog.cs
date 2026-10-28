namespace AgentCat.Internal.Logging;

internal interface IAgentCatLog
{
    /// <summary>Writes one SDK log line. The host logger level comes from the text: use this only when AgentCat's own text decides the level, else use <c>WriteRoutine</c> or the overload with a level.</summary>
    /// <param name="message">The line, without a prefix or a line break.</param>
    /// <remarks>
    /// Implementations must never throw. The event pipeline writes on its delivery path without guarding
    /// each call.
    /// </remarks>
    void Write(string message);

    /// <summary>Writes one SDK log line whose host logger level is <paramref name="hostSeverity"/>, not the level of its text.</summary>
    /// <remarks>
    /// For a line that carries a value the user supplied, such as a tool name, an ID or a URL, or whose own text names
    /// an error that did not happen. The file and diagnostics get the same line as from <see cref="Write(string)"/>.
    /// </remarks>
    void Write(string message, LogSeverity hostSeverity);
}

internal static class AgentCatLogExtensions
{
    /// <summary>Writes a routine line: the host logger gets it at Information whatever its text says.</summary>
    internal static void WriteRoutine(this IAgentCatLog log, string message) => log.Write(message, LogSeverity.Information);
}

internal sealed class NullAgentCatLog : IAgentCatLog
{
    internal static readonly NullAgentCatLog Instance = new();

    private NullAgentCatLog()
    {
    }

    public void Write(string message)
    {
    }

    public void Write(string message, LogSeverity hostSeverity)
    {
    }
}
