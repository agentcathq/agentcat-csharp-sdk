using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Text;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Engine;

public class HookTests
{
    private const string ToolCall = "mcp:tools/call";

    private const string Agent = "opus-4.80-1m|claude-code|k3n9x";

    private const string SourceTag = "agentcat_session_id_source";

    private const string AgentTag = "agentcat_agent_id";

    private const string AgentSourceTag = "agentcat_agent_id_source";

    private const string ProtocolTag = "agentcat_protocol_version";

    private const string EchoTool =
        """{"name":"echo","description":"echoes","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}""";

    private static readonly string[] SdkTagKeys = { SourceTag, AgentTag, AgentSourceTag, ProtocolTag };

    private static readonly JsonSerializerOptions Reflection = new();

    public static TheoryData<string?> Protocols => McpHarness.ProtocolVersions;

    private static string Quote(string text) => JsonSerializer.Serialize(text, Reflection);

    private static string Sid(string label)
    {
        var body = new StringBuilder();
        foreach (var c in label)
        {
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                body.Append(c);
            }
        }

        body.Append('0', 27);
        return "ses_" + body.ToString(0, 27);
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WithinAsync(Task task)
    {
        await FakeAgentCatApi.WithinAsync(task, FakeAgentCatApi.WaitBound);
        await task;
    }

    private static string TextAt(CallToolResult result, int index) => Assert.IsType<TextContentBlock>(result.Content[index]).Text;

    private static string? MintBackOf(CallToolResult result)
        => result.Content.Count > 0 && result.Content[0] is TextContentBlock first && first.Text.StartsWith("[session_id", StringComparison.Ordinal)
            ? first.Text
            : null;

    private static JsonObject CallEvent(IReadOnlyList<JsonObject> events) => Assert.Single(events, e => (string?)e["eventType"] == ToolCall);

    private static JsonObject[] CallEvents(IReadOnlyList<JsonObject> events) => events.Where(e => (string?)e["eventType"] == ToolCall).ToArray();

    private static string? Tag(JsonObject e, string key) => (string?)e["tags"]?[key];

    private static bool HasTag(JsonObject e, string key) => e["tags"] is JsonObject tags && tags.ContainsKey(key);

    private static string[] TagKeys(JsonObject e) => e["tags"] is JsonObject tags ? tags.Select(p => p.Key).ToArray() : Array.Empty<string>();

    private static string[] CustomerTags(JsonObject e)
        => e["tags"] is JsonObject tags
            ? tags.Where(p => !SdkTagKeys.Contains(p.Key)).Select(p => p.Key + "=" + (string?)p.Value).ToArray()
            : Array.Empty<string>();

    private static void AssertSeq(IEnumerable<string?> actual, params string[] expected) => Assert.Equal(expected, actual.ToArray());

    private static void AssertCustomerTags(JsonObject e, params string[] expected) => Assert.Equal(expected, CustomerTags(e));

    private static string Json(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    private static JsonNode? RawArgs(JsonObject e) => e["parameters"]?["request"]?["params"]?["arguments"];

    private static void AssertAnonymous(JsonObject e)
    {
        Assert.Null(e["identifyActorGivenId"]);
        Assert.Null(e["identifyActorName"]);
        Assert.Equal("{}", Json(e["identifyActorData"]));
    }

    private static string BudgetLine(string label, int milliseconds)
        => "Warning: " + label + " hook did not settle within " + milliseconds + "ms; publishing event without it";

    private static ValueTask<UserIdentity?> Identity(string userId, string? userName = null, JsonObject? userData = null)
        => new(new UserIdentity(userId) { UserName = userName, UserData = userData });

    private static ValueTask<IReadOnlyDictionary<string, string>?> Tags(params (string Key, string Value)[] pairs)
    {
        var tags = new Dictionary<string, string>();
        foreach (var (key, value) in pairs)
        {
            tags[key] = value;
        }

        return new ValueTask<IReadOnlyDictionary<string, string>?>(tags);
    }

    private static ValueTask<JsonObject?> Properties(string json) => new((JsonObject)JsonNode.Parse(json)!);

    private static EngineHarness Todos(AgentCatOptions options)
    {
        var h = new EngineHarness("test-project", options);
        var todos = new List<string>();
        h.AddTool(
            (string text) =>
            {
                lock (todos)
                {
                    todos.Add(text);
                    return "Added todo: \"" + text + "\" with ID " + todos.Count;
                }
            },
            "add_todo",
            "Add a new todo item");
        h.AddTool(
            () =>
            {
                lock (todos)
                {
                    return todos.Count == 0 ? "No todos found" : string.Join("\n", todos);
                }
            },
            "list_todos",
            "List all todos");
        h.AddTool((string id) => "Completed todo " + id, "complete_todo", "Mark a todo as completed");
        return h;
    }

    private static EngineHarness Notes(AgentCatOptions options)
    {
        var h = new EngineHarness("test-project", options);
        h.AddTool((string text) => "Added note: " + text, "add_note", "Adds a note");
        return h;
    }

    private static EngineHarness Echo(AgentCatOptions options)
    {
        var h = new EngineHarness("proj_test", options);
        h.AddTool((string msg) => "echo: " + msg, "echo", "Echo");
        return h;
    }

    private static EngineHarness LowLevel(AgentCatOptions options)
    {
        var h = new EngineHarness("proj_test", options);
        h.AddHandlerTool(EchoTool, (context, _) =>
        {
            var arguments = context.Params?.Arguments;
            var text = arguments is not null && arguments.TryGetValue("text", out var value) ? value.GetString() : null;
            if (text == "throw")
            {
                throw new InvalidOperationException("handler boom");
            }

            return new ValueTask<CallToolResult>(text == "explode"
                ? Mcp.Result("""{"isError":true,"content":[{"type":"text","text":"handler boom"}]}""")
                : Mcp.Result("{\"content\":[{\"type\":\"text\",\"text\":" + Quote("echo: " + text) + "}]}"));
        });
        return h;
    }

#pragma warning disable MCP9003
    private static RequestContext<CallToolRequestParams> Context(McpServer server, string paramsJson)
    {
        var request = (JsonRpcRequest)JsonSerializer.Deserialize(
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":" + paramsJson + "}",
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage)))!;
        return new RequestContext<CallToolRequestParams>(server, request) { Params = Mcp.CallToolParams(paramsJson) };
    }
