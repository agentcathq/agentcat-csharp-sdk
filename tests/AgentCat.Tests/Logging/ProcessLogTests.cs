using AgentCat.Internal.Logging;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using SdkDiagnostics = AgentCat.Internal.Diagnostics.Diagnostics;

namespace AgentCat.Tests.Logging;

public sealed class ProcessLogTests : IDisposable
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agentcat-processlog-" + Guid.NewGuid().ToString("N"));

    public ProcessLogTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LogPath => Path.Combine(_dir, "agentcat.log");

    private ProcessLog NewLog() => new(new FileLog(LogPath, () => Fixed));

    private string[] FileLines() => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : Array.Empty<string>();

    private static ILoggerFactory FactoryOf(RecordingLoggerProvider provider) => LoggerFactory.Create(b => b.AddProvider(provider));

    // TS: forwards every log entry to a registered sink
    [Fact]
    public void Forwards_every_log_entry_to_a_registered_sink()
    {
        var log = NewLog();
        var seen = new List<string>();
        log.SetDiagnosticsSink(seen.Add);

        log.Write("one");
        log.Write("two");

        Assert.Equal(new[] { LogLine.Format(Fixed, "one"), LogLine.Format(Fixed, "two") }, seen);
    }

    // TS: never throws when the sink throws
    [Fact]
    public void Never_throws_when_the_sink_throws()
    {
        var log = NewLog();
        log.SetDiagnosticsSink(_ => throw new InvalidOperationException("sink boom"));

        log.Write("still written");

        Assert.Equal(new[] { LogLine.Format(Fixed, "still written") }, FileLines());
    }

    // TS: stops forwarding after the sink is cleared
    [Fact]
    public void Stops_forwarding_after_the_sink_is_cleared()
    {
        var log = NewLog();
        var seen = new List<string>();
        log.SetDiagnosticsSink(seen.Add);
        log.Write("before");
        log.SetDiagnosticsSink(null);
        log.Write("after");

        Assert.Equal(new[] { LogLine.Format(Fixed, "before") }, seen);
    }

    [Fact]
    public void Sink_and_file_get_the_same_line()
    {
        var log = NewLog();
        string? sunk = null;
        log.SetDiagnosticsSink(l => sunk = l);

        log.Write("same");

        Assert.Equal(sunk, Assert.Single(FileLines()));
    }

    [Theory]
    [InlineData("ERROR: boom", LogLevel.Error)]
    [InlineData("Error: boom", LogLevel.Error)]
    [InlineData("error: lower case", LogLevel.Error)]
    [InlineData("Failed to send event evt_1, retrying... [Error: 503]", LogLevel.Error)]
    [InlineData("Failed to deliver AgentCat event after retries: HTTP 500", LogLevel.Error)]
    [InlineData("Telemetry export failed for otlp: connection refused", LogLevel.Error)]
    [InlineData("Failed to initialize exporter otlp", LogLevel.Error)]
    [InlineData("Warning: Failed to track server - x", LogLevel.Error)]
    [InlineData("WARN: careful", LogLevel.Warning)]
    [InlineData("WARN: AgentCat LogToHost works only with WithAgentCat; Track keeps writing to the AgentCat log file", LogLevel.Warning)]
    [InlineData("WARN: AGENTCAT_ENABLED \"maybe\" is not one of true, 1, yes, on, false, 0, no, off; the default is used", LogLevel.Warning)]
    [InlineData("WARN: AgentCat telemetry export queue full, dropped the oldest 3 request(s)", LogLevel.Warning)]
    [InlineData("WARN: Handle injection failed for tool \"search_errors\": boom", LogLevel.Warning)]
    [InlineData("WARN: Tool \"list_errors\" has complex schema (oneOf/allOf/anyOf). Skipping context injection.", LogLevel.Warning)]
    [InlineData("ERROR: Tool \"x\" already declares a 'agentcat_session_id' parameter.", LogLevel.Error)]
    [InlineData("Warning: AgentCat is slow", LogLevel.Warning)]
    [InlineData("Caution. Warning: inside", LogLevel.Warning)]
    [InlineData("Successfully sent event evt_1 | tools/call | session ses_1 | proj_a | 12 ms", LogLevel.Information)]
    [InlineData("AgentCat setup started | project p | sdk v1/di | signals filters", LogLevel.Information)]
    [InlineData("warning: lower case", LogLevel.Information)]
    public void Adopted_logger_gets_the_bare_message_with_the_level_rule(string message, LogLevel level)
    {
        var provider = new RecordingLoggerProvider();
        var log = NewLog();
        Assert.True(log.TryAdoptHost(FactoryOf(provider)));

        log.Write(message);

        var entry = Assert.Single(provider.Entries);
        Assert.Equal(("AgentCat", level, message), entry);
        Assert.Empty(FileLines());
    }

    [Theory]
    [InlineData("ERROR: boom")]
    [InlineData("Failed to send event evt_1, retrying... [Error: 503]")]
    [InlineData("Warning: AgentCat is slow")]
    [InlineData("Warning: Failed to track server - x")]
    [InlineData("Successfully sent event evt_1 | tools/call")]
    [InlineData("AgentCat setup started")]
    [InlineData("A FAIL here")]
    [InlineData("")]
    public void Host_and_diagnostics_classify_every_line_without_a_WARN_prefix_the_same_way(string message)
    {
        var provider = new RecordingLoggerProvider();
        var log = NewLog();
        string? sunk = null;
        log.SetDiagnosticsSink(l => sunk = l);
        Assert.True(log.TryAdoptHost(FactoryOf(provider)));

        log.Write(message);

        var host = Assert.Single(provider.Entries).Level;
        var (number, _) = SdkDiagnostics.Severity(sunk!);
        var expected = number switch
        {
            17 => LogLevel.Error,
            13 => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        Assert.Equal(expected, host);
        Assert.Equal(number, SdkDiagnostics.Severity(message).Number);
    }

    [Fact]
    public void A_routine_line_logs_at_Information_in_the_host_logger_but_diagnostics_keep_the_text_rule()
    {
        const string Line = "Successfully sent event evt_1 | mcp:tools/call | session ses_1 | proj_a | 12 ms | error_bot";
        var provider = new RecordingLoggerProvider();
        var log = NewLog();
        string? sunk = null;
        log.SetDiagnosticsSink(l => sunk = l);
        Assert.True(log.TryAdoptHost(FactoryOf(provider)));

        log.Write(Line, LogSeverity.Information);

        Assert.Equal(("AgentCat", LogLevel.Information, Line), Assert.Single(provider.Entries));
        Assert.Equal((17, "ERROR"), SdkDiagnostics.Severity(sunk!));
        Assert.Equal(LogLevel.Error, HostLogLevel.Of(Line));
    }

    [Theory]
    [InlineData("WARN: AgentCat telemetry export queue full, dropped the oldest 3 request(s)", 9)]
    [InlineData("WARN: Handle injection failed for tool \"x\"", 17)]
    [InlineData("Failed to send event evt_1, retrying... [Error: 503]", 17)]
    [InlineData("Warning: AgentCat is slow", 13)]
    public void Diagnostics_severity_ignores_the_WARN_prefix_rule_of_the_host(string message, int number)
    {
        Assert.Equal(number, SdkDiagnostics.Severity(message).Number);
    }

    [Fact]
    public void Braces_in_a_message_are_logged_verbatim()
    {
        var provider = new RecordingLoggerProvider();
        var log = NewLog();
        log.TryAdoptHost(FactoryOf(provider));

        log.Write("value {0} and {name}");

        Assert.Equal("value {0} and {name}", Assert.Single(provider.Entries).Message);
    }

    [Fact]
    public void Second_adoption_is_ignored()
    {
        var first = new RecordingLoggerProvider();
        var second = new RecordingLoggerProvider();
        var log = NewLog();

        Assert.True(log.TryAdoptHost(FactoryOf(first)));
        Assert.False(log.TryAdoptHost(FactoryOf(second)));
        log.Write("hello");

        Assert.Single(first.Entries);
        Assert.Empty(second.Entries);
    }

    [Fact]
    public void With_LogToHost_the_file_gets_nothing_but_the_sink_gets_the_line()
    {
        var provider = new RecordingLoggerProvider();
        var log = NewLog();
        var seen = new List<string>();
        log.SetDiagnosticsSink(seen.Add);
        log.TryAdoptHost(FactoryOf(provider));

        log.Write("hosted");

        Assert.Empty(FileLines());
        Assert.Equal(new[] { LogLine.Format(Fixed, "hosted") }, seen);
    }
}
