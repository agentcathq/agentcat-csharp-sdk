using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.PublicApi;

public class TrackTests
{
    private const string FailedToTrack = "Warning: Failed to track server - ";

    private const string AlreadyTracked = "[SESSION DEBUG] track() - Server already being tracked, skipping initialization";

    private const string NoProjectWarning = "Warning: No projectId provided and no exporters configured. Events will not be sent anywhere.";

    private const string AlternateHandlerWarning =
        "Warning: AgentCat did not instrument this server: Handlers.CallToolWithAlternateHandler is set, and McpServer.Create rejects tools/call filters with it.";

    private static readonly Func<string, string?> NoEnvironment = _ => null;

    public static TheoryData<string?> Protocols => McpHarness.ProtocolVersions;

    private static string Started(string project)
        => $"AgentCat setup started | project {project} | sdk v{McpHarness.McpMajor}/options | signals filters";

    private static string Complete(string project)
        => $"AgentCat setup complete | project {project} | tracing=true context=true reportMissing=true exporters=0";

    [Fact]
    public void Track_returns_the_same_options()
    {
        var runtime = new AgentCatRuntime(new RecordingLog(), new RecordingSender());
        var options = new McpServerOptions();

        Assert.Same(options, AgentCatSdk.Track(options, "proj_a", null, () => runtime));
        Assert.Same(options, AgentCatSdk.Track(options, "proj_b", null, () => runtime));

        Assert.True(EngineInstaller.TryGetTracker(options, out var tracker));
        Assert.Equal("proj_a", tracker!.ProjectId);

        var readOnly = new McpServerOptions();
        readOnly.Filters.Request.CallToolFilters = new ReadOnlyCollection<McpRequestFilter<CallToolRequestParams, CallToolResult>>(
            new List<McpRequestFilter<CallToolRequestParams, CallToolResult>>());
        Assert.Same(readOnly, AgentCatSdk.Track(readOnly, "proj_a", null, () => runtime));
        Assert.False(EngineInstaller.IsInstalled(readOnly));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Repeated_track_logs_started_and_the_skip_line_and_applies_the_base_url(string? protocolVersion)
    {
        using var firstApi = new FakeAgentCatApi();
        using var secondApi = new FakeAgentCatApi();
        using var http = new HttpRuntime();
        await using var h = new McpHarness();
        h.AddTool((string msg) => "echo:" + msg, "echo");
        h.Configure(o =>
        {
            AgentCatSdk.Track(o, "proj_first", new AgentCatOptions { ApiBaseUrl = firstApi.BaseUrl }, () => http.Runtime);
            AgentCatSdk.Track(o, "proj_second", new AgentCatOptions { ApiBaseUrl = secondApi.BaseUrl, EnableTracing = false }, () => http.Runtime);
        });

        await h.StartAsync(protocolVersion);

        Assert.Equal(new[] { Started("proj_first"), Complete("proj_first"), Started("proj_second"), AlreadyTracked }, http.Log.Lines);
        Assert.Equal(secondApi.BaseUrl + "/v1/public/events/", http.Sender.Endpoint);
        Assert.True(EngineInstaller.TryGetTracker(h.ServerOptions, out var tracker));
        Assert.Equal("proj_first", tracker!.ProjectId);
        Assert.Single(h.ServerOptions.Filters.Request.ListToolsFilters);
        Assert.Equal(2, h.ServerOptions.Filters.Request.CallToolFilters.Count);
        Assert.Single(h.ServerOptions.Filters.Message.OutgoingFilters);

        var tools = (await h.ListToolsAsync()).Tools;
        Assert.Equal(1, tools.Count(t => t.Name == "get_more_tools"));
        var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"double-track"}""");
        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo:hi");
        Assert.Single(result.Content, c => c is TextContentBlock t && t.Text.StartsWith("[session_id", StringComparison.Ordinal));

        var request = Assert.Single(await secondApi.WaitForRequestsAsync(1));
        Assert.True(await http.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Single(secondApi.Requests);
        Assert.Empty(firstApi.Requests);
        var body = Body(request);
        Assert.Equal("proj_first", (string?)body["project_id"]);
        Assert.Equal("echo", (string?)body["resource_name"]);
        Assert.Equal("double-track", (string?)body["user_intent"]);
    }

    [Fact]
    public void Track_returns_the_options_when_the_shared_runtime_cannot_be_built()
    {
        var options = new McpServerOptions();
        McpServerOptions? returned = null;
        McpServerOptions? returnedForNull = new();

        var thrown = Record.Exception(() =>
        {
            returned = AgentCatSdk.Track(options, "proj_a", null, () => throw new InvalidOperationException("no runtime"));
            returnedForNull = AgentCatSdk.Track(null, "proj_a", null, () => throw new InvalidOperationException("no runtime"));
        });

        Assert.Null(thrown);
        Assert.Same(options, returned);
        Assert.Null(returnedForNull);
        Assert.False(EngineInstaller.IsInstalled(options));
        Assert.Empty(options.Filters.Request.CallToolFilters);
    }

    // A second install that runs while the first is between its setup lines, as a concurrent caller can.
    [Theory]
    [InlineData("AgentCat setup started")]
    [InlineData("Warning: No projectId provided")]
    public void An_interleaved_track_installs_once_and_the_other_call_logs_like_a_repeated_track(string interleaveAfter)
    {
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender());
        var options = new McpServerOptions();
        AgentCatTracker? interleaved = null;
        var fired = false;
        log.AfterWrite = line =>
        {
            if (!fired && line.StartsWith(interleaveAfter, StringComparison.Ordinal))
            {
                fired = true;
                interleaved = AgentCatEngine.Install(options, "", null, runtime, "options", NoEnvironment);
            }
        };

        var first = AgentCatEngine.Install(options, "", null, runtime, "options", NoEnvironment);

        Assert.True(fired);
        Assert.True(first is null ^ interleaved is null);
        Assert.True(EngineInstaller.TryGetTracker(options, out var installed));
        Assert.Same(installed, first ?? interleaved);
        Assert.Single(options.Filters.Request.ListToolsFilters);
        Assert.Equal(2, options.Filters.Request.CallToolFilters.Count);
        var lines = log.Lines;
        Assert.Equal(5, lines.Count);
        Assert.Equal(2, lines.Count(l => l == Started("(telemetry-only)")));
        Assert.Single(lines, l => l == NoProjectWarning);
        Assert.Single(lines, l => l == AlreadyTracked);
        Assert.Single(lines, l => l == Complete("(telemetry-only)"));
    }

    [Fact]
    public void Failed_install_logs_started_then_the_failure_and_no_complete_line()
    {
        var readOnly = new ReadOnlyCollection<McpRequestFilter<CallToolRequestParams, CallToolResult>>(
            new List<McpRequestFilter<CallToolRequestParams, CallToolResult>>());
        var insert = Assert.Throws<NotSupportedException>(
            () => ((IList<McpRequestFilter<CallToolRequestParams, CallToolResult>>)readOnly).Insert(0, next => next));
        var failure = FailedToTrack + "NotSupportedException: " + insert.Message;

        string[] Install(string projectId)
        {
            var log = new RecordingLog();
            var runtime = new AgentCatRuntime(log, new RecordingSender());
            var options = new McpServerOptions();
            options.Filters.Request.CallToolFilters = readOnly;

            Assert.Null(AgentCatEngine.Install(options, projectId, null, runtime, "options", NoEnvironment));
            Assert.False(EngineInstaller.IsInstalled(options));
            Assert.Empty(options.Filters.Request.ListToolsFilters);
            Assert.Empty(options.Filters.Message.OutgoingFilters);
            return log.Lines.ToArray();
        }

        Assert.Equal(new[] { Started("proj_a"), failure }, Install("proj_a"));
        Assert.Equal(new[] { Started("(telemetry-only)"), NoProjectWarning, failure }, Install(""));
    }

    [Fact]
    public void A_call_that_resolves_no_base_url_keeps_the_current_one()
    {
        using var http = new HttpRuntime();
        var tracked = new McpServerOptions();

        Assert.NotNull(AgentCatEngine.Install(tracked, "proj_a", new AgentCatOptions { ApiBaseUrl = "http://one.test" }, http.Runtime, "options", NoEnvironment));
        Assert.Equal("http://one.test/v1/public/events/", http.Sender.Endpoint);

        Assert.NotNull(AgentCatEngine.Install(new McpServerOptions(), "proj_b", new AgentCatOptions { ApiBaseUrl = "" }, http.Runtime, "options", NoEnvironment));
        Assert.Equal("http://one.test/v1/public/events/", http.Sender.Endpoint);

        Assert.Null(AgentCatEngine.Install(tracked, "proj_a", null, http.Runtime, "options", NoEnvironment));
        Assert.Equal("http://one.test/v1/public/events/", http.Sender.Endpoint);

        Assert.Null(AgentCatEngine.Install(tracked, "proj_a", null, http.Runtime, "options", name => name == "AGENTCAT_API_URL" ? "http://env.test" : null));
        Assert.Equal("http://env.test/v1/public/events/", http.Sender.Endpoint);
    }

    [Fact]
    public void Track_null_options_logs_and_returns_null()
    {
        using var http = new HttpRuntime();
        var initial = http.Sender.Endpoint;

        McpServerOptions? returned = new();
        var thrown = Record.Exception(() => returned = AgentCatSdk.Track(null, "proj_a", new AgentCatOptions { ApiBaseUrl = "http://one.test" }, () => http.Runtime));

        Assert.Null(thrown);
        Assert.Null(returned);
        var line = Assert.Single(http.Log.Lines);
        Assert.StartsWith(FailedToTrack + "ArgumentNullException: ", line, StringComparison.Ordinal);
        Assert.Equal(initial, http.Sender.Endpoint);
    }

    [Fact]
    public void Alternate_handler_logs_started_then_the_warning_and_no_complete_line()
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "Handlers.CallToolWithAlternateHandler exists only on ModelContextProtocol 2.x.");
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender());
        var options = new McpServerOptions();
        Mcp.SetAlternateHandler(options);

        Assert.True(EngineInstaller.HasAlternateHandler(options));
        Assert.Null(AgentCatEngine.Install(options, "", null, runtime, "options", NoEnvironment));

        Assert.Equal(new[] { Started("(telemetry-only)"), AlternateHandlerWarning }, log.Lines);
        Assert.False(EngineInstaller.IsInstalled(options));
        Assert.Empty(options.Filters.Request.ListToolsFilters);
        Assert.Empty(options.Filters.Request.CallToolFilters);
        Assert.Empty(options.Filters.Message.OutgoingFilters);
    }

    [Fact]
    public void Failed_setup_installs_nothing()
    {
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender());
        Func<string, string?> unreadable = _ => throw new InvalidOperationException("env boom");
        var failed = AgentCatTracker.Create("proj_a", null, runtime, "options", unreadable);
        var options = new McpServerOptions();

        Assert.True(failed.SetupFailed);
        Assert.False(EngineInstaller.Install(options, failed));
        Assert.Null(AgentCatEngine.Install(options, "proj_a", null, runtime, "options", unreadable));

        Assert.False(EngineInstaller.IsInstalled(options));
        Assert.Empty(options.Filters.Request.ListToolsFilters);
        Assert.Empty(options.Filters.Request.CallToolFilters);
        Assert.Empty(options.Filters.Message.OutgoingFilters);
        Assert.Equal(2, log.Lines.Count);
        Assert.All(log.Lines, l => Assert.Equal(FailedToTrack + "InvalidOperationException: env boom", l));
    }

    [Fact]
    public void Install_reuses_the_seam_delegates_per_tracker()
    {
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender());
        var tracker = AgentCatTracker.Create("proj_a", null, runtime, "options", NoEnvironment);
        var other = AgentCatTracker.Create("proj_b", null, runtime, "options", NoEnvironment);
        var first = new McpServerOptions();
        var second = new McpServerOptions();

        Assert.True(EngineInstaller.Install(first, tracker));
        Assert.True(EngineInstaller.Install(second, tracker));

        foreach (var options in new[] { first, second })
        {
            Assert.Same(tracker.Seams.List, Assert.Single(options.Filters.Request.ListToolsFilters));
            Assert.Collection(
                options.Filters.Request.CallToolFilters,
                f => Assert.Same(tracker.Seams.Outer, f),
                f => Assert.Same(tracker.Seams.Inner, f));
            Assert.Same(tracker.Seams.Outgoing, Assert.Single(options.Filters.Message.OutgoingFilters));
            Assert.True(EngineInstaller.TryGetTracker(options, out var found));
            Assert.Same(tracker, found);
        }

        Assert.Same(tracker, Assert.IsType<ListToolsSeam>(tracker.Seams.List.Target).Tracker);
        Assert.Same(tracker, Assert.IsType<CallToolSeam>(tracker.Seams.Outer.Target).Tracker);
        Assert.Same(tracker, Assert.IsType<InnerTap>(tracker.Seams.Inner.Target).Tracker);
        Assert.Same(tracker, Assert.IsType<OutgoingSeam>(tracker.Seams.Outgoing.Target).Tracker);
        Assert.NotSame(tracker.Seams.Outer, other.Seams.Outer);

        var readOnly = new McpServerOptions();
        readOnly.Filters.Message.OutgoingFilters = new ReadOnlyCollection<McpMessageFilter>(new List<McpMessageFilter>());
        Assert.False(EngineInstaller.Install(readOnly, tracker));
        Assert.Empty(readOnly.Filters.Request.ListToolsFilters);
        Assert.Empty(readOnly.Filters.Request.CallToolFilters);
        Assert.False(EngineInstaller.IsInstalled(readOnly));
        Assert.StartsWith(FailedToTrack + "NotSupportedException: ", Assert.Single(log.Lines), StringComparison.Ordinal);

        Assert.True(EngineInstaller.Install(new McpServerOptions(), tracker));
    }

    [Fact]
    public void AgentCatSdk_Track_is_public_static()
    {
        var type = typeof(AgentCatSdk);
        Assert.True(type.IsPublic);
        Assert.True(type.IsAbstract && type.IsSealed);

        var track = type.GetMethod(
            "Track",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(McpServerOptions), typeof(string), typeof(AgentCatOptions) },
            null);

        Assert.NotNull(track);
        Assert.Equal(typeof(McpServerOptions), track!.ReturnType);
        Assert.All(track.GetParameters().Skip(1), p => Assert.True(p.IsOptional));
    }

    private static JsonObject Body(RecordedRequest request)
        => JsonNode.Parse(Encoding.UTF8.GetString(request.Body))!.AsObject();

    private sealed class HttpRuntime : IDisposable
    {
        internal HttpRuntime() => Runtime = new AgentCatRuntime(Log, Sender);

        internal RecordingLog Log { get; } = new();

        internal HttpEventSender Sender { get; } = new();

        internal AgentCatRuntime Runtime { get; }

        public void Dispose()
        {
            Runtime.Pipeline.Dispose();
            Sender.Dispose();
        }
    }
}

[Collection(SharedRuntime.Name)]
public class SharedRuntimeTrackTests
{
    [Fact]
    public async Task Public_Track_before_Create_tracks_end_to_end()
    {
        using var api = new FakeAgentCatApi();
        McpServerOptions? configured = null;
        McpServerOptions? returned = null;
        await using var h = new McpHarness();
        h.AddTool((string text) => "echo:" + text, "echo");
        h.Configure(o =>
        {
            configured = o;
            returned = AgentCatSdk.Track(o, "proj_test", new AgentCatOptions { ApiBaseUrl = api.BaseUrl });
        });
        await h.StartAsync();

        Assert.NotNull(configured);
        Assert.Same(configured, returned);
        var result = await h.CallToolAsync("echo", """{"text":"hi","context":"public track"}""");
        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo:hi");

        var request = Assert.Single(await api.WaitForRequestsAsync(1));
        Assert.True(await AgentCatRuntime.Shared.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Single(api.Requests);
        Assert.Equal("/v1/public/events/", request.Path);
        var body = JsonNode.Parse(Encoding.UTF8.GetString(request.Body))!.AsObject();
        Assert.Equal("proj_test", (string?)body["project_id"]);
        Assert.Equal("mcp:tools/call", (string?)body["event_type"]);
        Assert.Equal("echo", (string?)body["resource_name"]);
        Assert.Equal("public track", (string?)body["user_intent"]);
    }

    [Fact]
    public void Public_Track_of_null_returns_null_without_throwing()
    {
        McpServerOptions? returned = new();

        var thrown = Record.Exception(() => returned = AgentCatSdk.Track(null));

        Assert.Null(thrown);
        Assert.Null(returned);
    }
}