#pragma warning restore MCP9003

    private static McpServer Bare() => McpServer.Create(new StreamServerTransport(new MemoryStream(), new MemoryStream()), new McpServerOptions());

    private static PendingHooks Started(AgentCatOptions options, AgentCatToolCall call, RecordingLog log)
        => Assert.IsType<PendingHooks>(PendingHooks.Start(TrackerSettings.From(options), call, log, new ManualClock()));

    private static async Task<AgentCatEvent> AppliedAsync(PendingHooks hooks)
    {
        var e = new AgentCatEvent
        {
            ProjectId = "proj_1",
            Tags = new Dictionary<string, string> { [SourceTag] = "hook" },
            IdentifyActorData = new JsonObject(),
        };
        await WithinAsync(hooks.ApplyAsync(e, CancellationToken.None));
        return e;
    }

    // client-identity.test.ts (resolveIdentity)

    [Fact]
    public async Task Returns_the_identify_result()
    {
        await using var server = Bare();
        var log = new RecordingLog();
        var hooks = Started(new AgentCatOptions { Identify = (_, _) => Identity("u1") }, ToolCallSnapshot.Build(Context(server, """{"name":"echo"}""")), log);

        var e = await AppliedAsync(hooks);

        Assert.Equal("u1", e.IdentifyActorGivenId);
        Assert.Null(e.IdentifyActorName);
        Assert.Equal("{}", Json(e.IdentifyActorData));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task No_identify_configured_null()
    {
        await using var server = Bare();
        var call = ToolCallSnapshot.Build(Context(server, """{"name":"echo"}"""));
        var log = new RecordingLog();

        Assert.Null(PendingHooks.Start(TrackerSettings.From(new AgentCatOptions()), call, log, new ManualClock()));

        var e = await AppliedAsync(Started(new AgentCatOptions { EventTags = (_, _) => Tags(("env", "test")) }, call, log));
        Assert.Null(e.IdentifyActorGivenId);
        Assert.Null(e.IdentifyActorName);
        Assert.Equal("{}", Json(e.IdentifyActorData));
    }

    [Fact]
    public async Task Identify_throwing_null_never_throws()
    {
        await using var server = Bare();
        var call = ToolCallSnapshot.Build(Context(server, """{"name":"echo"}"""));
        var log = new RecordingLog();

        var sync = Started(new AgentCatOptions { Identify = (_, _) => throw new InvalidOperationException("boom") }, call, log);
        var faulted = Started(
            new AgentCatOptions
            {
                Identify = async (_, _) =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("async boom");
                },
            },
            call,
            log);

        Assert.Null((await AppliedAsync(sync)).IdentifyActorGivenId);
        Assert.Null((await AppliedAsync(faulted)).IdentifyActorGivenId);
        Assert.Contains("Error: identify callback threw - InvalidOperationException: boom", log.Lines);
        Assert.Contains("Error: identify callback threw - InvalidOperationException: async boom", log.Lines);
    }

    [Fact]
    public async Task Runs_on_every_call_no_caching_between_calls()
    {
        await using var server = Bare();
        var calls = 0;
        var options = new AgentCatOptions { Identify = (_, _) => Identity("u" + Interlocked.Increment(ref calls)) };
        var log = new RecordingLog();

        var first = await AppliedAsync(Started(options, ToolCallSnapshot.Build(Context(server, """{"name":"echo"}""")), log));
        var second = await AppliedAsync(Started(options, ToolCallSnapshot.Build(Context(server, """{"name":"echo"}""")), log));

        Assert.Equal("u1", first.IdentifyActorGivenId);
        Assert.Equal("u2", second.IdentifyActorGivenId);
    }

    // handles.test.ts (invokeSessionHook)

    [Fact]
    public async Task Resolves_the_hook_value_and_forwards_request_extra_verbatim()
    {
        await using var server = Bare();
        var context = Context(server, """{"name":"echo","arguments":{"session_id":"ses_agent_sent"},"_meta":{"correlationId":"corr-1"}}""");
        var call = ToolCallSnapshot.Build(context);
        var seen = new List<AgentCatToolCall>();
        var log = new RecordingLog();
        var hooks = Started(
            new AgentCatOptions
            {
                ResolveSessionId = (c, _) =>
                {
                    seen.Add(c);
                    return new ValueTask<string?>((string?)c.Meta?["correlationId"]);
                },
            },
            call,
            log);

        var e = await AppliedAsync(hooks);

        var only = Assert.Single(seen);
        Assert.Same(call, only);
        Assert.Same(context, only.Request);
        Assert.Equal("ses_agent_sent", only.Arguments["session_id"].GetString());
        Assert.Equal(SessionIds.Derive("corr-1", "proj_1"), e.SessionId);
        Assert.Equal("hook", e.Tags![SourceTag]);
    }

    [Fact]
    public async Task Each_hook_gets_its_own_copy_of_the_request_meta()
    {
        await using var server = Bare();
        var context = Context(server, """{"name":"echo","arguments":{"text":"hi"},"_meta":{"trace":{"id":"t1"}}}""");
        var call = ToolCallSnapshot.Build(context);
        var seen = new List<(string Label, AgentCatToolCall Call)>();
        void Mark(string label, AgentCatToolCall c)
        {
            lock (seen)
            {
                seen.Add((label, c));
            }

            c.Meta!["trace"]!["by"] = label;
            c.Meta[label] = true;
        }

        var hooks = Started(
            new AgentCatOptions
            {
                ResolveSessionId = (c, _) =>
                {
                    Mark("resolveSessionId", c);
                    return new ValueTask<string?>((string?)null);
                },
                Identify = (c, _) =>
                {
                    Mark("identify", c);
                    return new ValueTask<UserIdentity?>((UserIdentity?)null);
                },
                EventTags = (c, _) =>
                {
                    Mark("eventTags", c);
                    return new ValueTask<IReadOnlyDictionary<string, string>?>((IReadOnlyDictionary<string, string>?)null);
                },
                EventProperties = (c, _) =>
                {
                    Mark("eventProperties", c);
                    return new ValueTask<JsonObject?>((JsonObject?)null);
                },
            },
            call,
            new RecordingLog());

        await AppliedAsync(hooks);

        Assert.Equal(4, seen.Count);
        foreach (var (label, c) in seen)
        {
            Assert.Equal("{\"trace\":{\"id\":\"t1\",\"by\":\"" + label + "\"},\"" + label + "\":true}", c.Meta!.ToJsonString());
            Assert.Same(call.Request, c.Request);
            Assert.Equal("hi", c.Arguments["text"].GetString());
        }

        Assert.Equal("""{"trace":{"id":"t1"}}""", context.Params!.Meta!.ToJsonString());
    }

    [Fact]
    public async Task Resolves_null_when_the_hook_returns_nullish()
    {
        await using var server = Bare();
        var log = new RecordingLog();
        var hooks = Started(
            new AgentCatOptions { ResolveSessionId = (_, _) => new ValueTask<string?>((string?)null) },
            ToolCallSnapshot.Build(Context(server, """{"name":"echo"}""")),
            log);

        var e = await AppliedAsync(hooks);

        Assert.StartsWith("ses_", e.SessionId, StringComparison.Ordinal);
        Assert.Equal("minted", e.Tags![SourceTag]);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task Contains_a_synchronous_throw()
    {
        await using var server = Bare();
        var log = new RecordingLog();
        var hooks = Started(
            new AgentCatOptions { ResolveSessionId = (_, _) => throw new InvalidOperationException("db down") },
            ToolCallSnapshot.Build(Context(server, """{"name":"echo"}""")),
            log);

        var e = await AppliedAsync(hooks);

        Assert.StartsWith("ses_", e.SessionId, StringComparison.Ordinal);
        Assert.Equal("minted", e.Tags![SourceTag]);
        AssertSeq(log.Lines, "resolveSessionId hook error: InvalidOperationException: db down");
    }

    [Fact]
    public async Task Contains_a_rejection()
    {
        await using var server = Bare();
        var log = new RecordingLog();
        var hooks = Started(
            new AgentCatOptions
            {
                ResolveSessionId = async (_, _) =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("db down");
                },
            },
            ToolCallSnapshot.Build(Context(server, """{"name":"echo"}""")),
            log);

        var e = await AppliedAsync(hooks);

        Assert.StartsWith("ses_", e.SessionId, StringComparison.Ordinal);
        Assert.Equal("minted", e.Tags![SourceTag]);
        AssertSeq(log.Lines, "resolveSessionId hook error: InvalidOperationException: db down");
    }

    // engine-callwrap.test.ts (deferred customer hooks)

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_hanging_identify_hook_does_not_delay_the_tool_call(string? protocolVersion)
    {
        var never = Signal<UserIdentity?>();
        await using var h = LowLevel(new AgentCatOptions { Identify = (_, _) => new ValueTask<UserIdentity?>(never.Task) });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("echo", """{"text":"hi"}""");

        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo: hi");
        Assert.False(never.Task.IsCompleted);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Hanging_resolveSessionId_eventTags_eventProperties_do_not_delay_the_tool_call(string? protocolVersion)
    {
        await using var h = LowLevel(new AgentCatOptions
        {
            ResolveSessionId = (_, _) => new ValueTask<string?>(Signal<string?>().Task),
            EventTags = (_, _) => new ValueTask<IReadOnlyDictionary<string, string>?>(Signal<IReadOnlyDictionary<string, string>?>().Task),
            EventProperties = (_, _) => new ValueTask<JsonObject?>(Signal<JsonObject?>().Task),
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("echo", """{"text":"hi"}""");

        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo: hi");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task The_handler_starts_before_a_slow_hook_settles(string? protocolVersion)
    {
        var order = new List<string>();
        var hook = Signal<UserIdentity?>();
        var options = new AgentCatOptions { Identify = (_, _) => new ValueTask<UserIdentity?>(hook.Task) };
        await using var h = new EngineHarness(options: options);
        h.AddHandlerTool(EchoTool, (_, _) =>
        {
            lock (order)
            {
                order.Add("handler");
            }

            return new ValueTask<CallToolResult>(Mcp.Result("""{"content":[{"type":"text","text":"ok"}]}"""));
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("echo", """{"text":"hi"}""");
        lock (order)
        {
            order.Add("hook settled");
        }

        hook.SetResult(new UserIdentity("u1"));
        var e = CallEvent(await h.FlushAsync());

        Assert.Equal("handler", order[0]);
        Assert.Equal("u1", (string?)e["identifyActorGivenId"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_synchronously_throwing_hook_is_contained_and_the_event_still_publishes(string? protocolVersion)
    {
        await using var h = LowLevel(new AgentCatOptions { ResolveSessionId = (_, _) => throw new InvalidOperationException("sync hook boom") });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("echo", """{"text":"hi"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo: hi");
        Assert.StartsWith("ses_", (string?)e["sessionId"], StringComparison.Ordinal);
        Assert.Equal("minted", Tag(e, SourceTag));
        Assert.Contains("resolveSessionId hook error: InvalidOperationException: sync hook boom", h.Log.Lines);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task RedactEvent_sees_resolved_hook_state_and_pending_never_reaches_it_or_the_processed_event(string? protocolVersion)
    {
        string? seenActor = null;
        string? seenEnv = null;
        string? seenJson = null;
        await using var h = LowLevel(new AgentCatOptions
        {
            Identify = async (_, _) =>
            {
                await Task.Yield();
                return new UserIdentity("u-redact");
            },
            EventTags = async (_, _) =>
            {
                await Task.Yield();
                return new Dictionary<string, string> { ["env"] = "prod" };
            },
            RedactEvent = (e, _) =>
            {
                seenActor = e.IdentifyActorGivenId;
                seenEnv = e.Tags is not null && e.Tags.TryGetValue("env", out var env) ? env : null;
                seenJson = AgentCat.Internal.Events.EventShape.ToTsJson(e).ToJsonString();
                return new ValueTask<AgentCatEvent?>(e);
            },
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("echo", """{"text":"hi"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Equal("u-redact", seenActor);
        Assert.Equal("prod", seenEnv);
        Assert.DoesNotContain("pending", seenJson, StringComparison.Ordinal);
        Assert.False(e.ContainsKey("pending"));
        Assert.Equal("u-redact", (string?)e["identifyActorGivenId"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Error_path_events_carry_hook_results_resolved_after_the_throw(string? protocolVersion)
    {
        var late = Signal<bool>();
        await using var h = LowLevel(new AgentCatOptions
        {
            Identify = async (_, _) =>
            {
                await late.Task;
                return new UserIdentity("late-user");
            },
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("echo", """{"text":"throw"}""");
        late.SetResult(true);
        var e = CallEvent(await h.FlushAsync());

        Assert.True(result.IsError);
        Assert.Equal(true, (bool?)e["isError"]);
        Assert.Equal("handler boom", (string?)e["error"]?["message"]);
        Assert.Equal("System.InvalidOperationException", (string?)e["error"]?["type"]);
        Assert.Equal("late-user", (string?)e["identifyActorGivenId"]);
    }

    // error-capture.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_NOT_publish_identify_events_when_identify_function_throws(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions { Identify = (_, _) => throw new InvalidOperationException("Identify error") });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("list_todos", """{"context":"Testing identify error capture"}""");
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
        CallEvent(events);
    }

    // event-tags-properties.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_attach_tags_to_tool_call_events(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            EnableAgentTracking = true,
            EventTags = async (_, _) =>
            {
                await Task.Yield();
                return new Dictionary<string, string> { ["env"] = "test", ["trace_id"] = "abc-123" };
            },
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", "{\"text\":\"Test todo\",\"agent_id\":" + Quote(Agent) + "}");
        var e = CallEvent(await h.FlushAsync());

        AssertCustomerTags(e, "env=test", "trace_id=abc-123");
        Assert.Equal("minted", Tag(e, SourceTag));
        Assert.Equal("supplied", Tag(e, AgentSourceTag));
        Assert.Equal(Agent, Tag(e, AgentTag));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_overwrite_a_customer_tag_that_collides_with_an_SDK_tag_name(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions { EventTags = (_, _) => Tags((SourceTag, "customer-value"), ("env", "test")) });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", "{\"text\":\"Test todo\",\"session_id\":" + Quote(Sid("supplied")) + "}");
        var e = CallEvent(await h.FlushAsync());

        Assert.Equal("supplied", Tag(e, SourceTag));
        AssertCustomerTags(e, "env=test");
        Assert.Equal(new[] { SourceTag, "env" }, TagKeys(e).Take(2).ToArray());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_not_block_tool_calls_when_callback_throws_eventTags(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            EventTags = async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("Tags callback failed");
            },
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("add_todo", """{"text":"Test todo"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Contains("Added todo", TextAt(result, 1), StringComparison.Ordinal);
        Assert.Empty(CustomerTags(e));
        Assert.True(HasTag(e, SourceTag));
        Assert.Contains("eventTags callback error: InvalidOperationException: Tags callback failed", h.Log.Lines);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_null_return_from_callback_eventTags(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions { EventTags = (_, _) => new ValueTask<IReadOnlyDictionary<string, string>?>((IReadOnlyDictionary<string, string>?)null) });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Test todo"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Empty(CustomerTags(e));
        Assert.True(HasTag(e, SourceTag));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_validate_tags_from_callback(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions { EventTags = (_, _) => Tags(("valid", "value"), ("invalid!", "value")) });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Test todo"}""");
        var e = CallEvent(await h.FlushAsync());

        AssertCustomerTags(e, "valid=value");
        Assert.Contains("Dropping invalid tag: \"invalid!\" — key contains invalid characters or is empty", h.Log.Lines);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_attach_properties_to_tool_call_events(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            EventProperties = (_, _) => Properties("""{"device":"desktop","feature_flags":["dark_mode"],"nested":{"key":"value"}}"""),
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Test todo"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Equal("""{"device":"desktop","feature_flags":["dark_mode"],"nested":{"key":"value"}}""", Json(e["properties"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_not_block_tool_calls_when_callback_throws_eventProperties(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            EventProperties = async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("Properties callback failed");
            },
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("add_todo", """{"text":"Test todo"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Contains("Added todo", TextAt(result, 1), StringComparison.Ordinal);
        Assert.Null(e["properties"]);
        Assert.Contains("eventProperties callback error: InvalidOperationException: Properties callback failed", h.Log.Lines);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_null_return_from_callback_eventProperties(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions { EventProperties = (_, _) => new ValueTask<JsonObject?>((JsonObject?)null) });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Test todo"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.False(e.ContainsKey("properties"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_attach_both_tags_and_properties_to_the_same_event(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            EventTags = (_, _) => Tags(("env", "test")),
            EventProperties = (_, _) => Properties("""{"device":"mobile"}"""),
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Test todo"}""");
        var e = CallEvent(await h.FlushAsync());

        AssertCustomerTags(e, "env=test");
        Assert.Equal("""{"device":"mobile"}""", Json(e["properties"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_not_add_tags_or_properties_when_not_configured(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions());
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Test todo"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Empty(CustomerTags(e));
        Assert.True(HasTag(e, SourceTag));
        Assert.False(e.ContainsKey("properties"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_not_redact_tags_or_properties_when_redactSensitiveInformation_is_configured(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            RedactSensitiveInformation = (_, _) => new ValueTask<string>("[REDACTED]"),
            EventTags = (_, _) => Tags(("env", "production"), ("trace_id", "abc-123")),
            EventProperties = (_, _) => Properties("""{"device":"desktop","app_version":"2.1.0","feature_flags":["dark_mode","beta_ui"]}"""),
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Test todo"}""");
        var e = CallEvent(await h.FlushAsync());

        AssertCustomerTags(e, "env=production", "trace_id=abc-123");
        Assert.Equal("""{"device":"desktop","app_version":"2.1.0","feature_flags":["dark_mode","beta_ui"]}""", Json(e["properties"]));
        Assert.Equal("[REDACTED]", (string?)RawArgs(e)?["text"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_pass_request_and_extra_to_eventTags_callback(string? protocolVersion)
    {
        AgentCatToolCall? captured = null;
        await using var h = Todos(new AgentCatOptions
        {
            EventTags = (call, _) =>
            {
                captured = call;
                return Tags(("env", "test"));
            },
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Callback args test"}""");
        await h.FlushAsync();

        Assert.NotNull(captured);
        Assert.Equal("add_todo", captured!.ToolName);
        Assert.Equal("Callback args test", captured.Arguments["text"].GetString());
        Assert.NotNull(captured.Request);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_pass_request_and_extra_to_eventProperties_callback(string? protocolVersion)
    {
        AgentCatToolCall? captured = null;
        await using var h = Todos(new AgentCatOptions
        {
            EventProperties = (call, _) =>
            {
                captured = call;
                return Properties("""{"device":"test"}""");
            },
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Props callback args test"}""");
        await h.FlushAsync();

        Assert.NotNull(captured);
        Assert.Equal("add_todo", captured!.ToolName);
        Assert.NotNull(captured.Request);
    }

    // handles-integration.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Hook_mode_no_session_id_param_hook_derived_session_id_no_task_mint_back(string? protocolVersion)
    {
        await using var h = LowLevel(new AgentCatOptions
        {
            EnableAgentTracking = true,
            ResolveSessionId = (_, _) => new ValueTask<string?>("customer-correlation-7"),
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var echo = Assert.Single((await h.ListToolsAsync()).Tools, t => t.Name == "echo");
        var properties = echo.InputSchema.GetProperty("properties");
        var required = echo.InputSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.False(properties.TryGetProperty("session_id", out _));
        Assert.True(properties.TryGetProperty("agent_id", out _));
        Assert.Contains("agent_id", required);
        Assert.DoesNotContain("session_id", required);

        var result = await h.CallToolAsync("echo", "{\"text\":\"hi\",\"context\":\"testing\",\"agent_id\":" + Quote(Agent) + "}");
        var e = CallEvent(await h.FlushAsync());

        Assert.Null(MintBackOf(result));
        Assert.Equal("hook", Tag(e, SourceTag));
        Assert.Equal(Agent, Tag(e, AgentTag));
        Assert.Equal("supplied", Tag(e, AgentSourceTag));
        Assert.Equal(SessionIds.Derive("customer-correlation-7", "proj_test"), (string?)e["sessionId"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Identify_runs_per_call_and_stamps_the_event_no_identify_event_published(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions { Identify = (_, _) => Identity("actor-7", "Sam") });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_todo", """{"text":"x","context":"testing"}""");
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
        var call = CallEvent(events);
        Assert.Equal("actor-7", (string?)call["identifyActorGivenId"]);
        Assert.Equal("Sam", (string?)call["identifyActorName"]);
    }

    // identify.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_call_identify_on_the_tool_call_and_stamp_the_identity_onto_that_event(string? protocolVersion)
    {
        var identifyCalled = false;
        var userId = "user-" + Guid.NewGuid();
        var userData = new JsonObject { ["name"] = "Test User " + Guid.NewGuid(), ["email"] = "test-" + Guid.NewGuid() + "@example.com" };
        var expected = userData.ToJsonString();
        await using var h = Todos(new AgentCatOptions
        {
            EnableTracing = true,
            Identify = (call, _) =>
            {
                identifyCalled = call.Request is not null;
                return Identity(userId, userData: userData);
            },
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("add_todo", """{"text":"Test todo for identification","context":"Adding a todo item for identification test"}""");
        Assert.Contains("Added todo", TextAt(result, 1), StringComparison.Ordinal);
        Assert.True(identifyCalled);
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
        var e = CallEvent(events);
        Assert.Equal("add_todo", (string?)e["resourceName"]);
        Assert.Equal(userId, (string?)e["identifyActorGivenId"]);
        Assert.Equal(expected, Json(e["identifyActorData"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_call_identify_on_every_tool_call_and_stamp_every_event_with_no_dedup(string? protocolVersion)
    {
        var count = 0;
        var userId = "user-" + Guid.NewGuid();
        var userName = "Another User " + Guid.NewGuid();
        await using var h = Todos(new AgentCatOptions
        {
            Identify = (_, _) =>
            {
                Interlocked.Increment(ref count);
                return Identity(userId, userData: new JsonObject { ["name"] = userName });
            },
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"First todo","context":"Adding a todo item for identification test"}""");
        Assert.Equal(1, Volatile.Read(ref count));
        await h.CallToolAsync("list_todos", """{"context":"Adding a todo item for identification test"}""");
        Assert.Equal(2, Volatile.Read(ref count));
        await h.CallToolAsync("complete_todo", """{"id":"1","context":"Completing a todo item for identification test"}""");
        Assert.Equal(3, Volatile.Read(ref count));
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
        var calls = CallEvents(events);
        Assert.Equal(3, calls.Length);
        Assert.All(calls, e =>
        {
            Assert.Equal(userId, (string?)e["identifyActorGivenId"]);
            Assert.Equal("{\"name\":" + Quote(userName) + "}", Json(e["identifyActorData"]));
        });
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_properly_identify_when_calling_tools_added_after_track(string? protocolVersion)
    {
        var identifyCalled = false;
        var userId = "post-track-user-" + Guid.NewGuid();
        var userData = new JsonObject { ["name"] = "Post Track User " + Guid.NewGuid(), ["email"] = "post-track-" + Guid.NewGuid() + "@example.com" };
        var expected = userData.ToJsonString();
        await using var h = Todos(new AgentCatOptions
        {
            EnableTracing = true,
            EnableToolCallContext = true,
            Identify = (call, _) =>
            {
                identifyCalled = call.Request is not null;
                return Identity(userId, userData: userData);
            },
        });
        await h.StartAsync(protocolVersion);
        h.ServerOptions.ToolCollection!.Add(McpServerTool.Create(
            (string message) => "Processed message: " + message,
            new McpServerToolCreateOptions { Name = "post_track_tool", Description = "A tool added after tracking was enabled" }));

        var result = await h.CallToolAsync(
            "post_track_tool",
            """{"message":"Testing post-track identification","context":"Verifying identification works for dynamically added tools"}""");
        Assert.Contains("Processed message: Testing post-track identification", TextAt(result, 1), StringComparison.Ordinal);
        Assert.True(identifyCalled);
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
        var e = Assert.Single(events, e => (string?)e["eventType"] == ToolCall && (string?)e["resourceName"] == "post_track_tool");
        Assert.Equal("Verifying identification works for dynamically added tools", (string?)e["userIntent"]);
        Assert.Equal(userId, (string?)e["identifyActorGivenId"]);
        Assert.Equal(expected, Json(e["identifyActorData"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_maintain_user_identification_across_multiple_tool_calls(string? protocolVersion)
    {
        var userId = "persistent-user-" + Guid.NewGuid();
        var userData = new JsonObject { ["name"] = "Persistent User " + Guid.NewGuid(), ["department"] = "Engineering", ["customField"] = "custom-value-" + Guid.NewGuid() };
        var expected = userData.ToJsonString();
        var sessionId = Sid("identifypersistence");
        await using var h = Todos(new AgentCatOptions { Identify = (_, _) => Identity(userId, userData: userData) });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", "{\"text\":\"Todo 1\",\"context\":\"Adding a todo item for reset task\",\"session_id\":" + Quote(sessionId) + "}");
        await h.CallToolAsync("add_todo", "{\"text\":\"Todo 2\",\"context\":\"Adding a todo item for reset task\",\"session_id\":" + Quote(sessionId) + "}");
        await h.CallToolAsync("list_todos", "{\"context\":\"Listing todos for reset task\",\"session_id\":" + Quote(sessionId) + "}");
        var calls = CallEvents(await h.FlushAsync());

        Assert.Equal(3, calls.Length);
        Assert.Equal(new[] { sessionId }, calls.Select(e => (string?)e["sessionId"]).Distinct().ToArray());
        Assert.All(calls, e =>
        {
            Assert.Equal(userId, (string?)e["identifyActorGivenId"]);
            Assert.Equal(expected, Json(e["identifyActorData"]));
        });
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_when_identify_function_returns_null(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions { Identify = (_, _) => new ValueTask<UserIdentity?>((UserIdentity?)null) });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("add_todo", """{"text":"Test todo","context":"Adding a todo item for null identity test"}""");
        Assert.Contains("Added todo", TextAt(result, 1), StringComparison.Ordinal);
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
        AssertAnonymous(CallEvent(events));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_work_without_identify_function_anonymous_sessions(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions { EnableTracing = true });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Anonymous todo","context":"Adding a todo item for anonymous test"}""");
        await h.CallToolAsync("list_todos", """{"context":"Listing todos for anonymous test"}""");
        var events = await h.FlushAsync();

        var calls = CallEvents(events);
        Assert.Equal(2, calls.Length);
        Assert.All(calls, e => Assert.StartsWith("ses_", (string?)e["sessionId"], StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_populate_actorGivenId_actorName_and_actorData_on_the_event(string? protocolVersion)
    {
        var userId = "session-user-" + Guid.NewGuid();
        var userName = "Session User " + Guid.NewGuid();
        var userData = new JsonObject { ["name"] = "Session Test User " + Guid.NewGuid(), ["role"] = "Developer", ["team"] = "Platform" };
        var expected = userData.ToJsonString();
        await using var h = Todos(new AgentCatOptions { Identify = (_, _) => Identity(userId, userName, userData) });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Test session info","context":"Adding a todo item for session info test"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Equal(userId, (string?)e["identifyActorGivenId"]);
        Assert.Equal(userName, (string?)e["identifyActorName"]);
        Assert.Equal(expected, Json(e["identifyActorData"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_re_resolve_identity_per_request_so_a_changed_identity_lands_on_the_next_event(string? protocolVersion)
    {
        var first = "event-user-a-" + Guid.NewGuid();
        var second = "event-user-b-" + Guid.NewGuid();
        var identities = new[]
        {
            new UserIdentity(first) { UserName = "First", UserData = new JsonObject { ["seat"] = "a" } },
            new UserIdentity(second) { UserName = "Second", UserData = new JsonObject { ["seat"] = "b" } },
        };
        var call = -1;
        await using var h = Todos(new AgentCatOptions { Identify = (_, _) => new ValueTask<UserIdentity?>(identities[Interlocked.Increment(ref call)]) });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"First call","context":"Adding a todo item for event data test"}""");
        await h.FlushAsync();
        await h.CallToolAsync("add_todo", """{"text":"Second call","context":"Adding a todo item for event data test"}""");
        var calls = CallEvents(await h.FlushAsync());

        Assert.Equal(2, calls.Length);
        Assert.Equal(first, (string?)calls[0]["identifyActorGivenId"]);
        Assert.Equal("First", (string?)calls[0]["identifyActorName"]);
        Assert.Equal("""{"seat":"a"}""", Json(calls[0]["identifyActorData"]));
        Assert.Equal(second, (string?)calls[1]["identifyActorGivenId"]);
        Assert.Equal("Second", (string?)calls[1]["identifyActorName"]);
        Assert.Equal("""{"seat":"b"}""", Json(calls[1]["identifyActorData"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_async_operations_in_identify_function(string? protocolVersion)
    {
        var lookup = Signal<bool>();
        var completed = false;
        var asyncUserId = "async-user-" + Guid.NewGuid();
        await using var h = Todos(new AgentCatOptions
        {
            Identify = async (_, _) =>
            {
                await lookup.Task;
                Volatile.Write(ref completed, true);
                return new UserIdentity(asyncUserId) { UserData = new JsonObject { ["source"] = "async-lookup" } };
            },
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("add_todo", """{"text":"Async test todo","context":"Adding a todo item for async test"}""");
        Assert.Contains("Added todo", TextAt(result, 1), StringComparison.Ordinal);
        Assert.False(Volatile.Read(ref completed));
        lookup.SetResult(true);
        var e = CallEvent(await h.FlushAsync());

        Assert.True(Volatile.Read(ref completed));
        Assert.Equal(asyncUserId, (string?)e["identifyActorGivenId"]);
        Assert.Equal("""{"source":"async-lookup"}""", Json(e["identifyActorData"]));
        Assert.True((int?)e["duration"] < 95);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_errors_in_identify_function_gracefully(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            Identify = async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("Failed to identify user");
            },
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("add_todo", """{"text":"Error test todo","context":"Adding a todo item for error test"}""");
        Assert.Contains("Added todo", TextAt(result, 1), StringComparison.Ordinal);
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
        AssertAnonymous(CallEvent(events));
        Assert.Contains("Error: identify callback threw - InvalidOperationException: Failed to identify user", h.Log.Lines);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_identify_function_that_returns_invalid_data(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions { Identify = (_, _) => new ValueTask<UserIdentity?>(new UserIdentity(null!)) });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("add_todo", """{"text":"Invalid identity test","context":"Adding a todo item for invalid identity test"}""");
        Assert.Contains("Added todo", TextAt(result, 1), StringComparison.Ordinal);

        AssertAnonymous(CallEvent(await h.FlushAsync()));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_not_run_identify_or_publish_an_event_for_a_tools_list_request(string? protocolVersion)
    {
        var identifyCalled = false;
        await using var h = Todos(new AgentCatOptions
        {
            Identify = (_, _) =>
            {
                identifyCalled = true;
                return Identity("list-tools-user-" + Guid.NewGuid(), "List Tools User", new JsonObject { ["plan"] = "premium" });
            },
        });
        await h.StartAsync(protocolVersion);

        await h.ListToolsAsync();

        Assert.Empty(await h.FlushAsync());
        Assert.False(identifyCalled);
    }

    // redaction.test.ts (integration)

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_properly_redact_sensitive_data_in_published_events(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            RedactSensitiveInformation = (text, _) =>
            {
                var lower = text.ToLowerInvariant();
                return new ValueTask<string>(
                    text.Any(c => c == '@') ? "[REDACTED-EMAIL]"
                    : lower.Contains("secret") || lower.Contains("key") ? "[REDACTED-SECRET]"
                    : lower.Contains("password") || lower.Contains("ssn") ? "[REDACTED-SENSITIVE]"
                    : text);
            },
            Identify = (_, _) => Identity("test-user-123", "John Doe", new JsonObject { ["email"] = "user@example.com", ["apiKey"] = "secret-api-key-123" }),
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Send email to admin@company.com with password reset","context":"Adding a todo item for reset task"}""");
        var events = await h.FlushAsync();

        var e = CallEvent(events);
        Assert.Equal("[REDACTED-EMAIL]", (string?)RawArgs(e)?["text"]);
        Assert.Equal("Adding a todo item for reset task", (string?)RawArgs(e)?["context"]);
        Assert.DoesNotContain(events, x => (string?)x["eventType"] == "agentcat:identify");
        Assert.StartsWith("ses_", (string?)e["sessionId"], StringComparison.Ordinal);
        Assert.Equal("test-project", (string?)e["projectId"]);
        Assert.Equal("add_todo", (string?)e["resourceName"]);
        Assert.Equal(ToolCall, (string?)e["eventType"]);
        Assert.Equal("test-user-123", (string?)e["identifyActorGivenId"]);
        Assert.Equal("John Doe", (string?)e["identifyActorName"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_complex_nested_structures_with_redaction(string? protocolVersion)
    {
        var card = new System.Text.RegularExpressions.Regex("\\d{4}[\\s-]?\\d{4}[\\s-]?\\d{4}[\\s-]?\\d{4}");
        await using var h = Todos(new AgentCatOptions
        {
            RedactSensitiveInformation = (text, _) => new ValueTask<string>(card.IsMatch(text) ? "[REDACTED-CC]" : text),
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Process payment for card 1234 5678 9012 3456","context":"Processing payment for order"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Equal("[REDACTED-CC]", (string?)RawArgs(e)?["text"]);
        Assert.Equal("Processing payment for order", (string?)RawArgs(e)?["context"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_not_redact_protected_fields_even_if_they_contain_sensitive_patterns(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            RedactSensitiveInformation = (text, _) => new ValueTask<string>(System.Text.RegularExpressions.Regex.IsMatch(text, "id", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "[REDACTED-ID]" : text),
            Identify = (_, _) => Identity("user-with-id-123", "David Smith", new JsonObject { ["internalId"] = "internal-id-456" }),
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Task with id reference custom-id-789","context":"Adding task with id reference"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.StartsWith("ses_", (string?)e["sessionId"], StringComparison.Ordinal);
        Assert.False(e.ContainsKey("actorId"));
        Assert.Equal("[REDACTED-ID]", (string?)RawArgs(e)?["text"]);
        Assert.Equal("[REDACTED-ID]", (string?)RawArgs(e)?["context"]);
        Assert.Equal("user-with-id-123", (string?)e["identifyActorGivenId"]);
        Assert.Equal("David Smith", (string?)e["identifyActorName"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_let_the_hook_inspect_event_metadata_and_replace_event_fields(string? protocolVersion)
    {
        var seen = new List<string?>();
        await using var h = Todos(new AgentCatOptions
        {
            RedactEvent = (e, _) =>
            {
                lock (seen)
                {
                    seen.Add(e.ResourceName);
                }

                if (e.ResourceName == "add_todo")
                {
                    e.Parameters = new JsonObject { ["replaced"] = true };
                }

                return new ValueTask<AgentCatEvent?>(e);
            },
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Buy milk","context":"Adding a todo item"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Contains("add_todo", seen);
        Assert.Equal("""{"replaced":true}""", Json(e["parameters"]));
        Assert.StartsWith("ses_", (string?)e["sessionId"], StringComparison.Ordinal);
        Assert.Equal("test-project", (string?)e["projectId"]);
        Assert.Equal(ToolCall, (string?)e["eventType"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_drop_only_the_events_the_hook_rejects_while_the_others_still_publish(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            RedactEvent = (e, _) => new ValueTask<AgentCatEvent?>(e.ResourceName == "add_todo" ? null : e),
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"Buy milk","context":"Adding a todo item"}""");
        await h.CallToolAsync("list_todos", """{"context":"Listing todos"}""");
        await h.FlushAsync();

        Assert.DoesNotContain(h.Sender.Sent, e => e.ResourceName == "add_todo");
        Assert.Single(h.Sender.Sent, e => e.ResourceName == "list_todos");
        Assert.Contains("Event dropped by redactEvent hook", h.Log.Lines);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_run_the_event_hook_on_raw_values_before_string_redaction(string? protocolVersion)
    {
        var rawTexts = new List<string>();
        await using var h = Todos(new AgentCatOptions
        {
            RedactEvent = (e, _) =>
            {
                if (e.Parameters?["request"]?["params"]?["arguments"]?["text"] is JsonValue text && text.TryGetValue<string>(out var value))
                {
                    lock (rawTexts)
                    {
                        rawTexts.Add(value);
                    }
                }

                return new ValueTask<AgentCatEvent?>(e);
            },
            RedactSensitiveInformation = (text, _) => new ValueTask<string>(text.Contains("secret") ? "[REDACTED-SECRET]" : text),
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"this contains a secret value","context":"Adding a todo item"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Contains("this contains a secret value", rawTexts);
        Assert.Equal("[REDACTED-SECRET]", (string?)RawArgs(e)?["text"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_apply_the_hook_to_custom_events_published_on_a_tracked_server(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            RedactEvent = (e, _) =>
            {
                if (e.EventType == WireNames.CustomEventType)
                {
                    e.Response = new JsonObject { ["scrubbed"] = true };
                }

                return new ValueTask<AgentCatEvent?>(e);
            },
        });
        await h.StartAsync(protocolVersion);

        EventPublisher.Publish(
            h.Tracker!,
            new AgentCatEvent { EventType = WireNames.CustomEventType, ResourceName = "custom_action", Response = new JsonObject { ["secret"] = "raw value" } },
            null,
            redactStrings: false);
        var events = await h.FlushAsync();

        var custom = Assert.Single(events, e => (string?)e["eventType"] == WireNames.CustomEventType);
        Assert.Equal("""{"scrubbed":true}""", Json(custom["response"]));
    }

    // report-missing.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_work_correctly_with_user_identification(string? protocolVersion)
    {
        var userId = "report-user-" + Guid.NewGuid();
        var userData = new JsonObject { ["name"] = "Report Test User " + Guid.NewGuid(), ["role"] = "Developer" };
        var expected = userData.ToJsonString();
        await using var h = Todos(new AgentCatOptions { EnableReportMissing = true, Identify = (_, _) => Identity(userId, userData: userData) });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("get_more_tools", """{"context":"Need GraphQL query builder"}""");
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
        var report = Assert.Single(events, e => (string?)e["eventType"] == ToolCall && (string?)e["resourceName"] == "get_more_tools");
        Assert.Equal(userId, (string?)report["identifyActorGivenId"]);
        Assert.Equal(expected, Json(report["identifyActorData"]));
    }

    // v2/get-more-tools.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Stamps_identity_resolved_for_a_get_more_tools_call_onto_its_event(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions { Identify = (_, _) => Identity("report-user", userData: new JsonObject { ["role"] = "Developer" }) });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("get_more_tools", """{"context":"Need GraphQL query builder"}""");
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
        var e = Assert.Single(events);
        Assert.Equal("get_more_tools", (string?)e["resourceName"]);
        Assert.Equal("report-user", (string?)e["identifyActorGivenId"]);
        Assert.Equal("""{"role":"Developer"}""", Json(e["identifyActorData"]));
    }

    // v2/hooks.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Runs_identify_on_the_tool_call_and_stamps_the_identity_onto_that_event(string? protocolVersion)
    {
        var identifyCalled = false;
        var userId = "user-" + Guid.NewGuid();
        var userData = new JsonObject { ["name"] = "Test User " + Guid.NewGuid(), ["email"] = "test-" + Guid.NewGuid() + "@example.com" };
        var expected = userData.ToJsonString();
        await using var h = Notes(new AgentCatOptions
        {
            Identify = (call, _) =>
            {
                identifyCalled = call.Request is not null;
                return Identity(userId, userData: userData);
            },
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_note", """{"text":"hello","context":"identification test"}""");
        Assert.Contains("Added note", TextAt(result, 1), StringComparison.Ordinal);
        Assert.True(identifyCalled);
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "agentcat:identify");
        var e = CallEvent(events);
        Assert.Equal("add_note", (string?)e["resourceName"]);
        Assert.Equal(userId, (string?)e["identifyActorGivenId"]);
        Assert.Equal(expected, Json(e["identifyActorData"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Runs_identify_on_every_tool_call_and_stamps_every_event_with_no_dedup(string? protocolVersion)
    {
        var count = 0;
        var userId = "user-" + Guid.NewGuid();
        await using var h = Notes(new AgentCatOptions
        {
            Identify = (_, _) =>
            {
                Interlocked.Increment(ref count);
                return Identity(userId, userData: new JsonObject { ["name"] = "Another User" });
            },
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        foreach (var text in new[] { "first", "second", "third" })
        {
            await h.CallToolAsync("add_note", "{\"text\":" + Quote(text) + ",\"context\":\"no-dedup test\"}");
        }

        Assert.Equal(3, Volatile.Read(ref count));
        var calls = CallEvents(await h.FlushAsync());
        Assert.Equal(3, calls.Length);
        Assert.All(calls, e =>
        {
            Assert.Equal(userId, (string?)e["identifyActorGivenId"]);
            Assert.Equal("""{"name":"Another User"}""", Json(e["identifyActorData"]));
        });
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Re_resolves_identity_per_request_so_a_changed_identity_lands_on_the_next_event(string? protocolVersion)
    {
        var identities = new[]
        {
            new UserIdentity("event-user-a") { UserName = "First", UserData = new JsonObject { ["seat"] = "a" } },
            new UserIdentity("event-user-b") { UserName = "Second", UserData = new JsonObject { ["seat"] = "b" } },
        };
        var call = -1;
        await using var h = Notes(new AgentCatOptions { Identify = (_, _) => new ValueTask<UserIdentity?>(identities[Interlocked.Increment(ref call)]) });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_note", """{"text":"First call","context":"identity churn test"}""");
        await h.FlushAsync();
        await h.CallToolAsync("add_note", """{"text":"Second call","context":"identity churn test"}""");
        var calls = CallEvents(await h.FlushAsync());

        Assert.Equal(2, calls.Length);
        Assert.Equal("event-user-a", (string?)calls[0]["identifyActorGivenId"]);
        Assert.Equal("First", (string?)calls[0]["identifyActorName"]);
        Assert.Equal("""{"seat":"a"}""", Json(calls[0]["identifyActorData"]));
        Assert.Equal("event-user-b", (string?)calls[1]["identifyActorGivenId"]);
        Assert.Equal("Second", (string?)calls[1]["identifyActorName"]);
        Assert.Equal("""{"seat":"b"}""", Json(calls[1]["identifyActorData"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Publishes_anonymously_when_identify_returns_null(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions { Identify = (_, _) => new ValueTask<UserIdentity?>((UserIdentity?)null) });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_note", """{"text":"anon","context":"null identity test"}""");

        Assert.Contains("Added note", TextAt(result, 1), StringComparison.Ordinal);
        AssertAnonymous(CallEvent(await h.FlushAsync()));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Degrades_to_anonymous_when_identify_throws_the_tool_call_still_succeeds(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions
        {
            Identify = async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("Failed to identify user");
            },
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_note", """{"text":"err","context":"identify error test"}""");

        Assert.Contains("Added note", TextAt(result, 1), StringComparison.Ordinal);
        AssertAnonymous(CallEvent(await h.FlushAsync()));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Handles_identify_returning_invalid_data_by_leaving_actor_fields_empty(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions { Identify = (_, _) => new ValueTask<UserIdentity?>(new UserIdentity(null!)) });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_note", """{"text":"invalid","context":"invalid identity test"}""");

        Assert.Contains("Added note", TextAt(result, 1), StringComparison.Ordinal);
        AssertAnonymous(CallEvent(await h.FlushAsync()));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Returns_the_call_while_async_identify_is_still_in_flight_identity_lands_in_the_background(string? protocolVersion)
    {
        var lookup = Signal<bool>();
        var completed = false;
        await using var h = Notes(new AgentCatOptions
        {
            Identify = async (_, _) =>
            {
                await lookup.Task;
                Volatile.Write(ref completed, true);
                return new UserIdentity("async-user") { UserData = new JsonObject { ["source"] = "async-lookup" } };
            },
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_note", """{"text":"async","context":"async identify test"}""");
        Assert.Contains("Added note", TextAt(result, 1), StringComparison.Ordinal);
        Assert.False(Volatile.Read(ref completed));
        lookup.SetResult(true);
        var e = CallEvent(await h.FlushAsync());

        Assert.True(Volatile.Read(ref completed));
        Assert.Equal("async-user", (string?)e["identifyActorGivenId"]);
        Assert.Equal("""{"source":"async-lookup"}""", Json(e["identifyActorData"]));
        Assert.True((int?)e["duration"] < 95);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Does_not_run_identify_or_publish_an_event_for_tools_list(string? protocolVersion)
    {
        var identifyCalled = false;
        await using var h = Notes(new AgentCatOptions
        {
            Identify = (_, _) =>
            {
                identifyCalled = true;
                return Identity("list-user");
            },
        });
        await h.StartAsync(protocolVersion, withServices: true);

        await h.ListToolsAsync();

        Assert.False(identifyCalled);
        Assert.Empty(await h.FlushAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Attaches_customer_tags_SDK_handle_tags_ride_alongside(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions
        {
            EnableAgentTracking = true,
            EventTags = (_, _) => Tags(("env", "test"), ("trace_id", "abc-123")),
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_note", "{\"text\":\"tags\",\"context\":\"tag test\",\"agent_id\":" + Quote(Agent) + "}");
        var e = CallEvent(await h.FlushAsync());

        AssertCustomerTags(e, "env=test", "trace_id=abc-123");
        Assert.Equal("minted", Tag(e, SourceTag));
        Assert.Equal("supplied", Tag(e, AgentSourceTag));
        Assert.Equal(Agent, Tag(e, AgentTag));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task SDK_tag_wins_when_a_customer_tag_collides_with_an_SDK_tag_name(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions { EventTags = (_, _) => Tags((SourceTag, "customer-value"), ("env", "test")) });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_note", "{\"text\":\"collision\",\"context\":\"tag test\",\"session_id\":" + Quote(Sid("supplied")) + "}");
        var e = CallEvent(await h.FlushAsync());

        Assert.Equal("supplied", Tag(e, SourceTag));
        AssertCustomerTags(e, "env=test");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Does_not_block_tool_calls_when_the_callback_throws_only_SDK_tags_remain(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions { EventTags = (_, _) => throw new InvalidOperationException("Tags callback failed") });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_note", """{"text":"throw","context":"tag test"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Contains("Added note", TextAt(result, 1), StringComparison.Ordinal);
        Assert.Empty(CustomerTags(e));
        Assert.True(HasTag(e, SourceTag));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Handles_null_return_from_the_callback(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions { EventTags = (_, _) => new ValueTask<IReadOnlyDictionary<string, string>?>((IReadOnlyDictionary<string, string>?)null) });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_note", """{"text":"null","context":"tag test"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Empty(CustomerTags(e));
        Assert.True(HasTag(e, SourceTag));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Validates_tags_from_the_callback_dropping_invalid_keys(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions { EventTags = (_, _) => Tags(("valid", "value"), ("invalid!", "value")) });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_note", """{"text":"validate","context":"tag test"}""");
        var e = CallEvent(await h.FlushAsync());

        AssertCustomerTags(e, "valid=value");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Attaches_properties_to_tool_call_events(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions
        {
            EventProperties = (_, _) => Properties("""{"device":"desktop","feature_flags":["dark_mode"],"nested":{"key":"value"}}"""),
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_note", """{"text":"props","context":"props test"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Equal("""{"device":"desktop","feature_flags":["dark_mode"],"nested":{"key":"value"}}""", Json(e["properties"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Does_not_block_tool_calls_when_the_callback_throws(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions { EventProperties = (_, _) => throw new InvalidOperationException("Properties callback failed") });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_note", """{"text":"throw","context":"props test"}""");

        Assert.Contains("Added note", TextAt(result, 1), StringComparison.Ordinal);
        Assert.False(CallEvent(await h.FlushAsync()).ContainsKey("properties"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Omits_properties_when_the_callback_returns_null(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions { EventProperties = (_, _) => new ValueTask<JsonObject?>((JsonObject?)null) });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_note", """{"text":"null","context":"props test"}""");

        Assert.False(CallEvent(await h.FlushAsync()).ContainsKey("properties"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Attaches_both_tags_and_properties_to_the_same_event(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions
        {
            EventTags = (_, _) => Tags(("env", "test")),
            EventProperties = (_, _) => Properties("""{"device":"mobile"}"""),
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_note", """{"text":"both","context":"combined test"}""");
        var e = CallEvent(await h.FlushAsync());

        AssertCustomerTags(e, "env=test");
        Assert.Equal("""{"device":"mobile"}""", Json(e["properties"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Adds_neither_tags_nor_properties_when_no_callbacks_are_configured(string? protocolVersion)
    {
        await using var h = Notes(new AgentCatOptions());
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_note", """{"text":"bare","context":"no callback test"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Empty(CustomerTags(e));
        Assert.True(HasTag(e, SourceTag));
        Assert.False(e.ContainsKey("properties"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Passes_the_raw_request_and_the_v2_ServerContext_to_every_hook(string? protocolVersion)
    {
        var seen = new Dictionary<string, AgentCatToolCall>();
        void Record(string name, AgentCatToolCall call)
        {
            lock (seen)
            {
                seen[name] = call;
            }
        }

        await using var h = Notes(new AgentCatOptions
        {
            Identify = (call, _) =>
            {
                Record("identify", call);
                return Identity("ctx-user");
            },
            EventTags = (call, _) =>
            {
                Record("eventTags", call);
                return Tags(("env", "test"));
            },
            EventProperties = (call, _) =>
            {
                Record("eventProperties", call);
                return Properties("""{"device":"desktop"}""");
            },
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_note", "{\"text\":\"ctx\",\"context\":\"extra shape test\",\"session_id\":" + Quote(Sid("ctx")) + "}");
        var e = CallEvent(await h.FlushAsync());

        AssertSeq(seen.Keys.OrderBy(k => k, StringComparer.Ordinal), "eventProperties", "eventTags", "identify");
        var call = seen["identify"];
        foreach (var other in new[] { seen["eventTags"], seen["eventProperties"] })
        {
            Assert.Same(call.Request, other.Request);
            Assert.Same(call.Arguments, other.Arguments);
            Assert.Equal(call.Meta?.ToJsonString(), other.Meta?.ToJsonString());
            Assert.True(call.Meta is null || !ReferenceEquals(call.Meta, other.Meta));
        }

        Assert.Equal("add_note", call.ToolName);
        Assert.Equal("ctx", call.Arguments["text"].GetString());
        Assert.Equal("extra shape test", call.Arguments["context"].GetString());
        Assert.Equal(Sid("ctx"), call.Arguments["session_id"].GetString());
        Assert.NotNull(call.Request);
        Assert.NotNull(call.Request.JsonRpcRequest.Id.Id);
        Assert.NotNull(call.Request.Services);
        Assert.Equal((string?)e["clientName"], call.ClientName);
        Assert.Equal((string?)e["clientVersion"], call.ClientVersion);
        Assert.Equal(h.Client.NegotiatedProtocolVersion, call.ProtocolVersion);
        Assert.Equal(h.Server.SessionId, call.TransportSessionId);
        Assert.Null(call.User);
        Assert.Empty(call.Headers);
    }

    // v2/late-registration.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Stamps_resolved_identity_onto_events_from_late_registered_tools(string? protocolVersion)
    {
        var identifyCalled = false;
        await using var h = new EngineHarness(options: new AgentCatOptions
        {
            Identify = (call, _) =>
            {
                identifyCalled = call.Request is not null;
                return Identity("post-track-user", userData: new JsonObject { ["name"] = "Post Track" });
            },
        });
        h.Configure(o =>
        {
            h.Install(o);
            o.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
            o.ToolCollection.Add(McpServerTool.Create(
                (string message) => "Processed: " + message,
                new McpServerToolCreateOptions { Name = "post_track_tool", Description = "A tool added after tracking was enabled" }));
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("post_track_tool", """{"message":"post-track identification","context":"verifying identify on dynamically added tools"}""");
        Assert.Contains("post-track identification", TextAt(result, 1), StringComparison.Ordinal);
        Assert.True(identifyCalled);
        var e = Assert.Single(await h.FlushAsync());

        Assert.Equal("post_track_tool", (string?)e["resourceName"]);
        Assert.Equal("post-track-user", (string?)e["identifyActorGivenId"]);
        Assert.Equal("""{"name":"Post Track"}""", Json(e["identifyActorData"]));
    }

    // v2/pipeline.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Applies_the_string_redaction_hook_to_a_v2_captured_event(string? protocolVersion)
    {
        await using var h = new EngineHarness(options: new AgentCatOptions
        {
            RedactSensitiveInformation = (text, _) => new ValueTask<string>(text.Replace("secret-sauce", "[REDACTED]")),
        });
        h.AddTool((string query) => "result for " + query + ": secret-sauce", "leaky", "Leaks");
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("leaky", "{\"query\":\"find the secret-sauce\",\"context\":\"pipeline redaction test\",\"session_id\":" + Quote(Sid("redact")) + "}");
        var e = CallEvent(await h.FlushAsync());

        var responseText = (string?)e["response"]?["content"]?[0]?["text"];
        Assert.Contains("[REDACTED]", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-sauce", responseText, StringComparison.Ordinal);
        Assert.Equal("find the [REDACTED]", (string?)RawArgs(e)?["query"]);
    }

    // v2/session-modes.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Advertises_no_session_id_param_agent_id_stays_available(string? protocolVersion)
    {
        await using var h = Echo(new AgentCatOptions
        {
            ResolveSessionId = (_, _) => new ValueTask<string?>("customer-correlation-7"),
            EnableAgentTracking = true,
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var tools = (await h.ListToolsAsync()).Tools;
        var echo = Assert.Single(tools, t => t.Name == "echo").InputSchema;
        var required = echo.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.False(echo.GetProperty("properties").TryGetProperty("session_id", out _));
        Assert.True(echo.GetProperty("properties").TryGetProperty("agent_id", out _));
        Assert.Contains("agent_id", required);
        Assert.DoesNotContain("session_id", required);
        var gmt = Assert.Single(tools, t => t.Name == "get_more_tools").InputSchema.GetProperty("properties");
        Assert.False(gmt.TryGetProperty("session_id", out _));
        Assert.True(gmt.TryGetProperty("agent_id", out _));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Derives_a_deterministic_ses__id_same_hook_value_project_same_id_across_calls_and_instances(string? protocolVersion)
    {
        string firstId;
        await using (var h = Echo(new AgentCatOptions { ResolveSessionId = (_, _) => new ValueTask<string?>("customer-correlation-7") }))
        {
            await h.StartAsync(protocolVersion, withServices: true);
            await h.ListToolsAsync();
            await h.CallToolAsync("echo", """{"msg":"one","context":"hook determinism"}""");
            await h.CallToolAsync("echo", """{"msg":"two","context":"hook determinism"}""");
            var events = await h.FlushAsync();

            Assert.Equal(2, events.Count);
            firstId = (string)events[0]["sessionId"]!;
            Assert.StartsWith("ses_", firstId, StringComparison.Ordinal);
            Assert.Equal(firstId, (string?)events[1]["sessionId"]);
            Assert.Equal("hook", Tag(events[0], SourceTag));
            Assert.Equal(SessionIds.Derive("customer-correlation-7", "proj_test"), firstId);
        }

        await using (var h = Echo(new AgentCatOptions { ResolveSessionId = (_, _) => new ValueTask<string?>("customer-correlation-7") }))
        {
            await h.StartAsync(protocolVersion, withServices: true);
            await h.ListToolsAsync();
            await h.CallToolAsync("echo", """{"msg":"three","context":"hook determinism"}""");

            Assert.Equal(firstId, (string?)Assert.Single(await h.FlushAsync())["sessionId"]);
        }

        await using (var h = Echo(new AgentCatOptions { ResolveSessionId = (_, _) => new ValueTask<string?>("customer-correlation-8") }))
        {
            await h.StartAsync(protocolVersion, withServices: true);
            await h.ListToolsAsync();
            await h.CallToolAsync("echo", """{"msg":"four","context":"hook determinism"}""");
            var fourth = (string?)Assert.Single(await h.FlushAsync())["sessionId"];

            Assert.StartsWith("ses_", fourth, StringComparison.Ordinal);
            Assert.NotEqual(firstId, fourth);
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Emits_no_mint_back_in_hook_mode_a_supplied_agent_id_is_tagged_as_supplied(string? protocolVersion)
    {
        await using var h = Echo(new AgentCatOptions
        {
            ResolveSessionId = (_, _) => new ValueTask<string?>("customer-correlation-7"),
            EnableAgentTracking = true,
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", "{\"msg\":\"hi\",\"context\":\"hook mint-back\",\"agent_id\":" + Quote(Agent) + "}");
        var e = CallEvent(await h.FlushAsync());

        Assert.Null(MintBackOf(result));
        Assert.Equal(Agent, Tag(e, AgentTag));
        Assert.Equal("supplied", Tag(e, AgentSourceTag));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Hook_mode_tracking_on_omitted_agent_id_no_mint_back_at_all_no_agent_tags(string? protocolVersion)
    {
        await using var h = Echo(new AgentCatOptions
        {
            ResolveSessionId = (_, _) => new ValueTask<string?>("customer-correlation-7"),
            EnableAgentTracking = true,
        });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"hook, no agent supplied"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Null(MintBackOf(result));
        Assert.Null(result.StructuredContent);
        Assert.False(HasTag(e, AgentTag));
        Assert.False(HasTag(e, AgentSourceTag));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Mints_silently_on_hook_null_fresh_ses__per_call_minted_source_no_mint_back(string? protocolVersion)
    {
        await using var h = Echo(new AgentCatOptions { ResolveSessionId = (_, _) => new ValueTask<string?>((string?)null) });
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var first = await h.CallToolAsync("echo", """{"msg":"one","context":"hook null"}""");
        var second = await h.CallToolAsync("echo", """{"msg":"two","context":"hook null"}""");
        var events = await h.FlushAsync();

        Assert.Null(MintBackOf(first));
        Assert.Null(MintBackOf(second));
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.StartsWith("ses_", (string?)e["sessionId"], StringComparison.Ordinal);
            Assert.Equal("minted", Tag(e, SourceTag));
        });
        Assert.NotEqual((string?)events[0]["sessionId"], (string?)events[1]["sessionId"]);
    }

    // The hook budget, request services and redaction scope.

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Hanging_identify_publishes_without_identity_after_the_budget(string? protocolVersion)
    {
        var started = Signal<bool>();
        var never = Signal<UserIdentity?>();
        var hookToken = CancellationToken.None;
        await using var h = Todos(new AgentCatOptions
        {
            Identify = (_, ct) =>
            {
                hookToken = ct;
                started.TrySetResult(true);
                return new ValueTask<UserIdentity?>(never.Task);
            },
        });
        await h.StartAsync(protocolVersion);
        h.Tracker!.Settings.PendingHookBudget = TimeSpan.FromMilliseconds(200);

        var result = await h.CallToolAsync("add_todo", """{"text":"Hanging identify","context":"budget test"}""");

        Assert.Contains("Added todo", TextAt(result, 1), StringComparison.Ordinal);
        Assert.True(started.Task.IsCompleted);
        Assert.False(never.Task.IsCompleted);
        var e = CallEvent(await h.FlushAsync());
        AssertAnonymous(e);
        Assert.Single(h.Log.Lines, line => line == BudgetLine("identify", 200));
        Assert.DoesNotContain(h.Log.Lines, line => line.StartsWith("Error: identify callback threw", StringComparison.Ordinal));
        Assert.True(hookToken.CanBeCanceled);
        Assert.True(hookToken.IsCancellationRequested);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Each_hook_has_its_own_budget_and_they_run_concurrently(string? protocolVersion)
    {
        var identifyStarted = Signal<bool>();
        var tagsStarted = Signal<bool>();
        await using (var h = Todos(new AgentCatOptions
        {
            ResolveSessionId = (_, _) => new ValueTask<string?>(Signal<string?>().Task),
            Identify = async (_, _) =>
            {
                identifyStarted.TrySetResult(true);
                await tagsStarted.Task;
                return new UserIdentity("both-ran");
            },
            EventTags = async (_, _) =>
            {
                tagsStarted.TrySetResult(true);
                await identifyStarted.Task;
                return new Dictionary<string, string> { ["env"] = "test" };
            },
            EventProperties = (_, _) => new ValueTask<JsonObject?>(Signal<JsonObject?>().Task),
        }))
        {
            await h.StartAsync(protocolVersion);
            h.Tracker!.Settings.PendingHookBudget = TimeSpan.FromMilliseconds(300);

            await h.CallToolAsync("add_todo", """{"text":"mixed","context":"budget test"}""");
            var e = CallEvent(await h.FlushAsync());

            Assert.Equal("both-ran", (string?)e["identifyActorGivenId"]);
            AssertCustomerTags(e, "env=test");
            Assert.Equal("minted", Tag(e, SourceTag));
            Assert.StartsWith("ses_", (string?)e["sessionId"], StringComparison.Ordinal);
            Assert.False(e.ContainsKey("properties"));
            Assert.Single(h.Log.Lines, line => line == BudgetLine("resolveSessionId", 300));
            Assert.Single(h.Log.Lines, line => line == BudgetLine("eventProperties", 300));
            Assert.DoesNotContain(h.Log.Lines, line => line.Contains("identify hook") || line.Contains("eventTags hook"));
        }

        // The budgets end when the test says so: all four must be running at once before any of them ends, which a
        // stalled runner cannot fake and one budget after another cannot reach.
        var budget = TimeSpan.FromMilliseconds(500);
        var labels = new[] { "resolveSessionId", "identify", "eventTags", "eventProperties" };
        var hooksStarted = labels.ToDictionary(label => label, _ => Signal<bool>());
        var budgets = new List<(TimeSpan Length, TaskCompletionSource<bool> End)>();
        var allBudgetsRunning = Signal<bool>();
        ValueTask<T?> Hang<T>(string label)
            where T : class
        {
            hooksStarted[label].TrySetResult(true);
            return new ValueTask<T?>(Signal<T?>().Task);
        }

        await using (var h = Todos(new AgentCatOptions
        {
            ResolveSessionId = (_, _) => Hang<string>("resolveSessionId"),
            Identify = (_, _) => Hang<UserIdentity>("identify"),
            EventTags = (_, _) => Hang<IReadOnlyDictionary<string, string>>("eventTags"),
            EventProperties = (_, _) => Hang<JsonObject>("eventProperties"),
        }))
        {
            await h.StartAsync(protocolVersion);
            h.Tracker!.Settings.PendingHookBudget = budget;
            h.Tracker.Settings.HookBudgetDelay = (length, _) =>
            {
                var end = Signal<bool>();
                lock (budgets)
                {
                    budgets.Add((length, end));
                    if (budgets.Count == labels.Length)
                    {
                        allBudgetsRunning.TrySetResult(true);
                    }
                }

                return end.Task;
            };

            await h.CallToolAsync("add_todo", """{"text":"all hang","context":"budget test"}""");
            await WithinAsync(allBudgetsRunning.Task);

            Assert.All(hooksStarted.Values, started => Assert.True(started.Task.IsCompleted));
            Assert.DoesNotContain(h.Log.Lines, line => line.Contains(" hook did not settle within "));
            Assert.All(budgets, b => Assert.Equal(budget, b.Length));
            lock (budgets)
            {
                foreach (var b in budgets)
                {
                    b.End.SetResult(true);
                }
            }

            var e = CallEvent(await h.FlushAsync());
            AssertAnonymous(e);
            Assert.Equal("minted", Tag(e, SourceTag));
            Assert.Equal(labels.Length, budgets.Count);
            foreach (var label in labels)
            {
                Assert.Single(h.Log.Lines, line => line == BudgetLine(label, 500));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_hook_that_observes_its_token_stops_at_the_budget(string? protocolVersion)
    {
        var stopped = Signal<bool>();
        await using var h = Todos(new AgentCatOptions
        {
            Identify = async (_, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return new UserIdentity("never");
                }
                finally
                {
                    stopped.TrySetResult(true);
                }
            },
        });
        await h.StartAsync(protocolVersion);
        h.Tracker!.Settings.PendingHookBudget = TimeSpan.FromMilliseconds(200);

        await h.CallToolAsync("add_todo", """{"text":"observes the token","context":"budget test"}""");
        var e = CallEvent(await h.FlushAsync());
        await WithinAsync(stopped.Task);

        AssertAnonymous(e);
        Assert.Single(h.Log.Lines, line => line == BudgetLine("identify", 200));
        Assert.DoesNotContain(h.Log.Lines, line => line.StartsWith("Error: identify callback threw", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Dropped_events_cancel_the_hook_token(string? protocolVersion)
    {
        var tokens = new List<CancellationToken>();
        var options = new AgentCatOptions
        {
            Identify = (_, ct) =>
            {
                lock (tokens)
                {
                    tokens.Add(ct);
                }

                return new ValueTask<UserIdentity?>(Signal<UserIdentity?>().Task);
            },
        };
        await using var h = new EngineHarness("proj_test", options, new PipelineSettings { Concurrency = 0, MaxQueueSize = 1 });
        h.AddHandlerTool(EchoTool, (_, _) => new ValueTask<CallToolResult>(Mcp.Result("""{"content":[{"type":"text","text":"ok"}]}""")));
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("echo", """{"text":"first"}""");
        lock (tokens)
        {
            Assert.False(Assert.Single(tokens).IsCancellationRequested);
        }

        await h.CallToolAsync("echo", """{"text":"second"}""");

        lock (tokens)
        {
            Assert.Equal(2, tokens.Count);
            Assert.True(tokens[0].IsCancellationRequested);
            Assert.False(tokens[1].IsCancellationRequested);
        }

        Assert.Contains("Event queue full, dropping oldest event", h.Log.Lines);
        Assert.Equal(1, h.Runtime.Pipeline.QueueLength);
    }

    [Theory]
    [InlineData("finish", null)]
    [InlineData("finish", McpHarness.LegacyProtocol)]
    [InlineData("fail", null)]
    [InlineData("fail", McpHarness.LegacyProtocol)]
    public async Task A_seam_failure_after_the_hooks_started_cancels_the_hook_token(string stage, string? protocolVersion)
    {
        var token = Signal<CancellationToken>();
        var options = new AgentCatOptions
        {
            Identify = (_, ct) =>
            {
                token.TrySetResult(ct);
                return new ValueTask<UserIdentity?>(Signal<UserIdentity?>().Task);
            },
        };
        await using var h = LowLevel(options);
        await h.StartAsync(protocolVersion);
        h.Tracker!.OnCallStage = s =>
        {
            if (s == stage)
            {
                throw new InvalidOperationException("agentcat " + stage + " failure");
            }
        };

        if (stage == "finish")
        {
            await h.CallToolAsync("echo", """{"text":"hi"}""");
        }
        else
        {
            await h.CallToolAsync("echo", """{"text":"throw"}""");
        }

        await WithinAsync(token.Task);
        Assert.True((await token.Task).IsCancellationRequested);
        Assert.Empty(h.Sender.Sent);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Pipeline_cancellation_while_a_hook_is_pending(string? protocolVersion)
    {
        var started = Signal<CancellationToken>();
        var options = new AgentCatOptions
        {
            Identify = (_, ct) =>
            {
                started.TrySetResult(ct);
                return new ValueTask<UserIdentity?>(Signal<UserIdentity?>().Task);
            },
        };
        await using var h = LowLevel(options);
        await h.StartAsync(protocolVersion);
        await h.CallToolAsync("echo", """{"text":"hi"}""");
        await WithinAsync(started.Task);
        var token = await started.Task;
        var pipeline = h.Runtime.Pipeline;
        Assert.True(SpinWait.SpinUntil(() => pipeline.QueueLength == 0 && pipeline.ActiveCount == 1, FakeAgentCatApi.WaitBound));
        Assert.False(token.IsCancellationRequested);

        pipeline.Dispose();

        Assert.True(SpinWait.SpinUntil(() => token.IsCancellationRequested, FakeAgentCatApi.WaitBound));
        Assert.True(SpinWait.SpinUntil(() => pipeline.ActiveCount == 0, FakeAgentCatApi.WaitBound));
        Assert.Equal(0, h.Sender.Calls);
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("hook did not settle", StringComparison.Ordinal)
            || l.StartsWith("Failed to resolve pending hook results", StringComparison.Ordinal)
            || l.StartsWith("Error: identify callback threw", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Pipeline_stages_never_run_on_the_thread_that_completes_a_hook(string? protocolVersion)
    {
        var gate = new TaskCompletionSource<UserIdentity?>();
        var hookStarted = Signal<bool>();
        var completingThread = 0;
        var redactThread = 0;
        var sendThread = 0;
        await using var h = Todos(new AgentCatOptions
        {
            Identify = (_, _) =>
            {
                hookStarted.TrySetResult(true);
                return new ValueTask<UserIdentity?>(gate.Task);
            },
            RedactEvent = (e, _) =>
            {
                Volatile.Write(ref redactThread, Environment.CurrentManagedThreadId);
                return new ValueTask<AgentCatEvent?>(e);
            },
        });
        h.Sender.OnSend = _ => Volatile.Write(ref sendThread, Environment.CurrentManagedThreadId);
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"late identity","context":"thread test"}""");
        await WithinAsync(hookStarted.Task);
        var pipeline = h.Runtime.Pipeline;
        Assert.True(SpinWait.SpinUntil(() => pipeline.QueueLength == 0 && pipeline.ActiveCount == 1, FakeAgentCatApi.WaitBound));
        var completer = new Thread(() =>
        {
            Volatile.Write(ref completingThread, Environment.CurrentManagedThreadId);
            gate.SetResult(new UserIdentity("late"));
        });
        completer.Start();
        Assert.True(completer.Join(FakeAgentCatApi.WaitBound));
        var e = CallEvent(await h.FlushAsync());

        Assert.Equal("late", (string?)e["identifyActorGivenId"]);
        Assert.NotEqual(0, Volatile.Read(ref redactThread));
        Assert.NotEqual(0, Volatile.Read(ref sendThread));
        Assert.NotEqual(Volatile.Read(ref completingThread), Volatile.Read(ref redactThread));
        Assert.NotEqual(Volatile.Read(ref completingThread), Volatile.Read(ref sendThread));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Snapshot_protocol_version_falls_back_while_the_protocol_tag_stays_meta_only(string? protocolVersion)
    {
        AgentCatToolCall? seen = null;
        await using var h = Todos(new AgentCatOptions
        {
            Identify = (call, _) =>
            {
                seen = call;
                return Identity("u");
            },
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"x"}""");
        var e = CallEvent(await h.FlushAsync());

        var metaVersion = (string?)e["parameters"]?["request"]?["params"]?["_meta"]?["io.modelcontextprotocol/protocolVersion"];
        Assert.NotNull(seen);
        Assert.NotNull(seen!.ProtocolVersion);
        Assert.Equal(h.Client.NegotiatedProtocolVersion, seen.ProtocolVersion);
        if (McpHarness.McpMajor == 1 || protocolVersion == McpHarness.LegacyProtocol)
        {
            Assert.Null(metaVersion);
        }

        if (metaVersion is null)
        {
            Assert.False(HasTag(e, ProtocolTag));
        }
        else
        {
            Assert.Equal(metaVersion, Tag(e, ProtocolTag));
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Hooks_tolerate_disposed_request_services(string? protocolVersion)
    {
        var release = Signal<bool>();
        var resolvedOnTheRequest = false;
        await using var h = Notes(new AgentCatOptions
        {
            Identify = async (call, _) =>
            {
                resolvedOnTheRequest = call.Request.Services!.GetRequiredService<ScopedProbe>() is not null;
                await release.Task;
                call.Request.Services!.GetRequiredService<ScopedProbe>();
                return new UserIdentity("never");
            },
        });
        h.ConfigureServices(services => services.AddScoped<ScopedProbe>());
        await h.StartAsync(protocolVersion, withServices: true);
        Assert.True(h.ServerOptions.ScopeRequests);

        var result = await h.CallToolAsync("add_note", """{"text":"scoped","context":"disposed services test"}""");
        release.SetResult(true);
        var e = CallEvent(await h.FlushAsync());

        Assert.True(resolvedOnTheRequest);
        Assert.Contains("Added note: scoped", TextAt(result, 1), StringComparison.Ordinal);
        Assert.NotEqual(true, result.IsError);
        AssertAnonymous(e);
        Assert.Null(e["isError"]);
        Assert.Equal("Added note: scoped", (string?)e["response"]?["content"]?[0]?["text"]);
        Assert.Single(h.Log.Lines, line => line.StartsWith("Error: identify callback threw - ObjectDisposedException: ", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Identify_actor_data_is_redacted_end_to_end(string? protocolVersion)
    {
        var userData = new JsonObject { ["email"] = "ada@example.com", ["plan"] = "pro", ["nested"] = new JsonObject { ["backup"] = "ada@backup.example" } };
        var original = userData.ToJsonString();
        await using var h = Todos(new AgentCatOptions
        {
            RedactSensitiveInformation = (text, _) => new ValueTask<string>(text.Any(c => c == '@') ? "[REDACTED]" : text),
            Identify = (_, _) => Identity("ada@corp", "Ada@Home", userData),
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"x","context":"redaction scope test"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Equal("""{"email":"[REDACTED]","plan":"pro","nested":{"backup":"[REDACTED]"}}""", Json(e["identifyActorData"]));
        Assert.Equal("ada@corp", (string?)e["identifyActorGivenId"]);
        Assert.Equal("Ada@Home", (string?)e["identifyActorName"]);
        Assert.Equal(original, userData.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_hook_whose_error_line_fails_settles_with_the_background_failure_line(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions
        {
            EventTags = async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("tags down");
            },
            EventProperties = (_, _) => Properties("""{"kept":true}"""),
        });
        h.Log.ThrowWhen = line => line.StartsWith("eventTags callback error", StringComparison.Ordinal);
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"x"}""");
        var e = CallEvent(await h.FlushAsync());

        Assert.Empty(CustomerTags(e));
        Assert.Equal("""{"kept":true}""", Json(e["properties"]));
        Assert.Contains("Warning: eventTags hook failed in background - InvalidOperationException: log boom", h.Log.Lines);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Hooks_never_run_when_tracing_is_off(string? protocolVersion)
    {
        var calls = 0;
        await using var h = Todos(new AgentCatOptions
        {
            EnableTracing = false,
            ResolveSessionId = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return new ValueTask<string?>("x");
            },
            Identify = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Identity("x");
            },
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"x"}""");

        Assert.Empty(await h.FlushAsync());
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    private sealed class ScopedProbe
    {
    }
}
