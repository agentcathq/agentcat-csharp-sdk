#pragma warning disable CA1861
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.PublicApi;

public sealed class ExportersOptionTests
{
    internal const string NoProjectWarning = "Warning: No projectId provided and no exporters configured. Events will not be sent anywhere.";

    private const string AlreadyTracked = "[SESSION DEBUG] track() - Server already being tracked, skipping initialization";

    private static readonly Func<string, string?> NoEnvironment = _ => null;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Started(string project, string flavor = "options")
        => $"AgentCat setup started | project {project} | sdk v{McpHarness.McpMajor}/{flavor} | signals filters";

    internal static string Complete(string project, int exporters)
        => $"AgentCat setup complete | project {project} | tracing=true context=true reportMissing=true exporters={exporters}";

    private static AgentCatOptions With(params (string Name, AgentCatExporter? Config)[] exporters)
    {
        var options = new AgentCatOptions();
        foreach (var (name, config) in exporters)
        {
            options.Exporters[name] = config!;
        }

        return options;
    }

    private static OtlpExporter Otlp(string host = "otlp.test") => new("http://" + host);

    private static McpHarness Echo()
    {
        var h = new McpHarness();
        h.AddTool((string msg) => "echo:" + msg, "echo");
        return h;
    }

    private static async Task CallEchoAsync(McpHarness h, string intent)
    {
        var result = await h.CallToolAsync("echo", "{\"msg\":\"hi\",\"context\":\"" + intent + "\"}");
        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo:hi");
    }

    // One tools/call on a server built from these options, as stateless HTTP does for every POST.
    private static async Task CallEchoOnAsync(McpServerOptions options, string intent)
    {
        await using var h = new McpHarness();
        h.UseOptions(options);
        await h.StartAsync();
        await CallEchoAsync(h, intent);
    }

