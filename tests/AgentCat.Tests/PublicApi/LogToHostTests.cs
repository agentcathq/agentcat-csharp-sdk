using AgentCat.Internal.Engine;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.PublicApi;

[Collection(SharedRuntime.Name)]
public sealed class LogToHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agentcat-logtohost-" + Guid.NewGuid().ToString("N"));

    public LogToHostTests()
    {
        Directory.CreateDirectory(_dir);
        AgentCatTracker.ResetLogToHostWarning();
    }

    public void Dispose()
    {
        AgentCatTracker.ResetLogToHostWarning();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LogPath => Path.Combine(_dir, "agentcat.log");

    private string[] FileLines() => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : Array.Empty<string>();

    private static AgentCatRuntime NewRuntime(ProcessLog log) => new(log, new RecordingSender());

    private static ILoggerFactory? Provider(RecordingLoggerProvider? logs)
        => logs is null ? null : LoggerFactory.Create(b => b.AddProvider(logs));

    [Fact]
    public void WithAgentCat_LogToHost_routes_setup_lines_to_the_host_logger()
    {
        var logs = new RecordingLoggerProvider();
        var runtime = NewRuntime(new ProcessLog(new FileLog(LogPath)));
        var post = new AgentCatPostConfigure("proj_a", o => o.LogToHost = true, () => runtime, null, Provider(logs));

        post.PostConfigure(null, new McpServerOptions());

        Assert.Equal(2, logs.Entries.Count);
        Assert.All(logs.Entries, e => Assert.Equal("AgentCat", e.Category));
        Assert.StartsWith("AgentCat setup started", logs.Entries[0].Message, StringComparison.Ordinal);
        Assert.StartsWith("AgentCat setup complete", logs.Entries[1].Message, StringComparison.Ordinal);
        Assert.Empty(FileLines());
    }

    [Fact]
    public async Task WithAgentCat_LogToHost_through_DI_uses_the_registered_logger_factory()
    {
        var logs = new RecordingLoggerProvider();
        var runtime = NewRuntime(new ProcessLog(new FileLog(LogPath)));
        await using var h = new McpHarness();
        h.UseLoggerFactory(Provider(logs)!);
        h.ConfigureServices(s => s.AddSingleton(runtime));
        h.ConfigureBuilder(b => b.WithAgentCat("proj_a", o => o.LogToHost = true));

        await h.StartAsync(null, withServices: true);

        Assert.Contains(logs.Entries, e => e.Category == "AgentCat" && e.Message.StartsWith("AgentCat setup started", StringComparison.Ordinal));
        Assert.Empty(FileLines());
    }

    [Fact]
    public void WithAgentCat_LogToHost_without_a_logger_factory_keeps_the_file()
    {
        var runtime = NewRuntime(new ProcessLog(new FileLog(LogPath)));
        var post = new AgentCatPostConfigure("proj_a", o => o.LogToHost = true, () => runtime, null, Provider(null));

        post.PostConfigure(null, new McpServerOptions());

        Assert.Equal(2, FileLines().Length);
    }

    [Fact]
    public void WithAgentCat_without_LogToHost_keeps_the_file()
    {
        var logs = new RecordingLoggerProvider();
        var runtime = NewRuntime(new ProcessLog(new FileLog(LogPath)));
        var post = new AgentCatPostConfigure("proj_a", null, () => runtime, null, Provider(logs));

        post.PostConfigure(null, new McpServerOptions());

        Assert.Empty(logs.Entries);
        Assert.Equal(2, FileLines().Length);
    }

    [Fact]
    public void A_runtime_with_another_log_adopts_nothing()
    {
        var logs = new RecordingLoggerProvider();
        var recording = new RecordingLog();
        var runtime = NewRuntime2(recording);
        var post = new AgentCatPostConfigure("proj_a", o => o.LogToHost = true, () => runtime, null, Provider(logs));

        post.PostConfigure(null, new McpServerOptions());

        Assert.Empty(logs.Entries);
        Assert.Equal(2, recording.Lines.Count);
    }

    private static AgentCatRuntime NewRuntime2(IAgentCatLog log) => new(log, new RecordingSender());

    [Fact]
    public void Track_with_LogToHost_writes_the_warning_once()
    {
        var log = new RecordingLog();
        var runtime = NewRuntime2(log);
        var options = new AgentCatOptions { LogToHost = true };

        AgentCatSdk.Track(new McpServerOptions(), "proj_a", options, () => runtime);
        AgentCatSdk.Track(new McpServerOptions(), "proj_b", options, () => runtime);

        Assert.Equal(
            "WARN: AgentCat LogToHost works only with WithAgentCat; Track keeps writing to the AgentCat log file",
            Assert.Single(log.Lines, l => l.StartsWith("WARN:", StringComparison.Ordinal)));
        Assert.StartsWith("AgentCat setup started", log.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("WARN:", log.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Track_without_LogToHost_writes_no_warning()
    {
        var log = new RecordingLog();
        var runtime = NewRuntime2(log);

        AgentCatSdk.Track(new McpServerOptions(), "proj_a", new AgentCatOptions(), () => runtime);

        Assert.DoesNotContain(log.Lines, l => l.StartsWith("WARN:", StringComparison.Ordinal));
    }
}
