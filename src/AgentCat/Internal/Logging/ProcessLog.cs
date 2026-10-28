using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace AgentCat.Internal.Logging;

/// <summary>
/// The one place every AgentCat line goes through: a diagnostics sink gets the formatted line, then the line goes to
/// the file, or the bare message goes to the host logger once one is adopted.
/// </summary>
internal sealed class ProcessLog : IAgentCatLog
{
    private const string Category = "AgentCat";

    private readonly FileLog _file;
    private volatile Action<string>? _sink;
    private volatile ILogger? _host;
    private int _adopted;

    internal ProcessLog(FileLog file)
    {
        _file = file;
    }

    /// <summary>Sets the one diagnostics sink. <see langword="null"/> removes it.</summary>
    internal void SetDiagnosticsSink(Action<string>? sink) => _sink = sink;

    /// <summary>Adopts the host logger. The first call wins; later calls return <see langword="false"/>.</summary>
    internal bool TryAdoptHost(ILoggerFactory factory)
    {
        if (Interlocked.CompareExchange(ref _adopted, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            _host = factory.CreateLogger(Category);
        }
        catch (Exception)
        {
            // No host logger: the file keeps the lines.
        }

        return true;
    }

    public void Write(string message) => Emit(message, null);

    public void Write(string message, LogSeverity hostSeverity) => Emit(message, hostSeverity);

    private void Emit(string message, LogSeverity? hostSeverity)
    {
        try
        {
            var line = _file.Format(message);
            try
            {
                _sink?.Invoke(line);
            }
            catch (Exception)
            {
                // A sink never breaks the log.
            }

            var host = _host;
            if (host is null)
            {
                _file.WriteLine(line);
                return;
            }

            var level = hostSeverity is { } given ? HostLogLevel.Of(given) : HostLogLevel.Of(message);
            host.Log(level, default(EventId), message, null, static (s, _) => s);
        }
        catch (Exception)
        {
            // A log line never breaks the caller.
        }
    }
}