    private static ServiceProvider Provider(HostingRuntime rt, Action<IMcpServerBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(rt.Runtime);
        configure(services.AddMcpServer().WithTools(new[]
        {
            McpServerTool.Create((string msg) => "echo:" + msg, new McpServerToolCreateOptions { Name = "echo" }),
        }));
        return services.BuildServiceProvider();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true within the bound.");
            await Task.Delay(10, Ct);
        }
    }

    private static string[] SetupLines(IEnumerable<string> lines)
        => lines.Where(l => l.StartsWith("AgentCat setup", StringComparison.Ordinal) || l.StartsWith("Initialized", StringComparison.Ordinal) || l == NoProjectWarning).ToArray();

    [Fact]
    public void Exporters_starts_empty_and_keeps_insertion_order()
    {
        var options = new AgentCatOptions();
        var (a, b, c) = (Otlp("a"), Otlp("b"), Otlp("c"));
        var (a2, c2) = (Otlp("a2"), Otlp("c2"));

        Assert.Empty(options.Exporters);
        Assert.Same(options.Exporters, options.Exporters);
        Assert.False(options.Exporters.IsReadOnly);

        options.Exporters["c"] = c;
        options.Exporters.Add("a", a);
        options.Exporters["b"] = b;
        Assert.True(options.Exporters.Remove("a"));
        options.Exporters["a"] = a2;
        options.Exporters["c"] = c2;
        options.Exporters["C"] = c;

        Assert.Equal(new[] { "c", "b", "a", "C" }, options.Exporters.Keys);
        Assert.Equal(new AgentCatExporter[] { c2, b, a2, c }, options.Exporters.Values);
        Assert.Equal(new[] { "c", "b", "a", "C" }, options.Exporters.Select(p => p.Key));
        Assert.True(options.Exporters.TryGetValue("b", out var found));
        Assert.Same(b, found);
        Assert.False(options.Exporters.ContainsKey("B"));
        Assert.Throws<ArgumentException>(() => options.Exporters.Add("b", a));

        options.Exporters["none"] = null!;
        Assert.True(options.Exporters.ContainsKey("none"));
        Assert.Null(options.Exporters["none"]);

        var initialized = new AgentCatOptions { Exporters = { ["otlp"] = Otlp(), ["dd"] = new DatadogExporter("k", "datadoghq.com", "svc") } };
        Assert.Equal(new[] { "otlp", "dd" }, initialized.Exporters.Keys);
    }

    [Fact]
    public async Task Exports_follow_the_order_of_Exporters_after_a_removal()
    {
        using var rt = new HostingRuntime();
        var options = With(("c", Otlp("c")), ("a", Otlp("a")), ("b", Otlp("b")));
        options.Exporters.Remove("a");
        options.Exporters["a"] = Otlp("a");

        await using var h = Echo();
        h.Configure(o => AgentCatSdk.Track(o, "proj_a", options, () => rt.Runtime));
        await h.StartAsync();
        await CallEchoAsync(h, "order");
        await rt.FlushAsync();

        Assert.Equal(new[] { "c", "b", "a" }, rt.Exporters.Built.Select(x => x.Name));
        Assert.Equal(new[] { "c", "b", "a" }, rt.Exporters.Exports.Select(x => x.Name));
    }

    // Review focus: a stateless server whose first registration has no exporters, and a later registration that has
    // them. The empty Exporters is "not configured", so the later one installs, and the manager serves both servers.
    [Fact]
    public async Task An_empty_Exporters_does_not_claim_the_slot()
    {
        using var rt = new HostingRuntime();
        await using var first = Provider(rt, b => b.WithAgentCat(""));
        await using var second = Provider(rt, b => b.WithAgentCat("", o => o.Exporters["otlp"] = Otlp()));

        var firstOptions = first.GetRequiredService<IOptionsFactory<McpServerOptions>>().Create("");
        Assert.False(rt.Runtime.ExportersClaimed);
        var secondOptions = second.GetRequiredService<IOptionsFactory<McpServerOptions>>().Create("");
        Assert.True(rt.Runtime.ExportersClaimed);

        Assert.Equal(
            new[]
            {
                Started("(telemetry-only)", "di"),
                NoProjectWarning,
                Complete("(telemetry-only)", 0),
                Started("(telemetry-only)", "di"),
                "Initialized telemetry exporter: otlp",
                "Initialized telemetry with 1 exporters",
                Complete("(telemetry-only)", 1),
            },
            rt.Log.Lines);

        await CallEchoOnAsync(firstOptions, "first");
        await CallEchoOnAsync(secondOptions, "second");
        await rt.FlushAsync();

        Assert.Equal(new[] { "first", "second" }, rt.Exporters.Exports.Select(x => x.Event.UserIntent).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(rt.Exporters.Exports, x => Assert.Equal("otlp", x.Name));
        Assert.Empty(rt.Sender.Sent);
    }

    // PHP testOnlyTheFirstExportersReceiveEvents, without the WARN line: TS ignores a later set silently.
    [Fact]
    public async Task Only_the_first_configured_exporters_receive_events()
    {
        using var rt = new HostingRuntime();
        await using var first = Echo();
        await using var second = Echo();
        first.Configure(o => AgentCatSdk.Track(o, "proj_a", With(("a", Otlp("a.test"))), () => rt.Runtime));
        second.Configure(o => AgentCatSdk.Track(o, "proj_b", With(("b", Otlp("b.test"))), () => rt.Runtime));
        await first.StartAsync();
        await second.StartAsync();

        await CallEchoAsync(first, "first");
        await CallEchoAsync(second, "second");
        await rt.FlushAsync();

        Assert.Equal(new[] { "a" }, rt.Exporters.Built.Select(x => x.Name));
        Assert.Equal(new[] { "first", "second" }, rt.Exporters.Exports.Select(x => x.Event.UserIntent).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(rt.Exporters.Exports, x => Assert.Equal("a", x.Name));
        Assert.Equal(
            new[]
            {
                Started("proj_a"),
                "Initialized telemetry exporter: a",
                "Initialized telemetry with 1 exporters",
                Complete("proj_a", 1),
                Started("proj_b"),
                Complete("proj_b", 1),
            },
            SetupLines(rt.Log.Lines));
        Assert.Equal(2, rt.Sender.Sent.Count);
    }

    // PHP testPerRequestBuildersWithTheSameExportersShareOneManager, on stateless HTTP: every POST builds its own
    // server options from one WithAgentCat registration.
    [Fact]
    public async Task Per_request_options_share_one_manager()
    {
        using var rt = new HostingRuntime();
        await using var provider = Provider(rt, b => b.WithAgentCat("proj_test", o => o.Exporters["otlp"] = Otlp()));
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();

        for (var request = 0; request < 3; request++)
        {
            await CallEchoOnAsync(factory.Create(""), "request " + request);
        }

        await rt.FlushAsync();

        Assert.Single(rt.Exporters.Built);
        Assert.Equal(new[] { "request 0", "request 1", "request 2" }, rt.Exporters.Exports.Select(x => x.Event.UserIntent).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(
            new[] { Started("proj_test", "di"), "Initialized telemetry exporter: otlp", "Initialized telemetry with 1 exporters", Complete("proj_test", 1) },
            SetupLines(rt.Log.Lines));
    }

    // PHP testASecondTrackOfATrackedServerNeverTouchesTheExporters.
    [Fact]
    public async Task A_second_track_of_a_tracked_server_never_touches_exporters()
    {
        using var rt = new HostingRuntime();
        await using var h = Echo();
        h.Configure(o =>
        {
            AgentCatSdk.Track(o, "proj_a", null, () => rt.Runtime);
            AgentCatSdk.Track(o, "proj_a", With(("otlp", Otlp())), () => rt.Runtime);
        });
        await h.StartAsync();

        Assert.Equal(new[] { Started("proj_a"), Complete("proj_a", 0), Started("proj_a"), AlreadyTracked }, rt.Log.Lines);
        Assert.False(rt.Runtime.ExportersClaimed);

        await CallEchoAsync(h, "tracked once");
        await rt.FlushAsync();

        Assert.Empty(rt.Exporters.Built);
        Assert.Empty(rt.Exporters.Exports);
        Assert.Single(rt.Sender.Sent);
    }

    // PHP testNullExportersKeepTheNoProjectWarningAndSetNothingUp: a C# Exporters is never null; empty is "not
    // configured".
    [Fact]
    public void Without_project_or_exporters_the_warning_is_written()
    {
        using var rt = new HostingRuntime();

        Assert.NotNull(AgentCatEngine.Install(new McpServerOptions(), "", null, rt.Runtime, "options", NoEnvironment));
        Assert.NotNull(AgentCatEngine.Install(new McpServerOptions(), "", new AgentCatOptions(), rt.Runtime, "options", NoEnvironment));

        Assert.Equal(
            new[]
            {
                Started("(telemetry-only)"), NoProjectWarning, Complete("(telemetry-only)", 0),
                Started("(telemetry-only)"), NoProjectWarning, Complete("(telemetry-only)", 0),
            },
            rt.Log.Lines);
        Assert.False(rt.Runtime.ExportersClaimed);
    }

    // PHP testTelemetryOnlyTrackingExportsAndNeverCallsTheApi and testWithoutTheTelemetrySeamNoExportersAreSetUp: the
    // lines come in TS order, and configured exporters suppress the warning even where nothing is built.
    [Fact]
    public async Task Exporters_suppress_the_no_project_warning()
    {
        using var rt = new HostingRuntime();
        var log = new RecordingLog();
        var hostsNone = new AgentCatRuntime(log, new RecordingSender());
        try
        {
            await using var h = Echo();
            h.Configure(o => AgentCatSdk.Track(o, "", With(("otlp", Otlp())), () => rt.Runtime));
            await h.StartAsync();
            Assert.NotNull(AgentCatEngine.Install(new McpServerOptions(), "", With(("otlp", Otlp())), hostsNone, "options", NoEnvironment));

            Assert.Equal(
                new[] { Started("(telemetry-only)"), "Initialized telemetry exporter: otlp", "Initialized telemetry with 1 exporters", Complete("(telemetry-only)", 1) },
                rt.Log.Lines);
            Assert.Equal(new[] { Started("(telemetry-only)"), Complete("(telemetry-only)", 1) }, log.Lines);

            await CallEchoAsync(h, "telemetry only");
            await rt.FlushAsync();

            Assert.Equal("telemetry only", Assert.Single(rt.Exporters.Exports).Event.UserIntent);
            Assert.Empty(rt.Sender.Sent);
        }
        finally
        {
            hostsNone.Pipeline.Dispose();
        }
    }

    // TS counts the keys of this call's exporters, built or not, also on a call that lost first-wins.
    [Fact]
    public void The_setup_complete_line_counts_this_calls_exporters()
    {
        using var rt = new HostingRuntime();

        Assert.NotNull(AgentCatEngine.Install(new McpServerOptions(), "proj_a", With(("a", Otlp())), rt.Runtime, "options", NoEnvironment));
        Assert.NotNull(AgentCatEngine.Install(
            new McpServerOptions(),
            "proj_b",
            With(("x", Otlp()), ("y", new PostHogExporter("phc_x")), ("z", null)),
            rt.Runtime,
            "options",
            NoEnvironment));

        Assert.Equal(
            new[]
            {
                Started("proj_a"), "Initialized telemetry exporter: a", "Initialized telemetry with 1 exporters", Complete("proj_a", 1),
                Started("proj_b"), Complete("proj_b", 3),
            },
            rt.Log.Lines);
    }

    // TS sets the manager before installEngine, so a failed install leaves the exporters in place.
    [Fact]
    public async Task Exporters_set_up_before_a_failed_engine_install_stay()
    {
        using var rt = new HostingRuntime();
        var readOnly = new McpServerOptions();
        readOnly.Filters.Request.CallToolFilters = new ReadOnlyCollection<McpRequestFilter<CallToolRequestParams, CallToolResult>>(
            new List<McpRequestFilter<CallToolRequestParams, CallToolResult>>());

        Assert.Null(AgentCatEngine.Install(readOnly, "", With(("otlp", Otlp())), rt.Runtime, "options", NoEnvironment));

        var lines = rt.Log.Lines;
        Assert.Equal(new[] { Started("(telemetry-only)"), "Initialized telemetry exporter: otlp", "Initialized telemetry with 1 exporters" }, lines.Take(3));
        Assert.StartsWith("Warning: Failed to track server - NotSupportedException: ", Assert.Single(lines.Skip(3)), StringComparison.Ordinal);

        await using var h = Echo();
        h.Configure(o => AgentCatSdk.Track(o, "proj_b", null, () => rt.Runtime));
        await h.StartAsync();
        await CallEchoAsync(h, "after the failure");
        await rt.FlushAsync();

        Assert.Equal("after the failure", Assert.Single(rt.Exporters.Exports).Event.UserIntent);
    }

    [Fact]
    public void Concurrent_first_installs_build_one_manager()
    {
        const int Threads = 8;
        using var rt = new HostingRuntime();
        rt.Exporters.OnBuild = (_, _, _) => Thread.Sleep(20);
        using var start = new Barrier(Threads);
        var failures = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, Threads).Select(i => new Thread(() =>
        {
            try
            {
                start.SignalAndWait(FakeAgentCatApi.WaitBound);
                AgentCatEngine.Install(new McpServerOptions(), "proj_" + i, With(("e" + i, Otlp())), rt.Runtime, "options", NoEnvironment);
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        })).ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(FakeAgentCatApi.WaitBound));
        }

        Assert.Empty(failures);
        var built = Assert.Single(rt.Exporters.Built);
        var lines = rt.Log.Lines;
        Assert.Single(lines, l => l.StartsWith("Initialized telemetry exporter: ", StringComparison.Ordinal));
        Assert.Single(lines, l => l == "Initialized telemetry with 1 exporters");
        Assert.Contains("Initialized telemetry exporter: " + built.Name, lines);
        Assert.Equal(Threads, lines.Count(l => l.StartsWith("AgentCat setup complete", StringComparison.Ordinal) && l.EndsWith(" exporters=1", StringComparison.Ordinal)));
    }

    // PHP testExportsRunOncePerApiAttempt (TS B-2): three failed attempts and the last one give four exports of the
    // same event.
    [Fact]
    public async Task Exports_run_once_per_api_attempt()
    {
        using var rt = new HostingRuntime(new ManualClock());
        rt.Sender.Fail("HTTP 503", times: 4);
        await using var h = Echo();
        h.Configure(o => AgentCatSdk.Track(o, "proj_a", With(("otlp", Otlp())), () => rt.Runtime));
        await h.StartAsync();

        await CallEchoAsync(h, "retried");

        // The clock ends the retry waits at once, and would end a flush budget at once too.
        await WaitUntilAsync(() => rt.Log.Lines.Contains("Failed to deliver AgentCat event after retries: HTTP 503"));

        var exports = rt.Exporters.Exports;
        Assert.Equal(4, exports.Count);
        Assert.All(exports, x => Assert.Same(exports[0].Event, x.Event));
        Assert.Equal(4, rt.Sender.Calls);
        Assert.Equal(4, rt.Log.Lines.Count(l => l.StartsWith("Failed to send event " + exports[0].Event.Id + ", retrying...", StringComparison.Ordinal)));
    }

    // PHP testAnEventDroppedByRedactEventIsNotExported.
    [Fact]
    public async Task A_dropped_event_is_not_exported()
    {
        using var rt = new HostingRuntime();
        var options = With(("otlp", Otlp()));
        options.RedactEvent = static (_, _) => new ValueTask<AgentCatEvent?>((AgentCatEvent?)null);
        await using var h = Echo();
        h.Configure(o => AgentCatSdk.Track(o, "proj_a", options, () => rt.Runtime));
        await h.StartAsync();

        await CallEchoAsync(h, "dropped");
        await rt.FlushAsync();

        Assert.Single(rt.Exporters.Built);
        Assert.Empty(rt.Exporters.Exports);
        Assert.Empty(rt.Sender.Sent);
        Assert.Contains("Event dropped by redactEvent hook", rt.Log.Lines);
    }

    // A runtime that hosts exporters through the test builder.
    internal sealed class HostingRuntime : IDisposable
    {
        internal HostingRuntime(IPipelineClock? clock = null)
            => Runtime = new AgentCatRuntime(Log, Sender, clock: clock, exporterBuilder: Exporters.Build);

        internal RecordingLog Log { get; } = new();

        internal RecordingSender Sender { get; } = new();

        internal RecordingExporters Exporters { get; } = new();

        internal AgentCatRuntime Runtime { get; }

        internal async Task FlushAsync()
        {
            var flushed = Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, Ct);
            await FakeAgentCatApi.WithinAsync(flushed, FakeAgentCatApi.WaitBound);
            Assert.True(await flushed, "The event pipeline did not drain.");
        }

        public void Dispose() => Runtime.Pipeline.Dispose();
    }
}

