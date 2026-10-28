using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentCat.Internal.Engine;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Engine;

public class RebuildAndMrtrTests
{
    private const string ToolCall = "mcp:tools/call";

    private const string Agent = "opus-4.80-1m|claude-code|k3n9x";

    private const string RebuiltLine = "Rebuilt injection registries on demand (tools/call before tools/list on this instance)";

    private const string RebuildFailedPrefix = "Warning: registry rebuild-on-demand failed - ";

    private const string TracingFailedPrefix = "Warning: AgentCat tracing failed for tool ";

    private const string MrtrTag = "agentcat_mrtr";

    private const string EchoTool =
        """{"name":"echo","description":"echoes","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}""";

    private const string ConfirmTool =
        """{"name":"confirm","description":"asks before acting","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}""";

    private const string NotesTool =
        """{"name":"notes","description":"takes its own context","inputSchema":{"type":"object","properties":{"text":{"type":"string"},"context":{"type":"string","description":"customer's own"}}}}""";

    private const string AnswerRequest =
        """{"inputRequests":{"answer":{"method":"elicitation/create","params":{"message":"Proceed?","requestedSchema":{"type":"object","properties":{"ok":{"type":"boolean"}}}}}},"requestState":"round-1"}""";

    private const string StrictStatsSchema =
        """{"type":"object","properties":{"count":{"type":"number"}},"required":["count"],"additionalProperties":false}""";

    private static readonly JsonSerializerOptions Reflection = new();

    public static TheoryData<string?> Protocols => McpHarness.ProtocolVersions;

    public static TheoryData<string?, bool> Startups => McpHarness.Startups;

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

    private static string Quote(string text) => JsonSerializer.Serialize(text, Reflection);

    private static void AssertSeq(IEnumerable<string?> actual, params string[] expected) => Assert.Equal(expected, actual.ToArray());

    private static string[] Keys(IDictionary<string, JsonElement>? arguments) => arguments is null ? Array.Empty<string>() : arguments.Keys.ToArray();

    private static string? MintBackOf(CallToolResult result)
        => result.Content.Count > 0 && result.Content[0] is TextContentBlock first && first.Text.StartsWith("[session_id", StringComparison.Ordinal)
            ? first.Text
            : null;

    private static int SessionBlocks(CallToolResult result)
        => result.Content.Count(c => c is TextContentBlock t && t.Text.StartsWith("[session_id", StringComparison.Ordinal));

    private static string[] Texts(CallToolResult result) => result.Content.Select(c => (c as TextContentBlock)?.Text ?? "").ToArray();

    private static string HandleFrom(string text, string name) => new Regex(name + ": (\\S+)").Match(text).Groups[1].Value;

    private static JsonObject[] CallEvents(IReadOnlyList<JsonObject> events) => events.Where(e => (string?)e["eventType"] == ToolCall).ToArray();

    private static JsonObject CallEvent(IReadOnlyList<JsonObject> events) => Assert.Single(events, e => (string?)e["eventType"] == ToolCall);

    private static string? Tag(JsonObject e, string key) => (string?)e["tags"]?[key];

    private static bool HasTag(JsonObject e, string key) => e["tags"] is JsonObject tags && tags.ContainsKey(key);

    private static string[] TagKeys(JsonObject e) => e["tags"] is JsonObject tags ? tags.Select(p => p.Key).ToArray() : Array.Empty<string>();

    private static JsonNode? RawParams(JsonObject e) => e["parameters"]?["request"]?["params"];

    private static JsonNode? RawArgs(JsonObject e) => RawParams(e)?["arguments"];

