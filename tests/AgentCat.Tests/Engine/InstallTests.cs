using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Engine;

public class InstallTests
{
    private const string AlternateHandlerWarning =
        "Warning: AgentCat did not instrument this server: Handlers.CallToolWithAlternateHandler is set, and McpServer.Create rejects tools/call filters with it.";

    private const string FailedToTrack = "Warning: Failed to track server - ";

    public static TheoryData<string?> Protocols => McpHarness.ProtocolVersions;

    public static TheoryData<string?, bool> Startups => McpHarness.Startups;

    [Theory]
    [MemberData(nameof(Startups))]
    public async Task Filters_land_outermost_and_innermost(string? protocolVersion, bool withServices)
    {
        var order = new List<string>();
        McpRequestFilter<ListToolsRequestParams, ListToolsResult> userList = next => next;
        var userInsert = Recording(order, "user Insert(0)");
        var userAdd = Recording(order, "user Add");
        McpMessageFilter userOutgoing = next => next;
        await using var h = new EngineHarness();
        h.AddTool((string text) =>
        {
            order.Add("tool");
            return "echo:" + text;
        }, "echo");
        h.Configure(o =>
        {
            o.Filters.Request.ListToolsFilters.Add(userList);
            o.Filters.Request.CallToolFilters.Insert(0, userInsert);
            o.Filters.Request.CallToolFilters.Add(userAdd);
            o.Filters.Message.OutgoingFilters.Add(userOutgoing);
        });

        await h.StartAsync(protocolVersion, withServices);

        var tracker = Assert.IsType<AgentCatTracker>(h.Tracker);
        var request = h.ServerOptions.Filters.Request;
        Assert.Collection(
            request.ListToolsFilters,
            f => Assert.Same(tracker, Assert.IsType<ListToolsSeam>(f.Target).Tracker),
            f => Assert.Same(userList, f));
        Assert.Collection(
            request.CallToolFilters,
            f => Assert.Same(tracker, Assert.IsType<CallToolSeam>(f.Target).Tracker),
            f => Assert.Same(userInsert, f),
            f => Assert.Same(userAdd, f),
            f => Assert.Same(tracker, Assert.IsType<InnerTap>(f.Target).Tracker));
        Assert.Collection(
            h.ServerOptions.Filters.Message.OutgoingFilters,
            f => Assert.Same(tracker, Assert.IsType<OutgoingSeam>(f.Target).Tracker),
            f => Assert.Same(userOutgoing, f));

        var result = await h.CallToolAsync("echo", """{"text":"hi"}""");

        Assert.Equal("echo:hi", LastText(result));
        Assert.Equal("user Insert(0) > user Add > tool", string.Join(" > ", order));
    }

    [Fact]
    public void Install_is_idempotent_per_options()
    {
        var runtime = new AgentCatRuntime(new RecordingLog(), new RecordingSender());
        var options = new McpServerOptions();
        var first = AgentCatTracker.Create("proj_a", null, runtime, "options", _ => null);
        var second = AgentCatTracker.Create("proj_b", null, runtime, "options", _ => null);

        Assert.True(EngineInstaller.Install(options, first));
        Assert.False(EngineInstaller.Install(options, first));
        Assert.False(EngineInstaller.Install(options, second));
        Assert.Null(AgentCatEngine.Install(options, "proj_c", null, runtime, "options"));

        AssertSeamsOnce(options, first);

        var other = new McpServerOptions();
        Assert.True(EngineInstaller.Install(other, second));
        AssertSeamsOnce(other, second);
    }