// A fake is process-wide, so these tests run alone.
[Collection(SharedRuntime.Name)]
public sealed class ExportersUnderAFakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_shared_runtime_hosts_exporters()
    {
        Assert.True(AgentCatRuntime.Shared.HostsExporters);
    }

    // PHP: fake() sets up no exporters. The install still counts its entries and skips the no-project warning.
    [Theory]
    [InlineData("Track")]
    [InlineData("WithAgentCat")]
    public async Task An_install_under_a_fake_builds_no_exporters(string entryPoint)
    {
        using var fake = AgentCatSdk.Fake();
        await using var h = new McpHarness();
        h.AddTool((string msg) => "echo:" + msg, "echo");
        if (entryPoint == "Track")
        {
            var options = new AgentCatOptions { Exporters = { ["otlp"] = new OtlpExporter("http://otlp.invalid") } };
            h.Configure(o => AgentCatSdk.Track(o, "", options));
            await h.StartAsync();
        }
        else
        {
            h.ConfigureBuilder(b => b.WithAgentCat("", o => o.Exporters["otlp"] = new OtlpExporter("http://otlp.invalid")));
            await h.StartAsync(null, withServices: true);
        }

        await h.CallToolAsync("echo", """{"msg":"hi","context":"under a fake"}""");
        await fake.AssertTrackedAsync(e => e.UserIntent == "under a fake", Ct);

        var logs = await fake.GetLogsAsync(Ct);
        Assert.DoesNotContain(logs, l => l.StartsWith("Initialized", StringComparison.Ordinal));
        Assert.DoesNotContain(ExportersOptionTests.NoProjectWarning, logs);
        Assert.Contains(ExportersOptionTests.Complete("(telemetry-only)", 1), logs);
        Assert.False(fake.Runtime.HostsExporters);
        Assert.False(fake.Runtime.ExportersClaimed);
        Assert.False(AgentCatRuntime.Shared.ExportersClaimed);
    }
}