    private static string Json(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ValueTask<CallToolResult> Text(string text)
        => new(Mcp.Result("{\"content\":[{\"type\":\"text\",\"text\":" + Quote(text) + "}]}"));

    private static string[] AgentCatKeys(IDictionary<string, object?>? items)
        => items is null ? Array.Empty<string>() : items.Keys.Where(k => k.StartsWith("agentcat.", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private static string[] Registry(AgentCatTracker tracker)
        => tracker.Registries.InjectedParams
            .Select(p => p.Key + ":" + string.Join(",", p.Value.OrderBy(v => v, StringComparer.Ordinal)))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

    private static McpRequestFilter<CallToolRequestParams, CallToolResult> Around(
        Func<RequestContext<CallToolRequestParams>, McpRequestHandler<CallToolRequestParams, CallToolResult>, CancellationToken, ValueTask<CallToolResult>> filter)
        => next => (context, cancellationToken) => filter(context, next, cancellationToken);

    private static void InstallWithOuterFilter(
        EngineHarness h,
        Func<RequestContext<CallToolRequestParams>, McpRequestHandler<CallToolRequestParams, CallToolResult>, CancellationToken, ValueTask<CallToolResult>> outer)
        => h.Configure(o =>
        {
            h.Install(o);
            o.Filters.Request.CallToolFilters.Insert(0, Around(outer));
        });

    private static EngineHarness Echo(List<Dictionary<string, JsonElement>> received, AgentCatOptions? options = null)
    {
        var h = new EngineHarness(options: options);
        h.AddHandlerTool(EchoTool, (context, _) =>
        {
            var arguments = context.Params?.Arguments;
            lock (received)
            {
                received.Add(arguments is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(arguments));
            }

            var text = arguments is not null && arguments.TryGetValue("text", out var value) ? value.GetString() : null;
            return Text("echo: " + text);
        });
        return h;
    }

    private static EngineHarness LowLevelStructured()
    {
        var h = new EngineHarness(options: new AgentCatOptions { EnableAgentTracking = true });
        h.AddHandlerTool(
            "{\"name\":\"stats\",\"description\":\"returns stats\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}},\"outputSchema\":" + StrictStatsSchema + "}",
            (_, _) => new ValueTask<CallToolResult>(Mcp.Result("""{"content":[{"type":"text","text":"42 things"}],"structuredContent":{"count":42}}""")));
        h.AddHandlerTool(
            """{"name":"poly","description":"composed output schema","inputSchema":{"type":"object","properties":{}},"outputSchema":{"type":"object","oneOf":[{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}]}}""",
            (_, _) => new ValueTask<CallToolResult>(Mcp.Result("""{"content":[{"type":"text","text":"poly"}],"structuredContent":{"a":"x"}}""")));
        return h;
    }

    private static EngineHarness SharedWith(EngineHarness owner)
    {
        var h = new EngineHarness();
        h.AddTool((string msg) => msg, "echo", "Echo");
        h.Configure(o => h.InstallShared(o, owner.Tracker!));
        return h;
    }

    // A second options instance on the owner's tracker with its own handler listing, as a stateless POST builds one.
    private static EngineHarness NotesSharedWith(EngineHarness owner, List<Dictionary<string, JsonElement>> received, StrongBox<int> lists)
    {
        var h = new EngineHarness();
        h.AddHandlerTool(NotesTool, (context, _) =>
        {
            var arguments = context.Params!.Arguments!;
            lock (received)
            {
                received.Add(new Dictionary<string, JsonElement>(arguments));
            }

            return Text("notes: " + arguments["text"].GetString());
        });
        h.Configure(o =>
        {
            var list = o.Handlers.ListToolsHandler!;
            o.Handlers.ListToolsHandler = (context, cancellationToken) =>
            {
                Interlocked.Increment(ref lists.Value);
                return list(context, cancellationToken);
            };
            h.InstallShared(o, owner.Tracker!);
        });
        return h;
    }

    // engine-callwrap.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task IsInputRequiredShape_matches_only_resultType_input_required(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddHandlerTool(ConfirmTool, (context, _) =>
        {
            var text = context.Params?.Arguments?["text"].GetString();
            if (HasRaw(context, "inputResponses"))
            {
                return Text("done after input");
            }

            return text switch
            {
                "real" => throw InputRequired(AnswerRequest),
                "lookalike" => throw new InputRequiredException(),
                _ => Text("complete"),
            };
        });
        Elicit(h);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var complete = await h.CallToolAsync("confirm", """{"text":"complete"}""");
        var lookalike = await h.CallToolAsync("confirm", """{"text":"lookalike"}""");
        CallToolResult? real = null;
        if (McpHarness.McpMajor >= 2)
        {
            real = await h.CallToolAsync("confirm", """{"text":"real"}""");
        }

        Assert.Equal("complete", Texts(complete).Last());
        Assert.True(lookalike.IsError);
        var events = CallEvents(await h.FlushAsync());
        var completeEvent = Assert.Single(events, e => (string?)RawArgs(e)?["text"] == "complete");
        Assert.False(HasTag(completeEvent, MrtrTag));
        var lookalikeEvent = Assert.Single(events, e => (string?)RawArgs(e)?["text"] == "lookalike");
        Assert.False(HasTag(lookalikeEvent, MrtrTag));
        Assert.Equal(true, (bool?)lookalikeEvent["isError"]);
        Assert.EndsWith("+InputRequiredException", (string?)lookalikeEvent["error"]?["type"], StringComparison.Ordinal);
        Assert.Equal(real is null ? 2 : 4, events.Length);
        if (real is not null)
        {
            Assert.Equal("done after input", Texts(real).Last());
            var intermediate = Assert.Single(events, e => (string?)RawArgs(e)?["text"] == "real" && Tag(e, MrtrTag) == "input_required");
            Assert.Null(intermediate["isError"]);
            Assert.Null(intermediate["error"]);
            Assert.Null(intermediate["response"]);
            Assert.Single(events, e => (string?)RawArgs(e)?["text"] == "real" && Tag(e, MrtrTag) == "continuation");
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task MrtrContinuationTags_fires_only_when_inputResponses_ride_the_request(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = Echo(received);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolRawAsync("""{"name":"echo","arguments":{"text":"object"},"inputResponses":{"a":{}}}""");
        await h.CallToolRawAsync("""{"name":"echo","arguments":{"text":"absent"}}""");
        await h.CallToolRawAsync("""{"name":"echo","arguments":{"text":"null"},"inputResponses":null}""");
        if (McpHarness.McpMajor == 1)
        {
            await h.CallToolRawAsync("""{"name":"echo","arguments":{"text":"array"},"inputResponses":[]}""");
            await h.CallToolRawAsync("""{"name":"echo","arguments":{"text":"string"},"inputResponses":"a"}""");
        }

        var events = CallEvents(await h.FlushAsync());
        JsonObject For(string text) => Assert.Single(events, e => (string?)RawArgs(e)?["text"] == text);
        var tagged = For("object");
        Assert.Equal("continuation", Tag(tagged, MrtrTag));
        Assert.Equal(MrtrTag, TagKeys(tagged)[0]);
        Assert.False(HasTag(For("absent"), MrtrTag));
        Assert.False(HasTag(For("null"), MrtrTag));
        if (McpHarness.McpMajor == 1)
        {
            Assert.Equal("continuation", Tag(For("array"), MrtrTag));
            Assert.False(HasTag(For("string"), MrtrTag));
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tags_intermediate_input_required_rounds_and_skips_mint_back_on_them(string? protocolVersion)
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "InputRequiredException (MRTR) exists only on MCP 2.x.");
        var rounds = new List<Round>();
        var inputRequired = new ConcurrentQueue<string>();
        await using var h = Mrtr(rounds);
        h.Configure(o => o.Filters.Message.OutgoingFilters.Add(next => (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcResponse { Result: JsonObject result } && (string?)result["resultType"] == "input_required")
            {
                inputRequired.Enqueue(result.ToJsonString());
            }

            return next(context, cancellationToken);
        }));
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("confirm", """{"text":"go","context":"needs approval"}""");

        Assert.Equal(2, rounds.Count);
        Assert.Equal("confirmed", Texts(result).Last());
        Assert.Equal(1, SessionBlocks(result));
        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(2, events.Length);
        var intermediate = Assert.Single(events, e => Tag(e, MrtrTag) == "input_required");
        Assert.Null(intermediate["isError"]);
        Assert.Null(intermediate["error"]);
        Assert.Null(intermediate["response"]);
        Assert.Null(intermediate["outputTokens"]);
        Assert.Equal("needs approval", (string?)intermediate["userIntent"]);
        Assert.Matches("^ses_", (string?)intermediate["sessionId"]);
        var final = Assert.Single(events, e => Tag(e, MrtrTag) == "continuation");
        Assert.Equal(HandleFrom(MintBackOf(result)!, "session_id"), (string?)final["sessionId"]);
        Assert.NotEqual((string?)intermediate["sessionId"], (string?)final["sessionId"]);
        if (protocolVersion is null)
        {
            var sent = Assert.Single(inputRequired);
            Assert.DoesNotContain("[session_id", sent, StringComparison.Ordinal);
            Assert.DoesNotContain("\"content\"", sent, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(inputRequired);
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tags_continuation_rounds_from_mcpReq_inputResponses(string? protocolVersion)
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "MRTR retries with inputResponses exist only on MCP 2.x.");
        var rounds = new List<Round>();
        await using var h = Mrtr(rounds);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("confirm", "{\"text\":\"go\",\"session_id\":" + Quote(Sid("mrtr")) + "}");

        Assert.Equal(2, rounds.Count);
        Assert.False(rounds[0].Continuation);
        Assert.True(rounds[1].Continuation);
        Assert.All(rounds, r => AssertSeq(r.Arguments, "text"));
        var events = CallEvents(await h.FlushAsync());
        var final = Assert.Single(events, e => Tag(e, MrtrTag) == "continuation");
        Assert.Equal(MrtrTag, TagKeys(final)[0]);
        Assert.Equal("accept", (string?)RawParams(final)?["inputResponses"]?["answer"]?["action"]);
        Assert.Equal(Sid("mrtr"), (string?)RawArgs(final)?["session_id"]);
        Assert.All(events, e => Assert.Equal(Sid("mrtr"), (string?)e["sessionId"]));
        Assert.Single(events, e => Tag(e, MrtrTag) == "input_required");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Bounds_the_rebuild_a_hanging_tools_list_handler_cannot_hang_tools_call(string? protocolVersion)
    {
        var hang = Signal<ListToolsResult>();
        await using var h = Echo(new List<Dictionary<string, JsonElement>>());
        h.Configure(o => o.Handlers.ListToolsHandler = (_, _) => new ValueTask<ListToolsResult>(hang.Task));
        await h.StartAsync(protocolVersion);
        h.Tracker!.RebuildTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            var result = await h.CallToolAsync("echo", """{"text":"hi"}""");

            Assert.Contains("echo: hi", Texts(result));
            Assert.Contains(h.Log.Lines, l => l.Contains("rebuild-on-demand failed"));
        }
        finally
        {
            hang.TrySetResult(Mcp.ListResult("""{"tools":[]}"""));
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Keeps_serving_calls_when_the_list_handler_fails_every_time(string? protocolVersion)
    {
        var lists = 0;
        await using var h = Echo(new List<Dictionary<string, JsonElement>>());
        h.Configure(o => o.Handlers.ListToolsHandler = (_, _) =>
        {
            Interlocked.Increment(ref lists);
            throw new InvalidOperationException("list boom");
        });
        await h.StartAsync(protocolVersion);

        for (var i = 0; i < 2; i++)
        {
            var result = await h.CallToolAsync("echo", """{"text":"hi"}""");
            Assert.Contains("echo: hi", Texts(result));
        }

        Assert.Equal(2, Volatile.Read(ref lists));
        Assert.Equal(2, h.Log.Lines.Count(l => l == RebuildFailedPrefix + "InvalidOperationException: list boom"));
        Assert.DoesNotContain(RebuiltLine, h.Log.Lines);
        Assert.Equal(2, CallEvents(await h.FlushAsync()).Length);
    }

    // context-parameters.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_remove_context_parameter_before_calling_tool_callback(string? protocolVersion)
    {
        Dictionary<string, JsonElement>? captured = null;
        string? seen = null;
        await using var h = new EngineHarness("test-project", new AgentCatOptions { EnableTracing = true });
        h.AddTool(McpServerTool.Create(
            (RequestContext<CallToolRequestParams> request, string testParam) =>
            {
                captured = new Dictionary<string, JsonElement>(request.Params!.Arguments!);
                seen = testParam;
                return "Arguments captured";
            },
            new McpServerToolCreateOptions { Name = "test_context_removal", Description = "Test tool that captures callback arguments" }));
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync(
            "test_context_removal",
            """{"testParam":"test-value","context":"This context should be removed before callback"}""");

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(captured);
        AssertSeq(captured!.Keys, "testParam");
        Assert.Equal("test-value", seen);
        Assert.Single(h.Log.Lines, l => l == RebuiltLine);
    }

    // structured-mintback.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task No_tools_list_ever_no_registry_mirror_applies_a_list_less_client_cannot_be_validating(string? protocolVersion)
    {
        await using var h = LowLevelStructured();
        await h.StartAsync(protocolVersion);

        var stats = await h.CallToolAsync("stats");
        var poly = await h.CallToolAsync("poly");

        Assert.Matches("^ses_", stats.StructuredContent!.Value.GetProperty("mcp_session").GetProperty("session_id").GetString());
        Assert.False(poly.StructuredContent!.Value.TryGetProperty("mcp_session", out _));
        Assert.NotNull(MintBackOf(poly));
        Assert.Single(h.Log.Lines, l => l == RebuiltLine);
    }

    // concurrency.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Each_event_keeps_its_own_session_id_agent_id_and_actor_under_interleaving(string? protocolVersion)
    {
        await using var h = Interleaving();
        await h.StartAsync(protocolVersion);
        await AssertInterleavedAttribution(h, sourceTag: false);
    }

    // v2/concurrency.test.ts (run through the DI startup)

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Each_event_keeps_its_own_session_id_agent_id_and_actor_under_interleaving_v2(string? protocolVersion)
    {
        await using var h = Interleaving();
        await h.StartAsync(protocolVersion, withServices: true);
        await AssertInterleavedAttribution(h, sourceTag: true);
    }

    // v2/era-2026.test.ts (per-request options instances that share one tracker, as stateless HTTP builds them)

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tracks_per_request_instances_and_captures_envelope_identity(string? protocolVersion)
    {
        await using var first = new EngineHarness();
        first.AddTool((string msg) => msg, "echo", "Echo");
        await first.StartAsync(protocolVersion);
        await using var second = SharedWith(first);
        await second.StartAsync(protocolVersion);

        await first.CallToolAsync("echo", """{"msg":"one"}""");
        await second.CallToolAsync("echo", """{"msg":"two"}""");

        Assert.NotSame(first.ServerOptions, second.ServerOptions);
        var events = CallEvents(await first.FlushAsync()).Where(e => (string?)e["resourceName"] == "echo").ToArray();
        Assert.Equal(2, events.Length);
        var protocol = McpHarness.McpMajor >= 2 && protocolVersion is null ? "2026-07-28" : null;
        foreach (var e in events)
        {
            Assert.Equal("harness-client", (string?)e["clientName"]);
            Assert.Equal("3.1.4", (string?)e["clientVersion"]);
            Assert.Equal(protocol, Tag(e, "agentcat_protocol_version"));
        }

        Assert.Empty(CallEvents(await second.FlushAsync()));
        Assert.Single(first.Log.Lines, l => l == RebuiltLine);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Rebuild_on_demand_serves_calls_on_instances_that_never_listed(string? protocolVersion)
    {
        await using var first = new EngineHarness();
        first.AddTool((string msg) => msg, "echo", "Echo");
        await first.StartAsync(protocolVersion);
        await using var second = SharedWith(first);
        await second.StartAsync(protocolVersion);

        var one = await first.CallToolAsync("echo", """{"msg":"solo"}""");
        var two = await second.CallToolAsync("echo", """{"msg":"solo again"}""");

        Assert.Contains("solo", Texts(one)[1], StringComparison.Ordinal);
        Assert.Contains("solo again", Texts(two)[1], StringComparison.Ordinal);
        var events = CallEvents(await first.FlushAsync());
        foreach (var (result, msg) in new[] { (one, "solo"), (two, "solo again") })
        {
            var e = Assert.Single(events, x => (string?)RawArgs(x)?["msg"] == msg);
            Assert.Matches("^ses_", (string?)e["sessionId"]);
            Assert.Equal(HandleFrom(MintBackOf(result)!, "session_id"), (string?)e["sessionId"]);
        }

        Assert.Single(first.Log.Lines, l => l == RebuiltLine);
    }

    // v2/track-lowlevel.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Rebuilds_registries_on_demand_when_tools_call_arrives_before_any_tools_list(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = Echo(received);
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("echo", "{\"text\":\"hi\",\"session_id\":" + Quote(Sid("abc")) + ",\"context\":\"why\"}");

        Assert.Equal(Sid("abc"), (string?)CallEvent(await h.FlushAsync())["sessionId"]);
        AssertSeq(Assert.Single(received).Keys, "text");
        Assert.Contains(RebuiltLine, h.Log.Lines);
    }

    // Calls before any listing, hanging listings, concurrent calls and MRTR rounds

    [Theory]
    [MemberData(nameof(Startups))]
    public async Task First_call_before_any_listing_strips_by_the_rebuilt_registry(string? protocolVersion, bool withServices)
    {
        var handlerArgs = new List<Dictionary<string, JsonElement>>();
        var collectionArgs = new List<Dictionary<string, JsonElement>>();
        var lists = 0;
        await using var h = new EngineHarness(options: new AgentCatOptions { EnableAgentTracking = true });
        h.AddHandlerTool(
            """{"name":"htool","description":"declares its own session_id","inputSchema":{"type":"object","properties":{"text":{"type":"string"},"session_id":{"type":"string","description":"customer's own"}}}}""",
            (context, _) =>
            {
                lock (handlerArgs)
                {
                    handlerArgs.Add(new Dictionary<string, JsonElement>(context.Params!.Arguments!));
                }

                return Text("htool ok");
            });
        h.AddTool(McpServerTool.Create(
            (RequestContext<CallToolRequestParams> request, string text, string context) =>
            {
                lock (collectionArgs)
                {
                    collectionArgs.Add(new Dictionary<string, JsonElement>(request.Params!.Arguments!));
                }

                return "ctool: " + text + "/" + context;
            },
            new McpServerToolCreateOptions { Name = "ctool", Description = "declares its own context" }));
        h.Configure(o => o.Filters.Request.ListToolsFilters.Add(next => (context, cancellationToken) =>
        {
            Interlocked.Increment(ref lists);
            return next(context, cancellationToken);
        }));
        await h.StartAsync(protocolVersion, withServices);

        await h.CallToolAsync("htool", "{\"text\":\"a\",\"session_id\":\"cust-1\",\"agent_id\":" + Quote(Agent) + ",\"context\":\"why\"}");
        var fromCollection = await h.CallToolAsync(
            "ctool",
            "{\"text\":\"b\",\"context\":\"mine\",\"session_id\":" + Quote(Sid("ctool")) + ",\"agent_id\":" + Quote(Agent) + "}");

        Assert.Equal(0, Volatile.Read(ref lists));
        var fromHandler = Assert.Single(handlerArgs);
        AssertSeq(fromHandler.Keys, "text", "session_id");
        Assert.Equal("cust-1", fromHandler["session_id"].GetString());
        AssertSeq(Assert.Single(collectionArgs).Keys, "text", "context");
        Assert.Equal("ctool: b/mine", Texts(fromCollection).Last());
        var events = CallEvents(await h.FlushAsync());
        var handlerEvent = Assert.Single(events, e => (string?)e["resourceName"] == "htool");
        Assert.Equal("", (string?)handlerEvent["sessionId"]);
        Assert.Equal("foreign", Tag(handlerEvent, "agentcat_session_id_source"));
        Assert.Equal("cust-1", (string?)RawArgs(handlerEvent)?["session_id"]);
        var collectionEvent = Assert.Single(events, e => (string?)e["resourceName"] == "ctool");
        Assert.Equal(Sid("ctool"), (string?)collectionEvent["sessionId"]);
        Assert.Equal("supplied", Tag(collectionEvent, "agentcat_session_id_source"));
        Assert.All(events, e => Assert.Equal(Agent, Tag(e, "agentcat_agent_id")));
        Assert.Single(h.Log.Lines, l => l == RebuiltLine);

        var rebuilt = Registry(h.Tracker!);
        await h.ListToolsAsync();
        Assert.Equal(1, Volatile.Read(ref lists));
        Assert.Equal(rebuilt, Registry(h.Tracker!));
        Assert.Contains("htool:agent_id,context", rebuilt);
        Assert.Contains("ctool:agent_id,session_id", rebuilt);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Hanging_list_handler_bounds_the_rebuild(string? protocolVersion)
    {
        var hang = Signal<ListToolsResult>();
        var cancelled = Signal<bool>();
        var lists = 0;
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = Echo(received, new AgentCatOptions { EnableAgentTracking = true });
        h.Configure(o => o.Handlers.ListToolsHandler = (_, cancellationToken) =>
        {
            Interlocked.Increment(ref lists);
            cancellationToken.Register(() => cancelled.TrySetResult(true));
            return new ValueTask<ListToolsResult>(hang.Task);
        });
        await h.StartAsync(protocolVersion);
        Assert.Equal(TimeSpan.FromSeconds(5), h.Tracker!.RebuildTimeout);
        Assert.Equal("tools/list did not respond within 5000ms", RegistryRebuilder.TimeoutMessage(h.Tracker.RebuildTimeout));
        h.Tracker.RebuildTimeout = TimeSpan.FromMilliseconds(300);
        var arguments = "{\"text\":\"hi\",\"session_id\":" + Quote(Sid("hang")) + ",\"agent_id\":" + Quote(Agent) + ",\"context\":\"why\"}";
        try
        {
            var first = await h.CallToolAsync("echo", arguments);
            await FakeAgentCatApi.WithinAsync(cancelled.Task, FakeAgentCatApi.WaitBound);
            var second = await h.CallToolAsync("echo", arguments);

            AssertSeq(Texts(first), "echo: hi");
            AssertSeq(Texts(second), "echo: hi");
            Assert.Equal(2, Volatile.Read(ref lists));
            Assert.Equal(
                2,
                h.Log.Lines.Count(l => l == RebuildFailedPrefix + "TimeoutException: tools/list did not respond within 300ms"));
            Assert.DoesNotContain(RebuiltLine, h.Log.Lines);
            Assert.Equal(2, received.Count);
            Assert.All(received, args => AssertSeq(args.Keys, "text"));
            var events = CallEvents(await h.FlushAsync());
            Assert.Equal(2, events.Length);
            Assert.All(events, e =>
            {
                Assert.Equal(Sid("hang"), (string?)e["sessionId"]);
                Assert.Equal("why", (string?)e["userIntent"]);
            });
        }
        finally
        {
            hang.TrySetResult(Mcp.ListResult("""{"tools":[]}"""));
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_listing_that_lands_during_a_rebuild_keeps_its_registries(string? protocolVersion)
    {
        var rebuildStarted = Signal<bool>();
        var rebuildPage = Signal<ListToolsResult>();
        var lists = 0;
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = Echo(received, new AgentCatOptions { EnableAgentTracking = true });
        h.Configure(o => o.Handlers.ListToolsHandler = (_, _) =>
        {
            if (Interlocked.Increment(ref lists) == 1)
            {
                rebuildStarted.TrySetResult(true);
                return new ValueTask<ListToolsResult>(rebuildPage.Task);
            }

            return new ValueTask<ListToolsResult>(Mcp.ListResult("{\"tools\":[" + EchoTool + "]}"));
        });
        await h.StartAsync(protocolVersion);

        var call = h.CallToolAsync("echo", """{"text":"hi","context":"why"}""");
        await FakeAgentCatApi.WithinAsync(rebuildStarted.Task, FakeAgentCatApi.WaitBound);
        await h.ListToolsAsync();
        var listed = Registry(h.Tracker!);
        rebuildPage.SetResult(Mcp.ListResult(
            """{"tools":[{"name":"echo","inputSchema":{"type":"object","properties":{"text":{"type":"string"},"context":{"type":"string"}}}}]}"""));
        var result = await call;

        Assert.Contains("echo:agent_id,context,session_id", listed);
        Assert.Equal(listed, Registry(h.Tracker!));
        Assert.DoesNotContain(RebuiltLine, h.Log.Lines);
        Assert.Equal(2, Volatile.Read(ref lists));
        AssertSeq(Assert.Single(received).Keys, "text");
        Assert.Equal("echo: hi", Texts(result).Last());
        Assert.Equal("why", (string?)CallEvent(await h.FlushAsync())["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task An_abandoned_page_is_observed_when_its_cancellation_callback_throws(string? protocolVersion)
    {
        var marker = "abandoned page " + Guid.NewGuid().ToString("N");
        var unobserved = new ConcurrentQueue<string>();
        EventHandler<UnobservedTaskExceptionEventArgs> record = (_, args) => unobserved.Enqueue(args.Exception.ToString());
        var cancelled = Signal<bool>();
        var page = new StrongBox<TaskCompletionSource<ListToolsResult>?>(new TaskCompletionSource<ListToolsResult>());
        await using var h = Echo(new List<Dictionary<string, JsonElement>>());
        h.Configure(o => o.Handlers.ListToolsHandler = (_, cancellationToken) =>
        {
            cancellationToken.Register(() =>
            {
                cancelled.TrySetResult(true);
                throw new InvalidOperationException("cancellation callback boom");
            });
            return new ValueTask<ListToolsResult>(page.Value!.Task);
        });
        await h.StartAsync(protocolVersion);
        h.Tracker!.RebuildTimeout = TimeSpan.FromMilliseconds(100);
        TaskScheduler.UnobservedTaskException += record;
        try
        {
            var result = await h.CallToolAsync("echo", """{"text":"hi"}""");
            await FakeAgentCatApi.WithinAsync(cancelled.Task, FakeAgentCatApi.WaitBound);
            FaultAndRelease(page, marker);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.Equal("echo: hi", Texts(result).Last());
            Assert.Single(h.Log.Lines, l => l == RebuildFailedPrefix + "TimeoutException: tools/list did not respond within 100ms");
            Assert.DoesNotContain(unobserved, text => text.Contains(marker));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= record;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FaultAndRelease(StrongBox<TaskCompletionSource<ListToolsResult>?> page, string marker)
    {
        page.Value!.SetException(new InvalidOperationException(marker));
        page.Value = null;
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Concurrent_calls_keep_their_own_sessions(string? protocolVersion)
    {
        const int Calls = 50;
        var arrived = 0;
        var together = Signal<bool>();
        var leaks = new ConcurrentQueue<string>();
        await using var h = new EngineHarness(options: new AgentCatOptions { EnableAgentTracking = true });
        h.AddHandlerTool(
            """{"name":"work","inputSchema":{"type":"object","properties":{"n":{"type":"number"},"fail":{"type":"boolean"}},"required":["n"]},"outputSchema":{"type":"object","properties":{"n":{"type":"number"}},"required":["n"]}}""",
            async (context, _) =>
            {
                var arguments = context.Params!.Arguments!;
                var n = arguments["n"].GetInt32();
                if (Interlocked.Increment(ref arrived) == Calls)
                {
                    together.TrySetResult(true);
                }

                await FakeAgentCatApi.WithinAsync(together.Task, FakeAgentCatApi.WaitBound);
                if (arguments.TryGetValue("fail", out var fail) && fail.ValueKind == JsonValueKind.True)
                {
                    throw new InvalidOperationException("call " + n + " failed");
                }

                return Mcp.Result("{\"content\":[{\"type\":\"text\",\"text\":\"n=" + n + "\"}],\"structuredContent\":{\"n\":" + n + "}}");
            });
        h.Configure(o =>
        {
            o.Filters.Request.CallToolFilters.Add(Around((context, next, cancellationToken) =>
            {
                foreach (var key in AgentCatKeys(context.JsonRpcRequest.Context?.Items))
                {
                    leaks.Enqueue("request " + key);
                }

                return next(context, cancellationToken);
            }));
            o.Filters.Message.OutgoingFilters.Add(next => (context, cancellationToken) =>
            {
                if (context.JsonRpcMessage is JsonRpcResponse response)
                {
                    foreach (var key in AgentCatKeys(response.Context?.Items))
                    {
                        leaks.Enqueue("response " + key);
                    }
                }

                return next(context, cancellationToken);
            });
        });
        await h.StartAsync(protocolVersion);

        static string ArgumentsFor(int i)
            => "{\"n\":" + i + ",\"agent_id\":\"agt_" + i + "\",\"session_id\":" + Quote(i % 3 == 0 ? Sid("call" + i) : "start")
                + (i % 3 == 2 ? ",\"fail\":true" : "")
                + "}";

        var results = await Task.WhenAll(Enumerable.Range(0, Calls).Select(i => h.CallToolAsync("work", ArgumentsFor(i))));

        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(Calls, events.Length);
        var minted = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Calls; i++)
        {
            var result = results[i];
            var e = Assert.Single(events, x => (int?)RawArgs(x)?["n"] == i);
            string session;
            if (i % 3 == 0)
            {
                session = Sid("call" + i);
                Assert.Null(MintBackOf(result));
                Assert.Equal("supplied", Tag(e, "agentcat_session_id_source"));
            }
            else
            {
                Assert.Equal(1, SessionBlocks(result));
                session = HandleFrom(MintBackOf(result)!, "session_id");
                Assert.True(minted.Add(session), "Minted session " + session + " was handed to more than one call.");
                Assert.Equal("minted", Tag(e, "agentcat_session_id_source"));
            }

            if (i % 3 == 2)
            {
                Assert.True(result.IsError);
                Assert.Equal("An error occurred invoking 'work'.", Texts(result).Last());
                Assert.Equal(true, (bool?)e["isError"]);
                Assert.Equal("call " + i + " failed", (string?)e["error"]?["message"]);
            }
            else
            {
                var structured = result.StructuredContent!.Value;
                Assert.Equal(i, structured.GetProperty("n").GetInt32());
                Assert.Equal(session, structured.GetProperty("mcp_session").GetProperty("session_id").GetString());
                Assert.Equal("agt_" + i, structured.GetProperty("mcp_session").GetProperty("agent_id").GetString());
                Assert.Equal("n=" + i, Texts(result).Last());
            }

            Assert.Equal(session, (string?)e["sessionId"]);
            Assert.Equal("agt_" + i, Tag(e, "agentcat_agent_id"));
        }

        Assert.Empty(leaks);
        AssertSeq(Registry(h.Tracker!), "get_more_tools:agent_id,session_id", "work:agent_id,context,session_id");
        Assert.DoesNotContain(h.Log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Mrtr_rounds_are_tagged_and_items_are_cleared(string? protocolVersion)
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "InputRequiredException (MRTR) exists only on MCP 2.x.");
        var rounds = new List<Round>();
        var outerSeen = new List<string[]>();
        var finalKeys = new ConcurrentQueue<string[]>();
        await using var h = Mrtr(rounds, finalResult: """{"content":[{"type":"text","text":"denied"}],"isError":true}""");
        h.Configure(o =>
        {
            h.Install(o);
            o.Filters.Request.CallToolFilters.Insert(0, Around(async (context, next, cancellationToken) =>
            {
                lock (outerSeen)
                {
                    outerSeen.Add(AgentCatKeys(context.JsonRpcRequest.Context?.Items));
                }

                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception)
                {
                    context.Items[CallToolSeam.SessionTextItem] = "[session_id stale]";
                    context.Items[CallToolSeam.ExceptionItem] = new InvalidOperationException("stale");
                    throw;
                }
            }));
            o.Filters.Message.OutgoingFilters.Add(next => (context, cancellationToken) =>
            {
                if (context.JsonRpcMessage is JsonRpcResponse { Result: JsonObject result } response && result.ContainsKey("content"))
                {
                    finalKeys.Enqueue(AgentCatKeys(response.Context?.Items));
                }

                return next(context, cancellationToken);
            });
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("confirm", """{"text":"go"}""");

        Assert.Equal(2, rounds.Count);
        Assert.Equal(2, outerSeen.Count);
        Assert.Empty(outerSeen[0]);
        if (protocolVersion == McpHarness.LegacyProtocol)
        {
            Assert.Same(rounds[0].Items, rounds[1].Items);
            AssertSeq(outerSeen[1], CallToolSeam.ExceptionItem, CallToolSeam.SessionTextItem);
        }
        else
        {
            Assert.NotSame(rounds[0].Items, rounds[1].Items);
            Assert.Empty(outerSeen[1]);
        }

        Assert.All(rounds, r => Assert.Empty(r.KeysAtEntry));
        Assert.True(result.IsError);
        Assert.Equal(1, SessionBlocks(result));
        Assert.DoesNotContain(Texts(result), t => t.Contains("stale"));
        Assert.Empty(Assert.Single(finalKeys));
        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(2, events.Length);
        var intermediate = Assert.Single(events, e => Tag(e, MrtrTag) == "input_required");
        Assert.Null(intermediate["error"]);
        var final = Assert.Single(events, e => Tag(e, MrtrTag) == "continuation");
        Assert.Equal(true, (bool?)final["isError"]);
        Assert.Equal("denied", (string?)final["error"]?["message"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Mrtr_three_rounds_are_tagged_continuation(string? protocolVersion)
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "InputRequiredException (MRTR) exists only on MCP 2.x.");
        var rounds = new List<Round>();
        await using var h = new EngineHarness(options: new AgentCatOptions { EnableAgentTracking = true });
        h.AddHandlerTool(ConfirmTool, (context, _) => RawString(context, "requestState") switch
        {
            null => throw InputRequired(Ask("round-1")),
            "round-1" => throw InputRequired(Ask("round-2")),
            _ => Text("confirmed twice"),
        });
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around((context, next, cancellationToken) =>
        {
            lock (rounds)
            {
                rounds.Add(new Round(context.Items, AgentCatKeys(context.Items), HasRaw(context, "inputResponses"), Keys(context.Params?.Arguments)));
            }

            return next(context, cancellationToken);
        })));
        Elicit(h);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync(
            "confirm",
            "{\"text\":\"go\",\"session_id\":" + Quote(Sid("three")) + ",\"agent_id\":" + Quote(Agent) + ",\"context\":\"two approvals\"}");

        AssertSeq(rounds.Select(r => r.Continuation ? "continuation" : "first"), "first", "continuation", "continuation");
        Assert.All(rounds, r => AssertSeq(r.Arguments, "text"));
        Assert.Equal("confirmed twice", Texts(result).Last());
        Assert.Null(MintBackOf(result));
        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(3, events.Length);
        JsonObject ForState(string? state) => Assert.Single(events, e => (string?)RawParams(e)?["requestState"] == state);
        var first = ForState(null);
        Assert.Equal("input_required", Tag(first, MrtrTag));
        Assert.Null(RawParams(first)?["inputResponses"]);
        var second = ForState("round-1");
        Assert.Equal("input_required", Tag(second, MrtrTag));
        Assert.Equal("accept", (string?)RawParams(second)?["inputResponses"]?["answer"]?["action"]);
        var third = ForState("round-2");
        Assert.Equal("continuation", Tag(third, MrtrTag));
        Assert.Equal(MrtrTag, TagKeys(third)[0]);
        Assert.Equal("accept", (string?)RawParams(third)?["inputResponses"]?["answer"]?["action"]);
        Assert.Null(third["isError"]);
        Assert.All(events, e =>
        {
            Assert.Equal(Sid("three"), (string?)e["sessionId"]);
            Assert.Equal(Agent, Tag(e, "agentcat_agent_id"));
            Assert.Equal("two approvals", (string?)e["userIntent"]);
            Assert.Equal(Sid("three"), (string?)RawArgs(e)?["session_id"]);
        });
    }

    // Registry misses and client cancellation during a rebuild

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Registry_miss_rebuilds_from_the_request_options(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        var lists = new StrongBox<int>();
        await using var first = Echo(new List<Dictionary<string, JsonElement>>());
        await first.StartAsync(protocolVersion);
        await using var second = NotesSharedWith(first, received, lists);
        await second.StartAsync(protocolVersion);
        await first.ListToolsAsync();
        Assert.DoesNotContain(Registry(first.Tracker!), r => r.StartsWith("notes:", StringComparison.Ordinal));

        var result = await second.CallToolAsync("notes", "{\"text\":\"a\",\"context\":\"mine\",\"session_id\":" + Quote(Sid("notes")) + "}");
        var again = await second.CallToolAsync("notes", "{\"text\":\"b\",\"context\":\"mine too\",\"session_id\":" + Quote(Sid("notes")) + "}");

        Assert.Equal(2, received.Count);
        AssertSeq(received[0].Keys, "text", "context");
        Assert.Equal("mine", received[0]["context"].GetString());
        AssertSeq(received[1].Keys, "text", "context");
        Assert.Equal("notes: a", Texts(result).Last());
        Assert.Equal("notes: b", Texts(again).Last());
        Assert.Equal(1, Volatile.Read(ref lists.Value));
        Assert.Single(first.Log.Lines, l => l == RebuiltLine);
        var registry = Registry(first.Tracker!);
        Assert.Contains("notes:session_id", registry);
        Assert.Contains("echo:context,session_id", registry);
        var events = CallEvents(await first.FlushAsync());
        Assert.Equal(2, events.Length);
        Assert.All(events, e => Assert.Equal(Sid("notes"), (string?)e["sessionId"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Registry_miss_without_injectable_arguments_does_not_rebuild(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        var lists = new StrongBox<int>();
        await using var first = Echo(new List<Dictionary<string, JsonElement>>());
        await first.StartAsync(protocolVersion);
        await using var second = NotesSharedWith(first, received, lists);
        await second.StartAsync(protocolVersion);
        await first.ListToolsAsync();
        var listed = Registry(first.Tracker!);

        var result = await second.CallToolAsync("notes", """{"text":"a"}""");

        AssertSeq(Assert.Single(received).Keys, "text");
        Assert.Equal("notes: a", Texts(result).Last());
        Assert.Equal(0, Volatile.Read(ref lists.Value));
        Assert.DoesNotContain(RebuiltLine, first.Log.Lines);
        Assert.Equal(listed, Registry(first.Tracker!));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Client_cancellation_during_a_rebuild_logs_no_warning(string? protocolVersion)
    {
        var hang = Signal<ListToolsResult>();
        var requestId = Signal<string>();
        var finished = Signal<bool>();
        await using var h = new EngineHarness();
        h.AddHandlerTool(EchoTool, (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Text("not cancelled");
        });
        h.Configure(o => o.Handlers.ListToolsHandler = (context, _) =>
        {
            requestId.TrySetResult(JsonSerializer.Serialize(context.JsonRpcRequest.Id, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(RequestId))));
            return new ValueTask<ListToolsResult>(hang.Task);
        });
        InstallWithOuterFilter(h, async (context, next, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            finally
            {
                finished.TrySetResult(true);
            }
        });
        await h.StartAsync(protocolVersion);
        try
        {
            using var abandon = new CancellationTokenSource();
            var call = h.Client.CallToolAsync(Mcp.CallToolParams("""{"name":"echo","arguments":{"text":"hi","context":"why"}}"""), abandon.Token).AsTask();
            await FakeAgentCatApi.WithinAsync(requestId.Task, FakeAgentCatApi.WaitBound);
            var cancelled = (JsonRpcMessage)JsonSerializer.Deserialize(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":" + await requestId.Task + ",\"reason\":\"test\"}}",
                McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage)))!;
            await h.Client.SendMessageAsync(cancelled, TestContext.Current.CancellationToken);
            await FakeAgentCatApi.WithinAsync(finished.Task, FakeAgentCatApi.WaitBound);
            abandon.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

            Assert.DoesNotContain(h.Log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal));
            Assert.DoesNotContain(h.Log.Lines, l => l.StartsWith(RebuildFailedPrefix, StringComparison.Ordinal));
        }
        finally
        {
            hang.TrySetResult(Mcp.ListResult("""{"tools":[]}"""));
        }
    }

    // Carried coverage: raw params, task markers and structured validation

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Colliding_raw_param_keys_keep_the_call_traced(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool((string text) => "echo: " + text, "echo");
        InstallWithOuterFilter(h, (context, next, cancellationToken) =>
        {
            context.JsonRpcRequest.Params = JsonNode.Parse("""{"name":"echo","arguments":{"text":"hi","context":"why"},"x":1,"x":2}""");
            return next(context, cancellationToken);
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", """{"text":"hi","context":"why"}""");

        Assert.NotNull(MintBackOf(result));
        Assert.Equal("echo: hi", Texts(result).Last());
        Assert.DoesNotContain(h.Log.Lines, l => l.StartsWith(TracingFailedPrefix, StringComparison.Ordinal));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("why", (string?)e["userIntent"]);
        Assert.False(HasTag(e, MrtrTag));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_stray_task_param_on_2x_is_ignored_and_the_call_is_restored_and_decorated(string? protocolVersion)
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "MCP 1.x runs task-augmented collection tools as core tasks.");
        string[]? seen = null;
        IDictionary<string, JsonElement>? before = null;
        IDictionary<string, JsonElement>? after = null;
        await using var h = new EngineHarness();
        h.AddTool(McpServerTool.Create(
            (RequestContext<CallToolRequestParams> request, string text) =>
            {
                seen = Keys(request.Params?.Arguments);
                return "echo: " + text;
            },
            new McpServerToolCreateOptions { Name = "echo" }));
        InstallWithOuterFilter(h, async (context, next, cancellationToken) =>
        {
            before = context.Params!.Arguments;
            var result = await next(context, cancellationToken);
            after = context.Params!.Arguments;
            return result;
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolRawAsync("""{"name":"echo","arguments":{"text":"hi","context":"why","session_id":"start"},"task":{}}""");

        Assert.NotNull(MintBackOf(result));
        Assert.Equal("echo: hi", Texts(result).Last());
        AssertSeq(seen!, "text");
        Assert.Same(before, after);
        AssertSeq(Keys(after), "text", "context", "session_id");
        Assert.Null(CallEvent(await h.FlushAsync())["response"]?["task"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Handler_tools_with_a_task_param_run_inside_the_filters_and_are_restored(string? protocolVersion)
    {
        var order = new List<string>();
        var received = new List<Dictionary<string, JsonElement>>();
        IDictionary<string, JsonElement>? before = null;
        IDictionary<string, JsonElement>? after = null;
        await using var h = new EngineHarness();
        h.AddHandlerTool(EchoTool, (context, _) =>
        {
            lock (order)
            {
                order.Add("tool");
                received.Add(new Dictionary<string, JsonElement>(context.Params!.Arguments!));
            }

            return Text("echo: " + context.Params!.Arguments!["text"].GetString());
        });
        InstallWithOuterFilter(h, async (context, next, cancellationToken) =>
        {
            before = context.Params!.Arguments;
            var result = await next(context, cancellationToken);
            lock (order)
            {
                order.Add("filters returned");
            }

            after = context.Params!.Arguments;
            return result;
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolRawAsync("""{"name":"echo","arguments":{"text":"hi","context":"why","session_id":"start"},"task":{"ttl":60000}}""");

        AssertSeq(order, "tool", "filters returned");
        AssertSeq(Assert.Single(received).Keys, "text");
        Assert.Same(before, after);
        AssertSeq(Keys(after), "text", "context", "session_id");
        Assert.NotNull(MintBackOf(result));
        Assert.Equal("echo: hi", Texts(result).Last());
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("""{"content":[{"type":"text","text":"echo: hi"}]}""", Json(e["response"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Decorated_structured_content_validates_against_the_injected_output_schema(string? protocolVersion)
    {
        await using var h = LowLevelStructured();
        h.AddTool(McpServerTool.Create(
            () => new Stats(3),
            new McpServerToolCreateOptions { Name = "get_stats", Description = "Get todo stats", UseStructuredContent = true }));
        await h.StartAsync(protocolVersion);
        var listing = await h.ListToolsAsync();

        foreach (var name in new[] { "stats", "get_stats" })
        {
            var listed = JsonNode.Parse(Assert.Single(listing.Tools, t => t.Name == name).OutputSchema!.Value.GetRawText())!;
            Assert.NotNull(listed["properties"]?["mcp_session"]);

            var result = await h.CallToolAsync(name, "{\"agent_id\":" + Quote(Agent) + "}");

            var structured = JsonNode.Parse(result.StructuredContent!.Value.GetRawText());
            Assert.NotNull(structured?["mcp_session"]);
            Assert.Empty(SchemaViolations(listed, structured));
        }

        var strict = await h.CallToolAsync("stats");
        Assert.Contains("$.mcp_session: not allowed", SchemaViolations(JsonNode.Parse(StrictStatsSchema)!, JsonNode.Parse(strict.StructuredContent!.Value.GetRawText())));
    }

    // Shared bodies

    private static EngineHarness Interleaving()
    {
        var finished = new Dictionary<string, TaskCompletionSource<bool>>(StringComparer.Ordinal)
        {
            ["A"] = Signal<bool>(),
            ["B"] = Signal<bool>(),
            ["C"] = Signal<bool>(),
        };
        var waitsFor = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "C", ["C"] = "B" };
        var h = new EngineHarness(options: new AgentCatOptions
        {
            EnableAgentTracking = true,
            Identify = (call, _) => new ValueTask<UserIdentity?>(new UserIdentity("actor-" + call.Arguments["text"].GetString())),
        });
        h.AddTool(McpServerTool.Create(
            async (string text) =>
            {
                if (waitsFor.TryGetValue(text, out var other))
                {
                    await FakeAgentCatApi.WithinAsync(finished[other].Task, FakeAgentCatApi.WaitBound);
                }

                finished[text].TrySetResult(true);
                return text;
            },
            new McpServerToolCreateOptions { Name = "slow_echo", Description = "echoes slowly" }));
        return h;
    }

    private static async Task AssertInterleavedAttribution(EngineHarness h, bool sourceTag)
    {
        await h.ListToolsAsync();
        var calls = new[]
        {
            (Text: "A", Session: Sid("session_A"), Agent: "agt_A"),
            (Text: "B", Session: Sid("session_B"), Agent: "agt_B"),
            (Text: "C", Session: Sid("session_C"), Agent: "agt_C"),
        };

        await Task.WhenAll(calls.Select(c => h.CallToolAsync(
            "slow_echo",
            "{\"text\":" + Quote(c.Text) + ",\"session_id\":" + Quote(c.Session) + ",\"agent_id\":" + Quote(c.Agent)
                + ",\"context\":\"concurrency regression test\"}")));

        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(3, events.Length);
        foreach (var c in calls)
        {
            var e = Assert.Single(events, x => (string?)RawArgs(x)?["text"] == c.Text);
            Assert.Equal(c.Session, (string?)e["sessionId"]);
            Assert.Equal(c.Agent, Tag(e, "agentcat_agent_id"));
            Assert.Equal("actor-" + c.Text, (string?)e["identifyActorGivenId"]);
            if (sourceTag)
            {
                Assert.Equal("supplied", Tag(e, "agentcat_session_id_source"));
            }
        }
    }

    private static EngineHarness Mrtr(List<Round> rounds, string finalResult = """{"content":[{"type":"text","text":"confirmed"}]}""")
    {
        var h = new EngineHarness(options: new AgentCatOptions { EnableAgentTracking = true });
        h.AddHandlerTool(ConfirmTool, (context, _) => HasRaw(context, "inputResponses")
            ? new ValueTask<CallToolResult>(Mcp.Result(finalResult))
            : throw InputRequired(AnswerRequest));
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around((context, next, cancellationToken) =>
        {
            lock (rounds)
            {
                rounds.Add(new Round(context.Items, AgentCatKeys(context.Items), HasRaw(context, "inputResponses"), Keys(context.Params?.Arguments)));
            }

            return next(context, cancellationToken);
        })));
        Elicit(h);
        return h;
    }

    private static void Elicit(McpHarness h)
    {
        var answer = (ElicitResult)JsonSerializer.Deserialize(
            """{"action":"accept","content":{"ok":true}}""",
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ElicitResult)))!;
        h.ConfigureClient(o => o.Handlers.ElicitationHandler = (_, _) => new ValueTask<ElicitResult>(answer));
    }

    private static bool HasRaw(RequestContext<CallToolRequestParams> context, string name)
        => context.JsonRpcRequest.Params is JsonObject raw && raw.ContainsKey(name);

    private static string? RawString(RequestContext<CallToolRequestParams> context, string name)
        => context.JsonRpcRequest.Params is JsonObject raw && raw[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Ask(string requestState)
        => "{\"inputRequests\":{\"answer\":{\"method\":\"elicitation/create\",\"params\":{\"message\":\"Proceed?\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}}}}}},\"requestState\":"
            + Quote(requestState) + "}";

    private static Exception InputRequired(string resultJson)
    {
        var core = typeof(McpServer).Assembly;
        var resultType = core.GetType("ModelContextProtocol.Protocol.InputRequiredResult", throwOnError: true)!;
        var exceptionType = core.GetType("ModelContextProtocol.Protocol.InputRequiredException", throwOnError: true)!;
        var result = JsonSerializer.Deserialize(resultJson, McpJsonUtilities.DefaultOptions.GetTypeInfo(resultType));
        return (Exception)Activator.CreateInstance(exceptionType, result)!;
    }

    private static List<string> SchemaViolations(JsonNode schema, JsonNode? value)
    {
        var violations = new List<string>();
        var pending = new Stack<(JsonNode? Schema, JsonNode? Value, string Path)>();
        pending.Push((schema, value, "$"));
        while (pending.Count > 0)
        {
            var (current, node, path) = pending.Pop();
            if (current is not JsonObject rules)
            {
                continue;
            }

            if (rules["type"] is { } type && !TypeMatches(type, node))
            {
                violations.Add(path + ": type");
                continue;
            }

            if (rules["enum"] is JsonArray options && !options.Any(o => Json(o) == Json(node)))
            {
                violations.Add(path + ": enum");
            }

            if (node is not JsonObject obj)
            {
                continue;
            }

            var properties = rules["properties"] as JsonObject;
            if (rules["required"] is JsonArray required)
            {
                foreach (var name in required.Select(r => (string?)r))
                {
                    if (name is not null && !obj.ContainsKey(name))
                    {
                        violations.Add(path + "." + name + ": required");
                    }
                }
            }

            foreach (var property in obj)
            {
                if (properties is not null && properties.TryGetPropertyValue(property.Key, out var child))
                {
                    pending.Push((child, property.Value, path + "." + property.Key));
                }
                else if (rules["additionalProperties"] is JsonValue additional && additional.GetValueKind() == JsonValueKind.False)
                {
                    violations.Add(path + "." + property.Key + ": not allowed");
                }
            }
        }

        return violations;
    }

    private static bool TypeMatches(JsonNode type, JsonNode? node)
    {
        var names = type is JsonArray many ? many.Select(t => (string?)t).ToArray() : new[] { (string?)type };
        var kind = node?.GetValueKind() ?? JsonValueKind.Null;
        return names.Any(name => name switch
        {
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "string" => kind == JsonValueKind.String,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "number" => kind == JsonValueKind.Number,
            "integer" => kind == JsonValueKind.Number && decimal.TryParse(node!.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d),
            "null" => kind == JsonValueKind.Null,
            _ => false,
        });
    }

    private sealed record Round(object Items, string[] KeysAtEntry, bool Continuation, string[] Arguments);

    private sealed record Stats(int Count);

    private sealed class InputRequiredException : Exception
    {
        public InputRequiredException()
            : base("not the MCP type")
        {
        }
    }
}
