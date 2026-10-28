using Microsoft.Extensions.Logging;

namespace AgentCat.Tests.TestSupport;

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<(string Category, LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Recorder : ILogger
    {
        private readonly RecordingLoggerProvider _owner;
        private readonly string _category;

        internal Recorder(RecordingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_owner._gate)
            {
                _owner._entries.Add((_category, logLevel, formatter(state, exception)));
            }
        }
    }
}