    [Fact]
    public async Task Alternate_handler_skips_install_and_warns()
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "Handlers.CallToolWithAlternateHandler exists only on ModelContextProtocol 2.x.");
        await using var h = new EngineHarness();
        h.AddTool((string text) => "echo:" + text, "echo");
        h.Configure(Mcp.SetAlternateHandler);

        await h.StartAsync();

        Assert.Null(h.Tracker);
        Assert.Equal(1, h.Log.Lines.Count(l => l == AlternateHandlerWarning));
        Assert.Empty(h.ServerOptions.Filters.Request.ListToolsFilters);
        Assert.Empty(h.ServerOptions.Filters.Request.CallToolFilters);
        Assert.Empty(h.ServerOptions.Filters.Message.OutgoingFilters);
        Assert.Contains((await h.ListToolsAsync()).Tools, t => t.Name == "echo");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Install_before_Create_without_DI_works(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool((string text) => "echo:" + text, "echo");

        await h.StartAsync(protocolVersion);

        var tracker = Assert.IsType<AgentCatTracker>(h.Tracker);
        AssertSeamsOnce(h.ServerOptions, tracker);
        Assert.Contains($"AgentCat setup started | project proj_test | sdk v{McpHarness.McpMajor}/options | signals filters", h.Log.Lines);
        Assert.Contains((await h.ListToolsAsync()).Tools, t => t.Name == "echo");
        Assert.Equal("echo:hi", LastText(await h.CallToolAsync("echo", """{"text":"hi"}""")));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_failed_setup_installs_nothing(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.Log.ThrowWhen = line => line.StartsWith("AgentCat setup started", StringComparison.Ordinal);
        h.AddTool((string text) => "echo:" + text, "echo");

        await h.StartAsync(protocolVersion);

        Assert.Null(h.Tracker);
        Assert.Contains(FailedToTrack + "InvalidOperationException: log boom", h.Log.Lines);
        Assert.Empty(h.ServerOptions.Filters.Request.ListToolsFilters);
        Assert.Empty(h.ServerOptions.Filters.Request.CallToolFilters);
        Assert.Empty(h.ServerOptions.Filters.Message.OutgoingFilters);
        var tool = Assert.Single((await h.ListToolsAsync()).Tools);
        Assert.Equal("echo", tool.Name);
        Assert.False(tool.InputSchema.GetProperty("properties").TryGetProperty("context", out _));
        var result = await h.CallToolAsync("echo", """{"text":"hi"}""");
        Assert.Equal("echo:hi", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Empty(await h.FlushAsync());

        var options = new McpServerOptions();
        AgentCatTracker? viaEngine = null;
        Assert.Null(Record.Exception(() => viaEngine = AgentCatEngine.Install(options, "proj_a", null, h.Runtime, "options")));
        Assert.Null(viaEngine);
        Assert.Empty(options.Filters.Request.CallToolFilters);
    }

    [Fact]
    public void Install_never_throws()
    {
        var log = new LockProbeLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender());
        var tracker = AgentCatTracker.Create("proj_a", null, runtime, "options", _ => null);
        var options = new McpServerOptions();
        options.Filters.Request.CallToolFilters = ReadOnlyCallFilters();

        var installed = true;
        var installedWithNull = true;
        AgentCatTracker? viaEngine = tracker;

        var thrown = Record.Exception(() =>
        {
            installed = EngineInstaller.Install(options, tracker);
            viaEngine = AgentCatEngine.Install(options, "proj_a", null, runtime, "options");
            installedWithNull = EngineInstaller.Install(null!, tracker);
        });

        Assert.Null(thrown);
        Assert.False(installed);
        Assert.Null(viaEngine);
        Assert.False(installedWithNull);
        Assert.Equal(2, log.Lines.Count(l => l.StartsWith(FailedToTrack + "NotSupportedException: ", StringComparison.Ordinal)));
        Assert.Contains(log.Lines, l => l.StartsWith(FailedToTrack + "ArgumentNullException: ", StringComparison.Ordinal));
        Assert.DoesNotContain(true, log.UnderLock);
    }

    [Fact]
    public void Alternate_handler_warning_is_logged_once_per_tracker_outside_the_lock()
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "Handlers.CallToolWithAlternateHandler exists only on ModelContextProtocol 2.x.");
        var log = new LockProbeLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender());
        var first = AgentCatTracker.Create("proj_a", null, runtime, "options", _ => null);
        var second = AgentCatTracker.Create("proj_b", null, runtime, "options", _ => null);

        for (var i = 0; i < 3; i++)
        {
            var options = new McpServerOptions();
            Mcp.SetAlternateHandler(options);
            Assert.False(EngineInstaller.Install(options, first));
            Assert.False(EngineInstaller.Install(options, first));
            Assert.Empty(options.Filters.Request.CallToolFilters);
        }

        var other = new McpServerOptions();
        Mcp.SetAlternateHandler(other);
        Assert.False(EngineInstaller.Install(other, second));

        Assert.Equal(2, log.Lines.Count(l => l == AlternateHandlerWarning));
        Assert.DoesNotContain(true, log.UnderLock);
    }

    [Fact]
    public void Harness_turns_off_the_discover_probe_timeout()
    {
        var probe = typeof(ModelContextProtocol.Client.McpClientOptions).GetProperty("DiscoverProbeTimeout");

        Assert.Equal(McpHarness.McpMajor >= 2, probe is not null);
        if (probe is not null)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, probe.GetValue(McpHarness.ClientOptions(null)));
        }
    }

    // The message names the bound that ended the call; elapsed time would also count a stalled runner.
    [Fact]
    public async Task Harness_bounds_every_client_wait()
    {
        var release = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new EngineHarness();
        h.AddHandlerTool("""{"name":"hang","inputSchema":{"type":"object"}}""", (_, _) => new ValueTask<CallToolResult>(release.Task));
        await h.StartAsync();
        h.CallBound = TimeSpan.FromMilliseconds(200);

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => h.CallToolAsync("hang"));
        release.SetResult(Mcp.Result("""{"content":[]}"""));

        Assert.Equal("tools/call hang did not finish within 00:00:00.2000000.", timeout.Message);
    }

    [Theory]
    [MemberData(nameof(Startups))]
    public async Task Harness_round_trips_on_every_MCP_version(string? protocolVersion, bool withServices)
    {
        await using var h = new EngineHarness();
        h.AddTool((string text) => "echo:" + text, "echo", "Echoes text.");
        h.AddHandlerTool(
            """{"name":"shout","description":"Shouts text.","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}""",
            (context, _) => new ValueTask<CallToolResult>(Mcp.Result(
                """{"content":[{"type":"text","text":"SHOUT:""" + context.Params?.Arguments?["text"].GetString() + "\"}]}")));

        var client = await h.StartAsync(protocolVersion, withServices);

        var expectedProtocol = protocolVersion ?? (McpHarness.McpMajor < 2 ? McpHarness.LegacyProtocol : "2026-07-28");
        Assert.Equal(expectedProtocol, client.NegotiatedProtocolVersion);

        var names = (await h.ListToolsAsync()).Tools.Select(t => t.Name).ToArray();
        Assert.Contains("echo", names);
        Assert.Contains("shout", names);
        Assert.Equal("echo:hi", LastText(await h.CallToolAsync("echo", """{"text":"hi"}""")));
        Assert.Equal("SHOUT:hey", LastText(await h.CallToolAsync("shout", """{"text":"hey"}""")));
        Assert.NotNull(h.Tracker);
    }

    [Fact]
    public async Task Engine_harness_flushes_published_events_as_TS_json()
    {
        await using var h = new EngineHarness();
        h.AddTool((string text) => "echo:" + text, "echo");
        await h.StartAsync();

        EventPublisher.Publish(h.Tracker!, new AgentCatEvent { EventType = "mcp:probe", ResourceName = "probe" }, null, true);
        var events = await h.FlushAsync();

        var probe = Assert.Single(events, e => (string?)e["resourceName"] == "probe");
        Assert.Equal("proj_test", (string?)probe["projectId"]);
        Assert.Equal("mcp:probe", (string?)probe["eventType"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_properly_trace_tools_added_after_track_is_called(string? protocolVersion)
    {
        await using var h = new EngineHarness(options: new AgentCatOptions { EnableToolCallContext = true, EnableTracing = true });
        h.AddTool((string text) => "added:" + text, "add_todo");
        await h.StartAsync(protocolVersion);
        var tracker = Assert.IsType<AgentCatTracker>(h.Tracker);

        h.ServerOptions.ToolCollection!.Add(McpServerTool.Create(
            (string? data = null) => "New tool called with data: " + (data ?? "no data"),
            new McpServerToolCreateOptions { Name = "new_tool_after_track", Description = "A tool added after track() was called" }));

        var listed = Assert.Single((await h.ListToolsAsync()).Tools, t => t.Name == "new_tool_after_track");
        var listedProps = listed.InputSchema.GetProperty("properties");
        Assert.True(listedProps.TryGetProperty("data", out _));
        Assert.Equal("string", listedProps.GetProperty("context").GetProperty("type").GetString());
        var result = await h.CallToolAsync("new_tool_after_track", """{"context":"Testing new tool after track","data":"test data"}""");
        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text.Contains("test data"));
        AssertSeamsOnce(h.ServerOptions, tracker);
        var e = Assert.Single(await h.FlushAsync());
        Assert.Equal("new_tool_after_track", (string?)e["resourceName"]);
        Assert.Equal("Testing new tool after track", (string?)e["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Is_idempotent_wrapping_twice_does_not_double_wrap(string? protocolVersion)
    {
        var calls = 0;
        await using var h = new EngineHarness();
        h.AddTool(() =>
        {
            Interlocked.Increment(ref calls);
            return "done";
        }, "t");
        AgentCatTracker? once = null;
        AgentCatTracker? twice = null;
        h.Configure(o =>
        {
            once = h.Install(o);
            twice = h.Install(o);
        });

        await h.StartAsync(protocolVersion);
        await h.CallToolAsync("t");

        Assert.NotNull(once);
        Assert.Null(twice);
        AssertSeamsOnce(h.ServerOptions, once!);
        Assert.Equal(1, calls);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_not_create_duplicate_events_when_track_is_called_multiple_times(string? protocolVersion)
    {
        var calls = 0;
        await using var h = new EngineHarness();
        h.AddTool((string id) =>
        {
            Interlocked.Increment(ref calls);
            return "completed " + id;
        }, "complete_todo");
        h.Configure(o =>
        {
            h.Install(o);
            h.Install(o);
            h.Install(o);
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("complete_todo", """{"id":"1","context":"Testing double-wrapping protection"}""");
        var events = await h.FlushAsync();

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, calls);
        AssertSeamsOnce(h.ServerOptions, h.Tracker!);
        var e = Assert.Single(events);
        Assert.Equal("complete_todo", (string?)e["resourceName"]);
        Assert.Null(e["isError"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Advertises_a_tool_registered_after_track_with_injected_params(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.Configure(o =>
        {
            h.Install(o);
            o.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
            o.ToolCollection.Add(McpServerTool.Create(
                (string message) => "Processed: " + message,
                new McpServerToolCreateOptions { Name = "late_tool", Description = "Registered after tracking was enabled" }));
        });

        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();
        var late = Assert.Single(listing.Tools, t => t.Name == "late_tool");
        var lateProps = late.InputSchema.GetProperty("properties");
        Assert.True(lateProps.TryGetProperty("session_id", out _));
        Assert.True(lateProps.TryGetProperty("context", out _));
        Assert.Single(listing.Tools, t => t.Name == "get_more_tools");
        AssertSeamsOnce(h.ServerOptions, h.Tracker!);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Strips_injected_args_that_a_loose_schema_passes_through_inner_tap_on_the_late_executor(string? protocolVersion)
    {
        IDictionary<string, JsonElement>? lastSeenArgs = null;
        await using var h = new EngineHarness();
        h.Configure(o =>
        {
            h.Install(o);
            o.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
            o.ToolCollection.Add(McpServerTool.Create(
                (RequestContext<CallToolRequestParams> request) =>
                {
                    lastSeenArgs = request.Params?.Arguments;
                    return lastSeenArgs is not null && lastSeenArgs.TryGetValue("message", out var m) ? m.GetString() : null;
                },
                new McpServerToolCreateOptions { Name = "late_loose", Description = "Loose schema" }));
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("late_loose", """{"message":"hi","context":"late strip test","session_id":"ses_late00000000000000000000000"}""");

        Assert.NotNull(lastSeenArgs);
        Assert.Equal("message", Assert.Single(lastSeenArgs!.Keys));
        Assert.Equal("hi", lastSeenArgs["message"].GetString());
        Assert.Equal("hi", LastText(result));
        AssertSeamsOnce(h.ServerOptions, h.Tracker!);
        var e = Assert.Single(await h.FlushAsync());
        Assert.Equal("ses_late00000000000000000000000", (string?)e["sessionId"]);
        Assert.Equal("late strip test", (string?)e["userIntent"]);
        Assert.Equal("ses_late00000000000000000000000", (string?)e["parameters"]?["request"]?["params"]?["arguments"]?["session_id"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Second_track_on_the_same_server_is_a_no_op_no_double_wrap_one_event_per_call(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool((string msg) => "echo:" + msg, "echo");
        await h.StartAsync(protocolVersion);

        Assert.Null(AgentCatEngine.Install(h.ServerOptions, "proj_test", null, h.Runtime, "options"));

        var tools = (await h.ListToolsAsync()).Tools;
        Assert.Equal(1, tools.Count(t => t.Name == "get_more_tools"));
        var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"double-track"}""");
        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo:hi");
        AssertSeamsOnce(h.ServerOptions, h.Tracker!);
        Assert.Single(result.Content, c => c is TextContentBlock t && t.Text.StartsWith("[session_id", StringComparison.Ordinal));
        Assert.Single(await h.FlushAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Returns_an_incompatible_object_untouched_instead_of_throwing(string? protocolVersion)
    {
        McpRequestFilter<ListToolsRequestParams, ListToolsResult> userList = next => next;
        McpRequestFilter<CallToolRequestParams, CallToolResult> userCall = next => next;
        McpMessageFilter userOutgoing = next => next;
        await using var h = new EngineHarness();
        h.AddTool((string text) => "echo:" + text, "echo");
        h.Configure(o =>
        {
            o.Filters.Request.ListToolsFilters.Add(userList);
            o.Filters.Request.CallToolFilters = ReadOnlyCallFilters(userCall);
            o.Filters.Message.OutgoingFilters.Add(userOutgoing);
        });

        var thrown = await Record.ExceptionAsync(() => h.StartAsync(protocolVersion));

        Assert.Null(thrown);
        Assert.Null(h.Tracker);
        Assert.Contains(h.Log.Lines, l => l.StartsWith(FailedToTrack + "NotSupportedException: ", StringComparison.Ordinal));
        Assert.Same(userList, Assert.Single(h.ServerOptions.Filters.Request.ListToolsFilters));
        Assert.Same(userCall, Assert.Single(h.ServerOptions.Filters.Request.CallToolFilters));
        Assert.Same(userOutgoing, Assert.Single(h.ServerOptions.Filters.Message.OutgoingFilters));
        Assert.Equal("echo:hi", LastText(await h.CallToolAsync("echo", """{"text":"hi"}""")));
    }

    private static void AssertSeamsOnce(McpServerOptions options, AgentCatTracker tracker)
    {
        var request = options.Filters.Request;
        var list = Assert.Single(request.ListToolsFilters, f => f.Target is ListToolsSeam);
        Assert.Same(tracker, ((ListToolsSeam)list.Target!).Tracker);
        Assert.Same(list, request.ListToolsFilters[0]);
        var outer = Assert.Single(request.CallToolFilters, f => f.Target is CallToolSeam);
        Assert.Same(tracker, ((CallToolSeam)outer.Target!).Tracker);
        Assert.Same(outer, request.CallToolFilters[0]);
        var inner = Assert.Single(request.CallToolFilters, f => f.Target is InnerTap);
        Assert.Same(tracker, ((InnerTap)inner.Target!).Tracker);
        Assert.Same(inner, request.CallToolFilters[request.CallToolFilters.Count - 1]);
        var outgoing = Assert.Single(options.Filters.Message.OutgoingFilters, f => f.Target is OutgoingSeam);
        Assert.Same(tracker, ((OutgoingSeam)outgoing.Target!).Tracker);
        Assert.Same(outgoing, options.Filters.Message.OutgoingFilters[0]);
    }

    private static McpRequestFilter<CallToolRequestParams, CallToolResult> Recording(List<string> order, string name)
        => next => (context, cancellationToken) =>
        {
            order.Add(name);
            return next(context, cancellationToken);
        };

    private static ReadOnlyCollection<McpRequestFilter<CallToolRequestParams, CallToolResult>> ReadOnlyCallFilters(
        params McpRequestFilter<CallToolRequestParams, CallToolResult>[] filters)
        => new(filters.ToList());

    private static string LastText(CallToolResult result)
        => Assert.IsType<TextContentBlock>(Assert.Single(result.Content.Skip(result.Content.Count - 1))).Text;

    private sealed class LockProbeLog : IAgentCatLog
    {
        private static readonly object InstallerGate = typeof(EngineInstaller)
            .GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("EngineInstaller.Gate not found.");

        private readonly object _sync = new();
        private readonly List<string> _lines = new();
        private readonly List<bool> _underLock = new();

        internal IReadOnlyList<string> Lines
        {
            get
            {
                lock (_sync)
                {
                    return _lines.ToArray();
                }
            }
        }

        internal IReadOnlyList<bool> UnderLock
        {
            get
            {
                lock (_sync)
                {
                    return _underLock.ToArray();
                }
            }
        }

        public void Write(string message, LogSeverity hostSeverity) => Write(message);

        public void Write(string message)
        {
            var held = Monitor.IsEntered(InstallerGate);
            lock (_sync)
            {
                _lines.Add(message);
                _underLock.Add(held);
            }
        }
    }
}
