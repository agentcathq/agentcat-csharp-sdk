using System.Text.Json;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Engine;

public class HostSafetyTests
{
    private const string ToolErrorLine = "\"explode\" threw an unhandled exception.";

    private static readonly string[] CalledTools = { "explode", "fine" };

    private static readonly InvalidOperationException Thrown = new("kaboom from the customer's tool");

    public static TheoryData<string?> Protocols => McpHarness.ProtocolVersions;

    private static string Explode() => throw Thrown;

    private static ValueTask<CallToolResult> ExplodeHandler(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => throw Thrown;

    private static string Fine() => "fine";

    private static string[] Texts(CallToolResult result) => result.Content.Select(c => Assert.IsType<TextContentBlock>(c).Text).ToArray();

    private static async Task<(CallToolResult Result, LogEntry[] Entries)> CallOnceAsync(McpHarness h, string? protocolVersion, ListLogger logger)
    {
        await h.StartAsync(protocolVersion);
        var result = await h.CallToolAsync("explode");
        return (result, logger.Snapshot());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task The_SDK_tool_error_log_line_still_appears(string? protocolVersion)
    {
        var plainLog = new ListLogger();
        await using var plain = new McpHarness();
        plain.UseLoggerFactory(plainLog).AddTool(new Func<string>(Explode), "explode", "Throws");
        var baseline = await CallOnceAsync(plain, protocolVersion, plainLog);

        var log = new ListLogger();
        await using var h = new EngineHarness();
        h.UseLoggerFactory(log).AddTool(new Func<string>(Explode), "explode", "Throws");
        var traced = await CallOnceAsync(h, protocolVersion, log);

        var expected = Assert.Single(baseline.Entries, e => e.Level == LogLevel.Error && e.Message == ToolErrorLine);
        var line = Assert.Single(traced.Entries, e => e.Level == LogLevel.Error && e.Message == ToolErrorLine);
        Assert.Equal(expected.Level, line.Level);
        Assert.Same(Thrown, line.Exception);
        Assert.Equal(baseline.Entries.Count(e => e.Level >= LogLevel.Warning), traced.Entries.Count(e => e.Level >= LogLevel.Warning));
        Assert.Equal(true, traced.Result.IsError);
        var published = Assert.Single(await h.FlushAsync());
        Assert.Equal("mcp:tools/call", (string?)published["eventType"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task User_error_filters_still_run(string? protocolVersion)
    {
        var seen = new List<Exception>();
        var names = new List<string?>();
        var after = 0;
        await using var h = new EngineHarness();
        h.AddTool(new Func<string>(Explode), "explode", "Throws").AddTool(new Func<string>(Fine), "fine", "Works");
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(next => async (context, cancellationToken) =>
        {
            lock (names)
            {
                names.Add(context.Params?.Name);
            }

            try
            {
                var result = await next(context, cancellationToken);
                Interlocked.Increment(ref after);
                return result;
            }
            catch (Exception ex)
            {
                lock (seen)
                {
                    seen.Add(ex);
                }

                throw;
            }
        }));
        await h.StartAsync(protocolVersion);

        var failed = await h.CallToolAsync("explode");
        var worked = await h.CallToolAsync("fine");

        Assert.Equal(true, failed.IsError);
        Assert.NotEqual(true, worked.IsError);
        Assert.Same(Thrown, Assert.Single(seen));
        Assert.Equal(1, Volatile.Read(ref after));
        Assert.Equal(CalledTools, names);
        Assert.Equal(CalledTools, (await h.FlushAsync()).Select(e => (string?)e["resourceName"]).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task The_tool_exception_reaches_the_SDK_unchanged(string? protocolVersion)
    {
        var plainLog = new ListLogger();
        await using var plain = new McpHarness();
        plain.UseLoggerFactory(plainLog).AddHandlerTool("""{"name":"explode","inputSchema":{"type":"object"}}""", ExplodeHandler);
        var baseline = await CallOnceAsync(plain, protocolVersion, plainLog);

        var log = new ListLogger();
        await using var h = new EngineHarness();
        h.UseLoggerFactory(log).AddHandlerTool("""{"name":"explode","inputSchema":{"type":"object"}}""", ExplodeHandler);
        var traced = await CallOnceAsync(h, protocolVersion, log);

        var expected = Assert.Single(baseline.Entries, e => e.Level == LogLevel.Error && e.Message == ToolErrorLine);
        var line = Assert.Single(traced.Entries, e => e.Level == LogLevel.Error && e.Message == ToolErrorLine);
        Assert.Same(Thrown, expected.Exception);
        Assert.Same(Thrown, line.Exception);
        Assert.Equal(true, traced.Result.IsError);
        Assert.Equal(Texts(baseline.Result), Texts(traced.Result).Where(t => !t.StartsWith("[session_id", StringComparison.Ordinal)).ToArray());
        Assert.Single(await h.FlushAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task AgentCat_failures_never_reach_the_tool_result(string? protocolVersion)
    {
        foreach (var stage in new[] { "strip", "prepare", "finish", "fail" })
        {
            var log = new ListLogger();
            await using var h = new EngineHarness();
            h.UseLoggerFactory(log).AddTool(new Func<string>(Explode), "explode", "Throws").AddTool(new Func<string>(Fine), "fine", "Works");
            await h.StartAsync(protocolVersion);
            h.Tracker!.OnCallStage = s =>
            {
                if (s == stage)
                {
                    throw new InvalidOperationException("agentcat " + stage + " failure");
                }
            };

            var failed = await h.CallToolAsync("explode");
            var worked = await h.CallToolAsync("fine");

            Assert.True(failed.IsError == true, stage);
            Assert.DoesNotContain("agentcat", string.Join("|", Texts(failed)), StringComparison.Ordinal);
            Assert.NotEqual(true, worked.IsError);
            Assert.DoesNotContain("agentcat", string.Join("|", Texts(worked)), StringComparison.Ordinal);
            Assert.Contains("fine", Texts(worked));
            Assert.Same(Thrown, Assert.Single(log.Snapshot(), e => e.Level == LogLevel.Error && e.Message == ToolErrorLine).Exception);
            Assert.Contains(h.Log.Lines, l => l.StartsWith("Warning: AgentCat ", StringComparison.Ordinal));
        }
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLogger : ILoggerFactory, ILogger
    {
        private readonly List<LogEntry> _entries = new();

        internal LogEntry[] Snapshot()
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }
}
