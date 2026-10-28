using AgentCat.Internal.Logging;

namespace AgentCat.Tests.TestSupport;

internal sealed class RecordingLog : IAgentCatLog
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();
    private readonly List<(string Line, LogSeverity? Severity)> _written = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return _lines.ToArray();
            }
        }
    }

    public IReadOnlyList<(string Line, LogSeverity? Severity)> Written
    {
        get
        {
            lock (_gate)
            {
                return _written.ToArray();
            }
        }
    }

    public Func<string, bool>? ThrowWhen { get; set; }

    // Runs after the line is recorded, outside the lock, on the writing thread.
    public Action<string>? AfterWrite { get; set; }

    public void Write(string message, LogSeverity hostSeverity) => Record(message, hostSeverity);

    public void Write(string message) => Record(message, null);

    private void Record(string message, LogSeverity? severity)
    {
        if (ThrowWhen?.Invoke(message) == true)
        {
            throw new InvalidOperationException("log boom");
        }

        lock (_gate)
        {
            _lines.Add(message);
            _written.Add((message, severity));
        }

        AfterWrite?.Invoke(message);
    }
}
