using AgentCat.Internal.Engine;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.PublicApi;

// EnabledGate.SkippedAnInstall and the LogToHost warning are process-wide, so these tests run alone.
[Collection(SharedRuntime.Name)]
public sealed class TestRunSilenceTests : IDisposable
{
    private const string ProjectRequired = "projectId is required for publishCustomEvent";

    private const string FirstParameter = "First parameter must be either an MCP server object or a session ID string";

    private const string NotTracked = "Server is not tracked. Please call agentcat.track() first or provide a session ID string.";

    private const string LogToHostWarning = "WARN: AgentCat LogToHost works only with WithAgentCat; Track keeps writing to the AgentCat log file";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agentcat-silence-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _lines = new();
    private readonly List<AgentCatRuntime> _runtimes = new();

    public TestRunSilenceTests()
    {
        Directory.CreateDirectory(_dir);
        EnabledGate.ResetSkippedAnInstall();
        AgentCatTracker.ResetLogToHostWarning();
    }

    public void Dispose()
    {
        EnabledGate.ResetSkippedAnInstall();
        AgentCatTracker.ResetLogToHostWarning();
        foreach (var runtime in _runtimes)
        {
            runtime.Pipeline.Dispose();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static TheoryData<string> EntryPoints => new() { "WithAgentCat", "Track" };

    private string LogPath => Path.Combine(_dir, "agentcat.log");

    private RecordingSender Sender { get; } = new();

    // Every line the runtime writes, formatted as the file would get it.
    private string[] Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

    private AgentCatRuntime NewRuntime(Func<string, string?> environment, Func<bool> inTestRun)
    {
        var log = new ProcessLog(new FileLog(LogPath));
        log.SetDiagnosticsSink(line =>
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        });
        var runtime = new AgentCatRuntime(log, Sender, gate: new EnabledGate(environment, inTestRun, log));
        _runtimes.Add(runtime);
        return runtime;
    }

    // A test run with no AGENTCAT_ENABLED.
    private AgentCatRuntime TestRun() => NewRuntime(static _ => null, static () => true);

    private static bool HasContext(Tool tool)
        => tool.InputSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("context", out _);

    private static McpHarness EchoServer()
    {
        var h = new McpHarness();
        h.AddTool((string msg) => "echo:" + msg, "echo");
        return h;
    }

    private static async Task StartAsync(McpHarness h, string entryPoint, AgentCatRuntime runtime)
    {
        if (entryPoint == "WithAgentCat")
        {
            h.ConfigureServices(s => s.AddSingleton(runtime));
            h.ConfigureBuilder(b => b.WithAgentCat("proj_test"));
            await h.StartAsync(null, withServices: true);
        }
        else
        {
            h.Configure(o => Assert.Same(o, AgentCatSdk.Track(o, "proj_test", null, () => runtime)));
            await h.StartAsync();
        }
    }

    // The tools, the arguments and the results are the customer's own.
    private static async Task AssertUntrackedAsync(McpHarness h)
    {
        Assert.False(EngineInstaller.IsInstalled(h.ServerOptions));
        var tool = Assert.Single((await h.ListToolsAsync()).Tools);
        Assert.Equal("echo", tool.Name);
        Assert.False(HasContext(tool));
        var result = await h.CallToolAsync("echo", """{"msg":"hi"}""");
        Assert.Equal("echo:hi", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    private async Task AssertNothingSentOrWrittenAsync(AgentCatRuntime runtime)
    {
        var flushed = runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound);
        await FakeAgentCatApi.WithinAsync(flushed, FakeAgentCatApi.WaitBound);
        Assert.True(await flushed, "The event pipeline did not drain.");
        Assert.Empty(Sender.Sent);
        Assert.Empty(Lines);
        Assert.False(File.Exists(LogPath), "AgentCat created its log file.");
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task Test_environment_installs_nothing(string entryPoint)
    {
        var runtime = TestRun();
        await using var h = EchoServer();

        await StartAsync(h, entryPoint, runtime);

        await AssertUntrackedAsync(h);
        await AssertNothingSentOrWrittenAsync(runtime);
        Assert.True(EnabledGate.SkippedAnInstall);
    }

    [Fact]
    public async Task Custom_event_in_a_test_run_validates_then_does_nothing()
    {
        var runtime = TestRun();
        var client = new AgentCatClient(runtime);
        await using var h = EchoServer();
        await h.StartAsync();
        var data = new CustomEventData { ResourceName = "custom-action", SessionId = "ses_custom000000000000000000000" };

        Assert.Equal(ProjectRequired, (await Assert.ThrowsAsync<ArgumentException>(() => client.PublishCustomEventAsync("ses_1", ""))).Message);
        Assert.Equal(ProjectRequired, (await Assert.ThrowsAsync<ArgumentException>(() => client.PublishCustomEventAsync(h.Server, null!))).Message);
        Assert.Equal(FirstParameter, (await Assert.ThrowsAsync<ArgumentException>(() => client.PublishCustomEventAsync((string)null!, "proj_test"))).Message);
        Assert.Equal(FirstParameter, (await Assert.ThrowsAsync<ArgumentException>(() => client.PublishCustomEventAsync((McpServer)null!, "proj_test"))).Message);

        var stringForm = client.PublishCustomEventAsync("ses_1", "proj_test", data);
        var serverForm = client.PublishCustomEventAsync(h.Server, "proj_test", data);

        Assert.Equal(TaskStatus.RanToCompletion, stringForm.Status);
        Assert.Equal(TaskStatus.RanToCompletion, serverForm.Status);
        await AssertNothingSentOrWrittenAsync(runtime);
        Assert.False(EnabledGate.SkippedAnInstall, "A custom event is not an install.");

        // The same untracked server with AgentCat on: the server form throws.
        var on = new AgentCatClient(NewRuntime(static _ => "true", static () => true));
        Assert.Equal(NotTracked, (await Assert.ThrowsAsync<ArgumentException>(() => on.PublishCustomEventAsync(h.Server, "proj_test", data))).Message);
    }

    [Fact]
    public async Task Kill_switch_false_turns_everything_off_in_production()
    {
        var asked = 0;
        bool Production()
        {
            Interlocked.Increment(ref asked);
            return false;
        }

        var runtime = NewRuntime(static name => name == "AGENTCAT_ENABLED" ? " False " : null, Production);
        var client = new AgentCatClient(runtime);

        await using var di = EchoServer();
        await StartAsync(di, "WithAgentCat", runtime);
        await using var track = EchoServer();
        await StartAsync(track, "Track", runtime);
        await client.PublishCustomEventAsync("ses_1", "proj_test");
        await client.PublishCustomEventAsync(di.Server, "proj_test");

        await AssertUntrackedAsync(di);
        await AssertUntrackedAsync(track);
        await AssertNothingSentOrWrittenAsync(runtime);
        Assert.Equal(0, Volatile.Read(ref asked));
    }

    [Fact]
    public void A_registration_stopped_by_the_gate_asks_again_until_one_passes()
    {
        var testRun = true;
        var runtime = NewRuntime(static _ => null, () => testRun);
        var configureCalls = 0;
        var post = new AgentCatPostConfigure(
            "proj_test",
            o =>
            {
                configureCalls++;
                o.EnableTracing = false;
            },
            () => runtime);

        var whileOff = new[] { new McpServerOptions(), new McpServerOptions() };
        Array.ForEach(whileOff, o => post.PostConfigure("", o));

        Assert.Equal(0, configureCalls);
        Assert.All(whileOff, o => Assert.False(EngineInstaller.IsInstalled(o)));
        Assert.Empty(Lines);

        testRun = false;
        var first = new McpServerOptions();
        post.PostConfigure("", first);

        Assert.Equal(1, configureCalls);
        Assert.True(EngineInstaller.TryGetTracker(first, out var tracker));
        Assert.False(tracker.Settings.EnableTracing);
        Assert.Collection(
            Lines,
            l => Assert.Contains("AgentCat setup started | project proj_test", l, StringComparison.Ordinal),
            l => Assert.Contains("AgentCat setup complete | project proj_test", l, StringComparison.Ordinal));

        // A decided registration never changes.
        testRun = true;
        var later = new McpServerOptions();
        post.PostConfigure("", later);

        Assert.True(EngineInstaller.TryGetTracker(later, out var laterTracker));
        Assert.Same(tracker, laterTracker);
        Assert.Equal(1, configureCalls);
        Assert.Equal(2, Lines.Length);
        Assert.All(whileOff, o => Assert.False(EngineInstaller.IsInstalled(o)));
    }

    [Fact]
    public void The_gate_comes_before_the_LogToHost_warning_and_the_host_logger()
    {
        var testRun = true;
        var runtime = NewRuntime(static _ => null, () => testRun);
        var host = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(host));
        var post = new AgentCatPostConfigure("proj_test", o => o.LogToHost = true, () => runtime, null, factory);
        var logToHost = new AgentCatOptions { LogToHost = true };

        AgentCatSdk.Track(new McpServerOptions(), "proj_test", logToHost, () => runtime);
        post.PostConfigure("", new McpServerOptions());

        Assert.Empty(Lines);
        Assert.Empty(host.Entries);
        Assert.False(File.Exists(LogPath));

        testRun = false;
        AgentCatSdk.Track(new McpServerOptions(), "proj_test", logToHost, () => runtime);
        post.PostConfigure("", new McpServerOptions());

        // The Track warning was not used up while off, and the host logger was adopted at the decision, not before.
        Assert.Single(Lines, l => l.EndsWith(" " + LogToHostWarning, StringComparison.Ordinal));
        Assert.Contains(File.ReadAllLines(LogPath), l => l.EndsWith(" " + LogToHostWarning, StringComparison.Ordinal));
        Assert.Collection(
            host.Entries,
            e => Assert.StartsWith($"AgentCat setup started | project proj_test | sdk v{McpHarness.McpMajor}/di |", e.Message, StringComparison.Ordinal),
            e => Assert.StartsWith("AgentCat setup complete | project proj_test |", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Failures_write_nothing_in_a_test_run()
    {
        var runtime = TestRun();

        Assert.Null(AgentCatSdk.Track(null, "proj_test", null, () => runtime));
        Assert.False(EnabledGate.SkippedAnInstall, "Track(null) has no server to install on.");

        var services = new ServiceCollection();
        var builder = services.AddMcpServer();
        services.MakeReadOnly();
        Assert.Same(builder, AgentCatMcpServerBuilderExtensions.WithAgentCat(builder, "proj_test", null, () => runtime));

        var noRuntime = new AgentCatPostConfigure("proj_test", null, () => throw new InvalidOperationException("no runtime"), () => runtime);
        noRuntime.PostConfigure("", new McpServerOptions());

        Assert.Empty(Lines);
        Assert.False(File.Exists(LogPath));
    }
}
