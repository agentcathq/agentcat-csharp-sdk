using System.Reflection;
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

public class CallToolSeamTests
{
    private const string Agent = "opus-4.80-1m|claude-code|k3n9x";

    private const string ToolCall = "mcp:tools/call";

    private const string ReplyText =
        "Unfortunately, we have shown you the full tool list. We have noted your feedback and will work to improve the tool list in the future.";

    private const string TracingFailedPrefix = "Warning: AgentCat tracing failed for tool ";

    private const string PostHandlerPrefix = "Warning: AgentCat post-handler processing failed for tool ";

    private const string RebuiltLine = "Rebuilt injection registries on demand (tools/call before tools/list on this instance)";

    private const string EchoTool =
        """{"name":"echo","description":"echoes","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}""";

    private const string LooseTool =
        """{"name":"loose","description":"accepts extra arguments","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}""";

    private const string NotesTool =
        """{"name":"notes","description":"takes its own context","inputSchema":{"type":"object","properties":{"text":{"type":"string"},"context":{"type":"string","description":"customer's own"}}}}""";

    private static readonly JsonSerializerOptions Reflection = new();

    public static TheoryData<string?> Protocols => McpHarness.ProtocolVersions;

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

    private static void AssertSeq(IEnumerable<string?> actual, params string[] expected) => Assert.Equal(expected, actual.ToArray());

    private static string Quote(string text) => JsonSerializer.Serialize(text, Reflection);

    private static string? MintBackOf(CallToolResult result)
        => result.Content.Count > 0 && result.Content[0] is TextContentBlock first && first.Text.StartsWith("[session_id", StringComparison.Ordinal)
            ? first.Text
            : null;

    private static string HandleFrom(string text, string name) => new Regex(name + ": (\\S+)").Match(text).Groups[1].Value;

    private static int SessionBlocks(CallToolResult result)
        => result.Content.Count(c => c is TextContentBlock t && t.Text.StartsWith("[session_id", StringComparison.Ordinal));

    private static string AllText(CallToolResult result)
        => string.Join(" ", result.Content.Select(c => (c as TextContentBlock)?.Text ?? ""));

    private static string Serialize(CallToolResult result)
        => JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult)));

    private static JsonObject CallEvent(IReadOnlyList<JsonObject> events) => Assert.Single(events, e => (string?)e["eventType"] == ToolCall);

    private static JsonObject[] CallEvents(IReadOnlyList<JsonObject> events) => events.Where(e => (string?)e["eventType"] == ToolCall).ToArray();

    private static string? Tag(JsonObject e, string key) => (string?)e["tags"]?[key];

    private static bool HasTag(JsonObject e, string key) => e["tags"] is JsonObject tags && tags.ContainsKey(key);

    private static JsonNode? RawParams(JsonObject e) => e["parameters"]?["request"]?["params"];

    private static JsonNode? RawArgs(JsonObject e) => RawParams(e)?["arguments"];

    private static string Json(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    private static string[] Keys(IDictionary<string, JsonElement>? arguments) => arguments is null ? Array.Empty<string>() : arguments.Keys.ToArray();

    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    private static AgentCatOptions Options(Action<AgentCatOptions>? configure, bool agentTracking = false)
    {
        var options = new AgentCatOptions { EnableAgentTracking = agentTracking };
        configure?.Invoke(options);
        return options;
    }

    private static ValueTask<CallToolResult> Text(string text)
        => new(Mcp.Result("{\"content\":[{\"type\":\"text\",\"text\":" + Quote(text) + "}]}"));

    private static EngineHarness LowLevel(List<Dictionary<string, JsonElement>> received, Action<AgentCatOptions>? configure = null)
    {
        var h = new EngineHarness(options: Options(configure, agentTracking: true));
        h.AddHandlerTool(EchoTool, (context, _) =>
        {
            var arguments = context.Params?.Arguments;
            lock (received)
            {
                received.Add(arguments is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(arguments));
            }

            var text = arguments is not null && arguments.TryGetValue("text", out var value) ? value.GetString() : null;
            return text == "explode"
                ? new ValueTask<CallToolResult>(Mcp.Result("""{"isError":true,"content":[{"type":"text","text":"boom"}]}"""))
                : Text("echo: " + text);
        });
        return h;
    }

    private static void AddRecorded(EngineHarness h, string toolJson, List<Dictionary<string, JsonElement>> received)
    {
        var name = (string)JsonNode.Parse(toolJson)!["name"]!;
        h.AddHandlerTool(toolJson, (context, _) =>
        {
            var arguments = context.Params?.Arguments;
            lock (received)
            {
                received.Add(arguments is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(arguments));
            }

            var text = arguments is not null && arguments.TryGetValue("text", out var value) ? value.GetString() : null;
            return Text(name + ": " + text);
        });
    }

    private static EngineHarness OwnSessionId(List<Dictionary<string, JsonElement>> received)
    {
        var h = new EngineHarness(options: Options(null, agentTracking: true));
        h.AddHandlerTool(
            """{"name":"echo","description":"declares its own session_id","inputSchema":{"type":"object","properties":{"text":{"type":"string"},"session_id":{"type":"string","description":"customer's own"}}}}""",
            (context, _) =>
            {
                lock (received)
                {
                    received.Add(new Dictionary<string, JsonElement>(context.Params!.Arguments!));
                }

                return Text("ok");
            });
        return h;
    }

    private static EngineHarness Composed()
    {
        var h = new EngineHarness();
        h.AddHandlerTool(
            """{"name":"union_tool","description":"takes one of two shapes","inputSchema":{"type":"object","oneOf":[{"properties":{"kind":{"const":"a"},"a":{"type":"string"}},"required":["kind","a"]},{"properties":{"kind":{"const":"b"},"b":{"type":"number"}},"required":["kind","b"]}]}}""",
            (_, _) => Text("ok"));
        return h;
    }

    private static EngineHarness LowLevelStructured(Action<AgentCatOptions>? configure = null)
    {
        var h = new EngineHarness(options: Options(configure, agentTracking: true));
        h.AddHandlerTool(
            """{"name":"stats","description":"returns stats","inputSchema":{"type":"object","properties":{}},"outputSchema":{"type":"object","properties":{"count":{"type":"number"}},"required":["count"],"additionalProperties":false}}""",
            (_, _) => new ValueTask<CallToolResult>(Mcp.Result("""{"content":[{"type":"text","text":"42 things"}],"structuredContent":{"count":42}}""")));
        h.AddHandlerTool(
            """{"name":"poly","description":"composed output schema","inputSchema":{"type":"object","properties":{}},"outputSchema":{"type":"object","oneOf":[{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}]}}""",
            (_, _) => new ValueTask<CallToolResult>(Mcp.Result("""{"content":[{"type":"text","text":"poly"}],"structuredContent":{"a":"x"}}""")));
        return h;
    }

    private static EngineHarness Todos(Action<AgentCatOptions>? configure = null)
    {
        var h = new EngineHarness(options: Options(configure));
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
                    return todos.Count == 0 ? "No todos found" : string.Join("\n", todos.Select((t, i) => (i + 1) + ": " + t));
                }
            },
            "list_todos",
            "List all todo items");
        h.AddTool((string id) => "Completed todo: " + id, "complete_todo", "Mark a todo item as completed");
        h.AddTool(McpServerTool.Create(
            () =>
            {
                lock (todos)
                {
                    return new Stats(todos.Count);
                }
            },
            new McpServerToolCreateOptions { Name = "get_stats", Description = "Get todo stats", UseStructuredContent = true }));
        return h;
    }

    private static EngineHarness HighLevelStructured(Action<AgentCatOptions>? configure = null)
    {
        var h = new EngineHarness(options: Options(configure));
        h.AddTool(McpServerTool.Create(
            (string msg) => new Echoed(msg),
            new McpServerToolCreateOptions { Name = "echo", Description = "Echo", UseStructuredContent = true }));
        return h;
    }

    private static EngineHarness GetMoreToolsHighLevel(Action<AgentCatOptions>? configure = null)
    {
        var h = new EngineHarness(options: Options(configure));
        h.AddTool((string msg) => "echo: " + msg, "echo", "Echo");
        return h;
    }

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

    private static void FailAt(EngineHarness h, string stage, string message)
        => h.Tracker!.OnCallStage = s =>
        {
            if (s == stage)
            {
                throw new InvalidOperationException(message);
            }
        };

    // engine-callwrap.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Strips_registry_scoped_args_before_the_original_handler_keeps_raw_on_the_event(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received, o => o.EnableAgentTracking = false);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", "{\"text\":\"hi\",\"session_id\":" + Quote(Sid("supplied")) + ",\"context\":\"why\"}");

        AssertSeq(Keys(Assert.Single(received)), "text");
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("supplied"), (string?)e["sessionId"]);
        Assert.Equal(Sid("supplied"), (string?)RawArgs(e)?["session_id"]);
        Assert.NotEmpty(result.Content);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Answers_get_more_tools_directly_and_publishes_its_event(string? protocolVersion)
    {
        var customerRan = 0;
        await using var h = new EngineHarness();
        h.AddHandlerTool("""{"name":"get_more_tools","inputSchema":{"type":"object"}}""", (_, _) =>
        {
            Interlocked.Increment(ref customerRan);
            return Text("should not reach original handler");
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("get_more_tools", """{"context":"need csv export"}""");

        Assert.Contains("full tool list", ((TextContentBlock)result.Content[1]).Text, StringComparison.Ordinal);
        Assert.Equal(0, customerRan);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("get_more_tools", (string?)e["resourceName"]);
        Assert.Equal("need csv export", (string?)e["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Degrades_to_the_original_handler_with_the_stripped_request_when_tracing_setup_fails(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received, o => o.EnableAgentTracking = false);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();
        FailAt(h, "prepare", "client info boom");

        var result = await h.CallToolAsync("echo", """{"text":"hi","session_id":"ses_x","context":"why"}""");

        AssertSeq(Keys(Assert.Single(received)), "text");
        Assert.Equal("echo: hi", ((TextContentBlock)Assert.Single(result.Content)).Text);
        Assert.Empty(await h.FlushAsync());
        Assert.Single(h.Log.Lines, l => l.StartsWith(TracingFailedPrefix, StringComparison.Ordinal));
        Assert.Contains(
            "Warning: AgentCat tracing failed for tool echo, falling back to original handler - InvalidOperationException: client info boom",
            h.Log.Lines);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Answers_get_more_tools_directly_on_the_degraded_path_without_publishing(string? protocolVersion)
    {
        var customerRan = 0;
        await using var h = new EngineHarness();
        h.AddHandlerTool("""{"name":"get_more_tools","inputSchema":{"type":"object"}}""", (_, _) =>
        {
            Interlocked.Increment(ref customerRan);
            return Text("should not reach original handler");
        });
        await h.StartAsync(protocolVersion);
        FailAt(h, "prepare", "client info boom");

        var result = await h.CallToolAsync("get_more_tools", """{"context":"need csv export"}""");

        Assert.Contains("full tool list", ((TextContentBlock)result.Content[0]).Text, StringComparison.Ordinal);
        Assert.Single(result.Content);
        Assert.Equal(0, customerRan);
        Assert.Empty(await h.FlushAsync());
        Assert.Contains("Missing tool reported (context length: 15)", h.Log.Lines);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Does_not_intercept_when_enableReportMissing_is_false_the_customers_own_tool_runs(string? protocolVersion)
    {
        var customerRan = 0;
        await using var h = new EngineHarness(options: new AgentCatOptions { EnableReportMissing = false });
        h.AddHandlerTool("""{"name":"get_more_tools","inputSchema":{"type":"object"}}""", (_, _) =>
        {
            Interlocked.Increment(ref customerRan);
            return Text("customer answer");
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("get_more_tools", """{"context":"need csv"}""");

        Assert.Equal(1, customerRan);
        Assert.Contains("customer answer", AllText(result), StringComparison.Ordinal);
        Assert.DoesNotContain("full tool list", AllText(result), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Does_not_intercept_on_the_tracing_off_path_when_enableReportMissing_is_false(string? protocolVersion)
    {
        var customerRan = 0;
        await using var h = new EngineHarness(options: new AgentCatOptions { EnableReportMissing = false, EnableTracing = false });
        h.AddHandlerTool("""{"name":"get_more_tools","inputSchema":{"type":"object"}}""", (_, _) =>
        {
            Interlocked.Increment(ref customerRan);
            return Text("customer answer");
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("get_more_tools", """{"context":"need csv"}""");

        Assert.Equal(1, customerRan);
        Assert.Equal("customer answer", ((TextContentBlock)result.Content[0]).Text);

        foreach (var declares in new[] { false, true })
        {
            var received = new List<Dictionary<string, JsonElement>>();
            await using var own = new EngineHarness(options: new AgentCatOptions { EnableReportMissing = false, EnableTracing = false });
            AddRecorded(
                own,
                declares
                    ? """{"name":"get_more_tools","inputSchema":{"type":"object","properties":{"context":{"type":"string"}}}}"""
                    : """{"name":"get_more_tools","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}""",
                received);
            AddRecorded(own, LooseTool, received);
            await own.StartAsync(protocolVersion);
            var listing = await own.ListToolsAsync();
            var listed = Assert.Single(listing.Tools, t => t.Name == "get_more_tools");
            Assert.Equal(declares, listed.InputSchema.GetProperty("properties").TryGetProperty("context", out _));
            Assert.True(Assert.Single(listing.Tools, t => t.Name == "loose").InputSchema.GetProperty("properties").TryGetProperty("context", out _));

            await own.CallToolAsync("get_more_tools", declares ? """{"context":"need csv"}""" : """{"text":"csv"}""");
            await own.CallToolAsync("loose", """{"text":"b","context":"why"}""");

            Assert.Equal(2, received.Count);
            if (declares)
            {
                AssertSeq(Keys(received[0]), "context");
                Assert.Equal("need csv", received[0]["context"].GetString());
            }
            else
            {
                AssertSeq(Keys(received[0]), "text");
            }

            AssertSeq(Keys(received[1]), "text");
            Assert.Empty(await own.FlushAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_customer_get_more_tools_that_does_not_declare_context_receives_the_context_the_client_sends(string? protocolVersion)
    {
        foreach (var tracing in new[] { true, false })
        {
            var received = new List<Dictionary<string, JsonElement>>();
            await using var h = new EngineHarness(options: new AgentCatOptions { EnableReportMissing = false, EnableTracing = tracing });
            AddRecorded(h, """{"name":"get_more_tools","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}""", received);
            await h.StartAsync(protocolVersion);
            var listing = await h.ListToolsAsync();
            Assert.False(Assert.Single(listing.Tools, t => t.Name == "get_more_tools").InputSchema.GetProperty("properties").TryGetProperty("context", out _));

            var result = await h.CallToolAsync("get_more_tools", """{"text":"csv","context":"need csv export"}""");

            var arguments = Assert.Single(received);
            AssertSeq(Keys(arguments), "text", "context");
            Assert.Equal("need csv export", arguments["context"].GetString());
            Assert.Equal("get_more_tools: csv", Assert.IsType<TextContentBlock>(result.Content[^1]).Text);
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Does_not_intercept_on_the_degraded_path_when_enableReportMissing_is_false(string? protocolVersion)
    {
        var customerRan = 0;
        await using var h = new EngineHarness(options: new AgentCatOptions { EnableReportMissing = false });
        h.AddHandlerTool("""{"name":"get_more_tools","inputSchema":{"type":"object"}}""", (_, _) =>
        {
            Interlocked.Increment(ref customerRan);
            return Text("customer answer");
        });
        await h.StartAsync(protocolVersion);
        FailAt(h, "prepare", "client info boom");

        var result = await h.CallToolAsync("get_more_tools", """{"context":"need csv"}""");

        Assert.Equal(1, customerRan);
        Assert.Equal("customer answer", ((TextContentBlock)result.Content[0]).Text);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Returns_the_customers_isError_result_when_a_content_entry_is_null(string? protocolVersion)
    {
        var customer = Mcp.Result("""{"isError":true,"content":[null,{"type":"text","text":"quota exceeded, retry later"}]}""");
        CallToolResult? returned = null;
        await using var h = new EngineHarness();
        h.AddHandlerTool("""{"name":"echo","inputSchema":{"type":"object"}}""", (_, _) => new ValueTask<CallToolResult>(customer));
        InstallWithOuterFilter(h, async (context, next, ct) => returned = await next(context, ct));
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        Assert.NotNull(returned);
        Assert.True(returned!.IsError);
        Assert.Equal(3, returned.Content.Count);
        Assert.StartsWith("[session_id issued", ((TextContentBlock)returned.Content[0]).Text, StringComparison.Ordinal);
        Assert.Null(returned.Content[1]);
        Assert.Equal("quota exceeded, retry later", ((TextContentBlock)returned.Content[2]).Text);
        Assert.DoesNotContain(h.Log.Lines, l => l.StartsWith(PostHandlerPrefix, StringComparison.Ordinal));
        Assert.Equal(true, (bool?)CallEvent(await h.FlushAsync())["isError"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Returns_the_raw_customer_result_when_post_handler_decoration_throws(string? protocolVersion)
    {
        var customer = Mcp.Result("""{"content":[{"type":"text","text":"ok"}]}""");
        CallToolResult? returned = null;
        await using var h = new EngineHarness();
        h.AddHandlerTool("""{"name":"echo","inputSchema":{"type":"object"}}""", (_, _) => new ValueTask<CallToolResult>(customer));
        InstallWithOuterFilter(h, async (context, next, ct) => returned = await next(context, ct));
        await h.StartAsync(protocolVersion);
        FailAt(h, "finish", "decoration boom");

        var result = await h.CallToolAsync("echo", """{"msg":"hi"}""");

        Assert.Same(customer, returned);
        Assert.Null(MintBackOf(result));
        Assert.Equal("ok", ((TextContentBlock)Assert.Single(result.Content)).Text);
        Assert.Contains(
            "Warning: AgentCat post-handler processing failed for tool echo; returning the customer's result untouched - InvalidOperationException: decoration boom",
            h.Log.Lines);
        Assert.Empty(await h.FlushAsync());
    }

    // handles-integration.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task First_call_sends_start_mints_session_id_prepends_the_mint_back_tags_the_source(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion);
        await AssertFirstCallMints(h);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Echoed_handles_are_reused_verbatim_with_no_mint_back(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", "{\"text\":\"hi\",\"context\":\"testing\",\"session_id\":" + Quote(Sid("fixed")) + ",\"agent_id\":" + Quote(Agent) + "}");

        Assert.Null(MintBackOf(result));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("fixed"), (string?)e["sessionId"]);
        Assert.Equal("supplied", Tag(e, "agentcat_session_id_source"));
        Assert.Equal(Agent, Tag(e, "agentcat_agent_id"));
        Assert.Equal("supplied", Tag(e, "agentcat_agent_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Subagent_flow_inherited_task_omitted_agent_yields_no_mint_back_task_tagged_supplied(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", "{\"text\":\"hi\",\"context\":\"testing\",\"session_id\":" + Quote(Sid("parent")) + "}");

        Assert.Null(MintBackOf(result));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("supplied", Tag(e, "agentcat_session_id_source"));
        Assert.False(HasTag(e, "agentcat_agent_id"));
        Assert.False(HasTag(e, "agentcat_agent_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Prepends_the_mint_back_on_isError_results_too(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", """{"text":"explode","context":"testing"}""");

        Assert.True(result.IsError);
        Assert.Contains("[session_id issued", MintBackOf(result), StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", MintBackOf(result), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Strips_injected_params_before_the_customer_handler_event_keeps_them(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion);
        await AssertStripsBeforeHandler(h, received, "agt_x");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_customers_own_session_id_is_never_adopted_and_still_reaches_the_handler(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = OwnSessionId(received);
        await h.StartAsync(protocolVersion);
        var echo = Assert.Single((await h.ListToolsAsync()).Tools, t => t.Name == "echo");
        Assert.False(echo.InputSchema.GetProperty("properties").GetProperty("session_id").TryGetProperty("pattern", out _));
        Assert.True(!echo.InputSchema.TryGetProperty("required", out var required) || required.EnumerateArray().All(r => r.GetString() != "session_id"));

        await h.CallToolAsync("echo", """{"text":"hi","session_id":"CUSTOMER-VALUE-123"}""");

        Assert.Equal("CUSTOMER-VALUE-123", Assert.Single(received)["session_id"].GetString());
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("", (string?)e["sessionId"]);
        Assert.Equal("foreign", Tag(e, "agentcat_session_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_customers_own_session_id_param_start_is_their_value_too_never_a_sentinel(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = OwnSessionId(received);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", """{"text":"hi","session_id":"start"}""");

        Assert.Equal("start", Assert.Single(received)["session_id"].GetString());
        Assert.Null(MintBackOf(result));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("", (string?)e["sessionId"]);
        Assert.Equal("foreign", Tag(e, "agentcat_session_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Composed_schema_tool_injection_is_skipped_yet_the_call_still_mints_a_session_and_announces_it(string? protocolVersion)
    {
        await using var h = Composed();
        await h.StartAsync(protocolVersion);
        var union = Assert.Single((await h.ListToolsAsync()).Tools, t => t.Name == "union_tool");
        Assert.False(union.InputSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("session_id", out _));
        Assert.Equal(2, union.InputSchema.GetProperty("oneOf").GetArrayLength());

        var result = await h.CallToolAsync("union_tool", """{"kind":"a","a":"x"}""");

        var block = MintBackOf(result);
        Assert.Contains("[session_id issued", block, StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(HandleFrom(block!, "session_id"), (string?)e["sessionId"]);
        Assert.Equal("minted", Tag(e, "agentcat_session_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Composed_schema_tool_an_echoed_session_id_AgentCat_issued_is_honored_not_called_foreign(string? protocolVersion)
    {
        await using var h = Composed();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("union_tool", "{\"kind\":\"b\",\"b\":2,\"session_id\":" + Quote(Sid("union")) + "}");

        Assert.Null(MintBackOf(result));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("union"), (string?)e["sessionId"]);
        Assert.Equal("supplied", Tag(e, "agentcat_session_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task EnableTracing_false_no_handle_params_injected_no_mint_back_no_events(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received, o => o.EnableTracing = false);
        await h.StartAsync(protocolVersion);
        var echo = Assert.Single((await h.ListToolsAsync()).Tools, t => t.Name == "echo");
        Assert.False(echo.InputSchema.GetProperty("properties").TryGetProperty("session_id", out _));
        Assert.False(echo.InputSchema.GetProperty("properties").TryGetProperty("agent_id", out _));

        var result = await h.CallToolAsync("echo", """{"text":"hi"}""");

        Assert.Null(MintBackOf(result));
        Assert.Empty(await h.FlushAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task EnableAgentTracking_false_no_agent_param_tag_or_mint_back_line(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received, o => o.EnableAgentTracking = false);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();
        var echo = Assert.Single((await h.ListToolsAsync()).Tools, t => t.Name == "echo");
        Assert.False(echo.InputSchema.GetProperty("properties").TryGetProperty("agent_id", out _));

        var result = await h.CallToolAsync("echo", """{"text":"hi","context":"testing"}""");

        var block = MintBackOf(result);
        Assert.Contains("session_id: ", block, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", block, StringComparison.Ordinal);
        Assert.False(HasTag(CallEvent(await h.FlushAsync()), "agentcat_agent_id"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Stamps_meta_clientInfo_and_protocol_version_when_present(string? protocolVersion)
    {
        var metas = new List<string?>();
        await using var h = MetaCapturing(metas);
        await h.StartAsync(protocolVersion);
        await AssertMetaReachesTheEvent(h, metas, "modern-client", "2.0");
    }

    public static TheoryData<string?, string, string> CollidingMetas => new()
    {
        { null, "\"trace\":1,\"Trace\":2", """{"trace":1,"Trace":2}""" },
        { McpHarness.LegacyProtocol, "\"trace\":1,\"Trace\":2", """{"trace":1,"Trace":2}""" },
        { null, "\"trace\":1,\"trace\":2", """{"trace":2}""" },
        { McpHarness.LegacyProtocol, "\"trace\":1,\"trace\":2", """{"trace":2}""" },
    };

    [Theory]
    [MemberData(nameof(CollidingMetas))]
    public async Task Colliding_meta_keys_are_read_as_absent_and_the_call_is_still_traced(string? protocolVersion, string trace, string eventTrace)
    {
        Assert.SkipWhen(McpHarness.McpMajor >= 2, "MCP 2.x rejects a request whose _meta keys collide before any filter runs.");
        var received = new List<Dictionary<string, JsonElement>>();
        var hookMetas = new List<JsonObject?>();
        await using var h = LowLevel(received, o => o.Identify = (call, _) =>
        {
            lock (hookMetas)
            {
                hookMetas.Add(call.Meta);
            }

            return new ValueTask<UserIdentity?>(new UserIdentity("meta-user"));
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();
        var meta = "{" + trace
            + ",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"meta-client\",\"version\":\"9.0\"},\"io.modelcontextprotocol/protocolVersion\":\"2025-11-25\"}";

        var result = await h.CallToolRawAsync("{\"name\":\"echo\",\"arguments\":{\"text\":\"hi\",\"context\":\"why\"},\"_meta\":" + meta + "}");

        AssertSeq(Keys(Assert.Single(received)), "text");
        Assert.Equal(1, SessionBlocks(result));
        Assert.Equal("echo: hi", Assert.IsType<TextContentBlock>(result.Content[1]).Text);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(HandleFrom(MintBackOf(result)!, "session_id"), (string?)e["sessionId"]);
        Assert.Equal("why", (string?)e["userIntent"]);
        Assert.Equal("harness-client", (string?)e["clientName"]);
        Assert.Equal("3.1.4", (string?)e["clientVersion"]);
        Assert.False(HasTag(e, "agentcat_protocol_version"));
        Assert.Equal("meta-user", (string?)e["identifyActorGivenId"]);
        Assert.Null(Assert.Single(hookMetas));
        var eventMeta = Assert.IsType<JsonObject>(RawParams(e)?["_meta"]);
        Assert.Equal(eventMeta.ToJsonString(), Json(e["parameters"]?["extra"]?["_meta"]));
        Assert.Equal(eventTrace, new JsonObject(eventMeta.Where(p => p.Key.Equals("trace", StringComparison.OrdinalIgnoreCase))
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone()))).ToJsonString());
        Assert.Equal("meta-client", (string?)eventMeta["io.modelcontextprotocol/clientInfo"]?["name"]);
        Assert.DoesNotContain(h.Log.Lines, line => line.StartsWith(TracingFailedPrefix, StringComparison.Ordinal)
            || line.StartsWith(PostHandlerPrefix, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Mints_on_first_call_prepends_exactly_one_mint_back_block(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_todo", """{"text":"buy milk","context":"testing handles"}""");

        Assert.Equal(1, SessionBlocks(result));
        var block = MintBackOf(result);
        Assert.NotNull(block);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(HandleFrom(block!, "session_id"), (string?)e["sessionId"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Callback_receives_stripped_args_supplied_handles_reused_silently(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_todo", "{\"text\":\"buy milk\",\"context\":\"testing\",\"session_id\":" + Quote(Sid("hl")) + ",\"agent_id\":\"agt_hl\"}");

        Assert.Contains("buy milk", ((TextContentBlock)result.Content[0]).Text, StringComparison.Ordinal);
        Assert.Null(MintBackOf(result));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("hl"), (string?)e["sessionId"]);
        Assert.Equal("supplied", Tag(e, "agentcat_session_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Agent_tracking_is_OFF_by_default_no_param_no_tag_task_only_mint_back(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);
        foreach (var tool in (await h.ListToolsAsync()).Tools)
        {
            Assert.False(tool.InputSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("agent_id", out _));
        }

        var result = await h.CallToolAsync("add_todo", """{"text":"buy milk","context":"testing defaults"}""");

        var block = MintBackOf(result);
        Assert.Contains("[session_id issued", block, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", block, StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.False(HasTag(e, "agentcat_agent_id"));
        Assert.False(HasTag(e, "agentcat_agent_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Get_more_tools_carries_handles_and_the_mint_back(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("get_more_tools", """{"context":"need a search tool"}""");

        Assert.Contains("[session_id issued", MintBackOf(result), StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", MintBackOf(result), StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.Matches("^ses_", (string?)e["sessionId"]);
        Assert.Equal("need a search tool", (string?)e["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task EnableTracing_false_get_more_tools_still_answers_no_mint_back_no_event(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableTracing = false);
        await h.StartAsync(protocolVersion);
        Assert.Contains((await h.ListToolsAsync()).Tools, t => t.Name == "get_more_tools");

        var result = await h.CallToolAsync("get_more_tools", """{"context":"need a search tool"}""");

        Assert.Contains("we have shown you the full tool list", ((TextContentBlock)result.Content[0]).Text, StringComparison.Ordinal);
        Assert.Null(MintBackOf(result));
        Assert.Empty(await h.FlushAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Low_level_minting_call_block_on_the_wire_absent_from_event_response(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion);
        await AssertMintingCallStaysOffTheEvent(h);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Low_level_minting_error_call_event_response_and_event_error_stay_clean(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion);
        await AssertMintingErrorCallStaysOffTheEvent(h);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Low_level_echoed_handles_event_response_is_exactly_the_handler_result(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion);
        await AssertEchoedResponseIsExact(h, "agt_fixed");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task High_level_minting_call_block_on_the_wire_absent_from_event_response(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_todo", """{"text":"buy milk","context":"testing handles"}""");

        Assert.Contains("[session_id issued", MintBackOf(result), StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", MintBackOf(result), StringComparison.Ordinal);
        var response = Json(CallEvent(await h.FlushAsync())["response"]);
        Assert.DoesNotContain("[session_id", response, StringComparison.Ordinal);
        Assert.Contains("buy milk", response, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task High_level_get_more_tools_minting_call_event_response_keeps_only_the_answer(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("get_more_tools", """{"context":"need a search tool"}""");

        Assert.Contains("[session_id issued", MintBackOf(result), StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", MintBackOf(result), StringComparison.Ordinal);
        var response = CallEvent(await h.FlushAsync())["response"];
        Assert.DoesNotContain("[session_id", Json(response), StringComparison.Ordinal);
        Assert.Equal("{\"content\":[{\"type\":\"text\",\"text\":" + Quote(ReplyText) + "}]}", Json(response));
    }

    // structured-mintback.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task StructuredContent_only_consumer_discovers_minted_handles(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("get_stats", """{"context":"checking stats"}""");

        var structured = result.StructuredContent!.Value;
        var mint = structured.GetProperty("mcp_session");
        Assert.Matches("^ses_", mint.GetProperty("session_id").GetString());
        Assert.False(mint.TryGetProperty("agent_id", out _));
        Assert.Equal("issued", mint.GetProperty("status").GetString());
        Assert.Equal(0, structured.GetProperty("count").GetInt32());
        Assert.Equal("mcp_session", Keys(structured)[0]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Steady_state_supplied_handles_are_re_echoed_on_every_response_text_block_stays_mint_only(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("get_stats", "{\"context\":\"checking stats\",\"session_id\":" + Quote(Sid("fixed")) + ",\"agent_id\":" + Quote(Agent) + "}");

        var mint = result.StructuredContent!.Value.GetProperty("mcp_session");
        Assert.Equal(Sid("fixed"), mint.GetProperty("session_id").GetString());
        Assert.Equal(Agent, mint.GetProperty("agent_id").GetString());
        Assert.Equal("active", mint.GetProperty("status").GetString());
        Assert.Equal(0, SessionBlocks(result));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Wire_only_published_event_response_never_contains_mcp_session(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("get_stats", """{"context":"checking stats"}""");

        var response = CallEvent(await h.FlushAsync())["response"];
        Assert.DoesNotContain("mcp_session", Json(response), StringComparison.Ordinal);
        Assert.DoesNotContain("[session_id", Json(response), StringComparison.Ordinal);
        Assert.Equal("""{"count":0}""", Json(response?["structuredContent"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Content_only_tools_are_unaffected_block_leads_content_no_structuredContent_conjured(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_todo", """{"text":"buy milk","context":"testing"}""");

        Assert.Null(result.StructuredContent);
        Assert.StartsWith("[session_id issued", ((TextContentBlock)result.Content[0]).Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Mints_into_structuredContent_on_the_V1_path_too(string? protocolVersion)
    {
        await using var h = LowLevelStructured();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("stats");

        var structured = result.StructuredContent!.Value;
        var mint = structured.GetProperty("mcp_session");
        Assert.Matches("^ses_", mint.GetProperty("session_id").GetString());
        Assert.False(mint.TryGetProperty("agent_id", out _));
        Assert.Equal("issued", mint.GetProperty("status").GetString());
        Assert.Equal(42, structured.GetProperty("count").GetInt32());
        Assert.Equal("mcp_session", Keys(structured)[0]);
        Assert.DoesNotContain("mcp_session", Json(CallEvent(await h.FlushAsync())["response"]), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Composed_outputSchema_schema_untouched_no_mirror_text_block_still_delivers(string? protocolVersion)
    {
        await using var h = LowLevelStructured();
        await h.StartAsync(protocolVersion);
        var poly = Assert.Single((await h.ListToolsAsync()).Tools, t => t.Name == "poly");
        Assert.DoesNotContain("mcp_session", poly.OutputSchema!.Value.GetRawText(), StringComparison.Ordinal);

        var result = await h.CallToolAsync("poly");

        Assert.False(result.StructuredContent!.Value.TryGetProperty("mcp_session", out _));
        Assert.Equal("x", result.StructuredContent.Value.GetProperty("a").GetString());
        Assert.StartsWith("[session_id issued", ((TextContentBlock)result.Content[0]).Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Hook_mode_mirrored_payload_carries_agent_id_but_never_session_id_or_status(string? protocolVersion)
    {
        await using var h = LowLevelStructured(o => o.ResolveSessionId = (_, _) => new ValueTask<string?>("corr-1"));
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("stats", "{\"agent_id\":" + Quote(Agent) + "}");

        var mint = result.StructuredContent!.Value.GetProperty("mcp_session");
        Assert.False(mint.TryGetProperty("session_id", out _));
        Assert.False(mint.TryGetProperty("status", out _));
        Assert.Equal(Agent, mint.GetProperty("agent_id").GetString());
        Assert.Null(MintBackOf(result));
    }

    // report-missing.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_successfully_call_get_more_tools_with_only_context(string? protocolVersion)
    {
        await AssertGetMoreToolsCall(protocolVersion, "Need a database query tool for SQL operations");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_successfully_call_get_more_tools_with_description_and_optional_context(string? protocolVersion)
    {
        await AssertGetMoreToolsCall(protocolVersion, "User wants to fetch data from external REST APIs");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_missing_description_parameter_gracefully(string? protocolVersion)
    {
        await using var h = Todos(o =>
        {
            o.EnableReportMissing = true;
            o.EnableTracing = true;
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("get_more_tools");

        Assert.Contains("Unfortunately", ((TextContentBlock)result.Content[1]).Text, StringComparison.Ordinal);
        Assert.Contains("Missing tool reported (context length: 0)", h.Log.Lines);
        var e = CallEvent(await h.FlushAsync());
        Assert.Null(e["userIntent"]);
        Assert.Equal("{}", Json(RawArgs(e)));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_track_report_missing_events_with_proper_structure(string? protocolVersion)
    {
        const string context = "User wants to monitor file changes in real-time";
        await using var h = Todos();
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("get_more_tools", "{\"context\":" + Quote(context) + "}");

        var e = Assert.Single(CallEvents(await h.FlushAsync()), x => (string?)x["resourceName"] == "get_more_tools");
        Assert.False(string.IsNullOrEmpty((string?)e["sessionId"]));
        Assert.Equal("get_more_tools", (string?)RawParams(e)?["name"]);
        Assert.Equal("{\"context\":" + Quote(context) + "}", Json(RawArgs(e)));
        Assert.Equal(context, (string?)e["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_maintain_task_continuity_when_calling_report_missing_between_other_tools(string? protocolVersion)
    {
        var sessionId = Sid("report_missing_continuity");
        await using var h = Todos();
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", "{\"text\":\"First todo\",\"context\":\"Adding first todo to test task continuity\",\"session_id\":" + Quote(sessionId) + "}");
        await h.FlushAsync();
        await h.CallToolAsync("get_more_tools", "{\"context\":\"Need a bulk todo import tool\",\"session_id\":" + Quote(sessionId) + "}");
        await h.FlushAsync();
        await h.CallToolAsync("list_todos", "{\"context\":\"Listing todos after reporting missing tool\",\"session_id\":" + Quote(sessionId) + "}");

        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(3, events.Length);
        Assert.All(events, e => Assert.Equal(sessionId, (string?)e["sessionId"]));
        AssertSeq(events.Select(e => (string?)e["resourceName"]).ToArray(), "add_todo", "get_more_tools", "list_todos");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_provide_actionable_insights_for_missing_tool_reports(string? protocolVersion)
    {
        var reports = new[] { "Want to query PostgreSQL database", "Need to construct complex queries", "Managing schema changes" };
        await using var h = Todos();
        await h.StartAsync(protocolVersion);

        foreach (var report in reports)
        {
            await h.CallToolAsync("get_more_tools", "{\"context\":" + Quote(report) + "}");
            await h.FlushAsync();
        }

        var events = CallEvents(await h.FlushAsync()).Where(e => (string?)e["resourceName"] == "get_more_tools").ToArray();
        Assert.Equal(3, events.Length);
        for (var i = 0; i < reports.Length; i++)
        {
            Assert.Equal(reports[i], (string?)RawArgs(events[i])?["context"]);
            Assert.NotNull(events[i]["sessionId"]);
            Assert.NotNull(events[i]["timestamp"]);
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_track_get_more_tools_across_different_sessions(string? protocolVersion)
    {
        var events = new List<JsonObject>();
        foreach (var context in new[] { "Need OAuth integration tool", "Need OAuth2 authentication tool" })
        {
            await using var h = Todos();
            await h.StartAsync(protocolVersion);
            await h.CallToolAsync("get_more_tools", "{\"context\":" + Quote(context) + "}");
            events.AddRange(CallEvents(await h.FlushAsync()));
        }

        Assert.Equal(2, events.Count);
        Assert.Equal(2, events.Select(e => (string?)e["sessionId"]).Distinct().Count());
        Assert.All(events, e => Assert.Contains("OAuth", (string?)RawArgs(e)?["context"], StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_multiple_missing_tool_reports_in_the_same_task(string? protocolVersion)
    {
        var sessionId = Sid("report_missing_workflow");
        await using var h = Todos();
        await h.StartAsync(protocolVersion);

        foreach (var context in new[] { "Importing data from spreadsheets", "Need to validate imported data", "Process large datasets" })
        {
            await h.CallToolAsync("get_more_tools", "{\"context\":" + Quote(context) + ",\"session_id\":" + Quote(sessionId) + "}");
        }

        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(3, events.Length);
        Assert.All(events, e => Assert.Equal(sessionId, (string?)e["sessionId"]));
    }

    // basic-server.test.ts and tracing-initialization.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_publish_events_only_once_no_duplicates(string? protocolVersion)
    {
        await using var h = Todos(o =>
        {
            o.EnableToolCallContext = true;
            o.EnableTracing = true;
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"context":"test context 1","text":"First todo with unique ID: 1"}""");
        await h.CallToolAsync("add_todo", """{"context":"test context 2","text":"Second todo with unique ID: 2"}""");
        await h.CallToolAsync("list_todos", """{"context":"test context 3"}""");

        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(3, events.Length);
        var identities = events.Select(e => (string?)e["eventType"] + "_" + (string?)e["resourceName"] + "_" + Json(e["parameters"])).ToArray();
        Assert.Equal(identities.Length, identities.Distinct().Count());
        Assert.Equal(2, events.Count(e => (string?)e["resourceName"] == "add_todo"));
        Assert.Single(events, e => (string?)e["resourceName"] == "list_todos");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_publish_events_for_successful_tool_calls_with_handler_level_architecture(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableTracing = true);
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("add_todo", """{"text":"Test successful handler wrapping","context":"Testing handler-level event publishing"}""");

        Assert.NotEqual(true, result.IsError);
        var events = await h.FlushAsync();
        var e = Assert.Single(events);
        Assert.Equal("add_todo", (string?)e["resourceName"]);
        Assert.Null(e["isError"]);
        Assert.Equal("Testing handler-level event publishing", (string?)e["userIntent"]);
        Assert.NotNull(e["eventType"]);
        Assert.NotNull(e["timestamp"]);
    }

    // v2/lowlevel-handles.test.ts (run through the DI startup)

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task First_call_sends_start_mints_session_id_prepends_the_mint_back_tags_the_source_v2(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion, withServices: true);
        await AssertFirstCallMints(h);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Strips_injected_params_before_the_customer_handler_event_keeps_them_v2(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion, withServices: true);
        await AssertStripsBeforeHandler(h, received, Agent);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Appends_the_mint_back_on_isError_results_event_response_and_event_error_stay_clean(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion, withServices: true);
        await AssertMintingErrorCallStaysOffTheEvent(h);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Minting_call_block_on_the_wire_absent_from_event_response(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion, withServices: true);
        await AssertMintingCallStaysOffTheEvent(h);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Echoed_handles_event_response_is_exactly_the_handler_result(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion, withServices: true);
        await AssertEchoedResponseIsExact(h, Agent);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task An_invalid_session_id_publishes_sessionless_and_tells_the_agent_to_re_send(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", """{"text":"hi","context":"testing intent","session_id":"not-a-real-id"}""");

        Assert.Contains("[session_id unrecognized", MintBackOf(result), StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("", (string?)e["sessionId"]);
        Assert.Equal("invalid", Tag(e, "agentcat_session_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Stamps_meta_clientInfo_and_protocol_version_when_present_v2(string? protocolVersion)
    {
        var metas = new List<string?>();
        await using var h = MetaCapturing(metas);
        await h.StartAsync(protocolVersion, withServices: true);
        await AssertMetaReachesTheEvent(h, metas, "modern-client", "2.0");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Prefers_the_v2_envelope_for_clientInfo_and_protocol_version(string? protocolVersion)
    {
        var metas = new List<string?>();
        await using var h = MetaCapturing(metas);
        await h.StartAsync(protocolVersion, withServices: true);
        await AssertMetaReachesTheEvent(h, metas, "envelope-client", "3.0");
    }

    // v2/agent-tracking.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tolerates_omission_task_only_mint_back_event_without_agent_identity(string? protocolVersion)
    {
        await using var h = HighLevelStructured(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"agent omission"}""");

        var block = MintBackOf(result);
        Assert.Contains("[session_id issued", block, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", block, StringComparison.Ordinal);
        var mirror = result.StructuredContent!.Value.GetProperty("mcp_session");
        Assert.Equal(HandleFrom(block!, "session_id"), mirror.GetProperty("session_id").GetString());
        Assert.False(mirror.TryGetProperty("agent_id", out _));
        var e = CallEvent(await h.FlushAsync());
        Assert.False(HasTag(e, "agentcat_agent_id"));
        Assert.False(HasTag(e, "agentcat_agent_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Echoes_a_self_chosen_agent_id_verbatim_with_source_tag_supplied(string? protocolVersion)
    {
        await using var h = HighLevelStructured(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", "{\"msg\":\"hi\",\"context\":\"agent echo\",\"session_id\":" + Quote(Sid("fixed")) + ",\"agent_id\":" + Quote(Agent) + "}");

        Assert.Null(MintBackOf(result));
        var mirror = result.StructuredContent!.Value.GetProperty("mcp_session");
        Assert.Equal(Sid("fixed"), mirror.GetProperty("session_id").GetString());
        Assert.Equal(Agent, mirror.GetProperty("agent_id").GetString());
        Assert.Equal("active", mirror.GetProperty("status").GetString());
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("fixed"), (string?)e["sessionId"]);
        Assert.Equal(Agent, Tag(e, "agentcat_agent_id"));
        Assert.Equal("supplied", Tag(e, "agentcat_agent_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Subagent_flow_inherited_task_own_generated_agent_id_no_mint_back(string? protocolVersion)
    {
        const string subagent = "haiku-4.5|claude-code|q7w2e";
        await using var h = HighLevelStructured(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", "{\"msg\":\"hi\",\"context\":\"subagent flow\",\"session_id\":" + Quote(Sid("parent")) + ",\"agent_id\":" + Quote(subagent) + "}");

        Assert.Null(MintBackOf(result));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("parent"), (string?)e["sessionId"]);
        Assert.Equal(subagent, Tag(e, "agentcat_agent_id"));
        Assert.Equal("supplied", Tag(e, "agentcat_agent_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Injects_no_agent_id_param_tags_nothing_and_mints_task_only(string? protocolVersion)
    {
        await using var h = HighLevelStructured();
        await h.StartAsync(protocolVersion);
        foreach (var tool in (await h.ListToolsAsync()).Tools)
        {
            Assert.False(tool.InputSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("agent_id", out _));
        }

        var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"defaults"}""");

        var block = MintBackOf(result);
        Assert.Contains("[session_id issued", block, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", block, StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.False(HasTag(e, "agentcat_agent_id"));
        Assert.False(HasTag(e, "agentcat_agent_id_source"));
    }

    // v2/get-more-tools.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Answers_with_the_canned_text_and_publishes_an_event_carrying_userIntent(string? protocolVersion)
    {
        const string missing = "Need a database query tool for SQL operations";
        await using var h = GetMoreToolsHighLevel();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("get_more_tools", "{\"context\":" + Quote(missing) + "}");

        var answer = ((TextContentBlock)result.Content[1]).Text;
        Assert.Contains("Unfortunately", answer, StringComparison.Ordinal);
        Assert.Contains("we have shown you the full tool list", answer, StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(ToolCall, (string?)e["eventType"]);
        Assert.Equal("get_more_tools", (string?)e["resourceName"]);
        Assert.Equal(missing, (string?)e["userIntent"]);
        Assert.Equal(missing, (string?)RawArgs(e)?["context"]);
        Assert.Matches("^ses_", (string?)e["sessionId"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Applies_the_mint_back_to_its_result_the_published_event_keeps_only_the_answer(string? protocolVersion)
    {
        await using var h = GetMoreToolsHighLevel(o => o.EnableAgentTracking = true);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("get_more_tools", "{\"context\":\"need a search tool\",\"agent_id\":" + Quote(Agent) + "}");

        var block = MintBackOf(result);
        Assert.Contains("[session_id issued", block, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", block, StringComparison.Ordinal);
        Assert.Matches("^ses_", HandleFrom(block!, "session_id"));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(HandleFrom(block!, "session_id"), (string?)e["sessionId"]);
        Assert.Equal(Agent, Tag(e, "agentcat_agent_id"));
        Assert.Equal("supplied", Tag(e, "agentcat_agent_id_source"));
        Assert.DoesNotContain("[session_id", Json(e["response"]), StringComparison.Ordinal);
        Assert.Contains("we have shown you the full tool list", Json(e["response"]), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Keeps_task_continuity_when_a_supplied_session_id_spans_customer_tools_and_get_more_tools(string? protocolVersion)
    {
        var sessionId = Sid("gmt_continuity");
        await using var h = GetMoreToolsHighLevel();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("echo", "{\"msg\":\"one\",\"context\":\"before reporting\",\"session_id\":" + Quote(sessionId) + "}");
        await h.FlushAsync();
        var gmt = await h.CallToolAsync("get_more_tools", "{\"context\":\"need a bulk import tool\",\"session_id\":" + Quote(sessionId) + "}");
        await h.FlushAsync();
        await h.CallToolAsync("echo", "{\"msg\":\"two\",\"context\":\"after reporting\",\"session_id\":" + Quote(sessionId) + "}");

        Assert.Null(MintBackOf(gmt));
        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(3, events.Length);
        Assert.All(events, e => Assert.Equal(sessionId, (string?)e["sessionId"]));
        AssertSeq(events.Select(e => (string?)e["resourceName"]).ToArray(), "echo", "get_more_tools", "echo");
    }

    // v2/track-highlevel.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Captures_events_strips_injected_args_loose_schema_prepends_mint_back_mirrors_structuredContent(string? protocolVersion)
    {
        IDictionary<string, JsonElement>? lastSeenArgs = null;
        await using var h = new EngineHarness();
        h.AddTool(McpServerTool.Create(
            (RequestContext<CallToolRequestParams> request) =>
            {
                lastSeenArgs = request.Params?.Arguments;
                var msg = lastSeenArgs is not null && lastSeenArgs.TryGetValue("msg", out var m) ? m.GetString() : null;
                return new Echoed(msg ?? "");
            },
            new McpServerToolCreateOptions { Name = "echo", Description = "Echo", UseStructuredContent = true }));
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"test intent"}""");

        Assert.NotNull(lastSeenArgs);
        AssertSeq(Keys(lastSeenArgs), "msg");
        Assert.Contains("[session_id issued", ((TextContentBlock)result.Content[0]).Text, StringComparison.Ordinal);
        Assert.Matches("^ses_", result.StructuredContent!.Value.GetProperty("mcp_session").GetProperty("session_id").GetString());
        var events = await h.FlushAsync();
        var e = Assert.Single(events);
        Assert.Equal("echo", (string?)e["resourceName"]);
        Assert.Matches("^ses_", (string?)e["sessionId"]);
        Assert.Equal("test intent", (string?)e["userIntent"]);
    }

    // handles.test.ts (appendMintBack, mirrorStructuredMintBack)

    [Fact]
    public void Prepends_a_text_block_as_the_first_content_element()
    {
        var out1 = ResultDecorator.Decorate(Mcp.Result("""{"content":[{"type":"text","text":"hi"}]}"""), "BLOCK", null, false);

        Assert.Equal(2, out1.Content.Count);
        Assert.Equal("BLOCK", ((TextContentBlock)out1.Content[0]).Text);
        Assert.Equal("hi", ((TextContentBlock)out1.Content[1]).Text);
        Assert.Equal("""{"content":[{"type":"text","text":"BLOCK"},{"type":"text","text":"hi"}]}""", Serialize(out1));
    }

    [Fact]
    public void Prepends_on_isError_results_too()
    {
        var out1 = ResultDecorator.Decorate(Mcp.Result("""{"isError":true,"content":[{"type":"text","text":"boom"}]}"""), "BLOCK", null, false);

        Assert.Equal(2, out1.Content.Count);
        Assert.Equal("BLOCK", ((TextContentBlock)out1.Content[0]).Text);
        Assert.True(out1.IsError);
    }

    [Fact]
    public void Leaves_non_array_content_untouched()
    {
        var r1 = Mcp.Result("""{"content":null,"structuredContent":{"a":1}}""");

        var out1 = ResultDecorator.Decorate(r1, "BLOCK", null, false);

        Assert.Null(out1.Content);
        Assert.Equal(Serialize(r1), Serialize(out1));
        Assert.Equal("""{"structuredContent":{"a":1}}""", Serialize(out1));
    }

    [Fact]
    public void Does_not_mutate_the_original_result()
    {
        var original = Mcp.Result("""{"content":[{"type":"text","text":"hi"}],"structuredContent":{"a":1}}""");
        var content = original.Content;
        var before = Serialize(original);

        var out1 = ResultDecorator.Decorate(original, "BLOCK", new JsonObject { ["session_id"] = Sid("t"), ["status"] = "issued" }, true);

        Assert.Equal(before, Serialize(original));
        Assert.Same(content, original.Content);
        Assert.Single(original.Content);
        Assert.NotSame(original, out1);
        Assert.NotSame(original.Content, out1.Content);
        Assert.Equal("mcp_session", Keys(out1.StructuredContent!.Value)[0]);
    }

    [Fact]
    public void Returns_the_input_untouched_when_structuredContent_is_absent_or_not_a_plain_object()
    {
        var mint = new JsonObject { ["session_id"] = Sid("t"), ["status"] = "issued" };
        foreach (var json in new[]
        {
            """{"content":[]}""",
            """{"content":[],"structuredContent":[1,2]}""",
            """{"content":[],"structuredContent":"nope"}""",
            """{"content":[],"structuredContent":{"mcp_session":"customer-owned"}}""",
        })
        {
            var original = Mcp.Result(json);

            var out1 = ResultDecorator.Decorate(original, null, mint, true);

            Assert.Equal(Serialize(original), Serialize(out1));
            Assert.Equal(json, Serialize(original));
        }
    }

    // Cached results, argument restoration and other cross-cutting behaviors

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Cached_result_objects_are_never_mutated(string? protocolVersion)
    {
        var cached = Mcp.Result("""{"content":[{"type":"text","text":"cached"}],"structuredContent":{"count":1},"_meta":{"k":"v"}}""");
        var snapshot = Serialize(cached);
        var content = cached.Content;
        var block = cached.Content[0];
        var meta = cached.Meta;
        await using var h = new EngineHarness();
        h.AddHandlerTool(
            """{"name":"cached","inputSchema":{"type":"object"},"outputSchema":{"type":"object","properties":{"count":{"type":"number"}}}}""",
            (_, _) => new ValueTask<CallToolResult>(cached));
        h.AddTool(McpServerTool.Create(() => cached, new McpServerToolCreateOptions { Name = "cached_attr" }));
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        foreach (var name in new[] { "cached", "cached", "cached_attr", "cached_attr" })
        {
            var result = await h.CallToolAsync(name);

            Assert.Equal(1, SessionBlocks(result));
            Assert.NotNull(MintBackOf(result));
            Assert.Equal(2, result.Content.Count);
            Assert.Equal("cached", ((TextContentBlock)result.Content[1]).Text);
            var structured = result.StructuredContent!.Value;
            if (name == "cached")
            {
                AssertSeq(Keys(structured), "mcp_session", "count");
            }
            else
            {
                AssertSeq(Keys(structured), "count");
            }
        }

        Assert.Equal(snapshot, Serialize(cached));
        Assert.Same(content, cached.Content);
        Assert.Same(block, Assert.Single(cached.Content));
        Assert.Same(meta, cached.Meta);
        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(4, events.Length);
        Assert.All(events, e => Assert.Equal("""{"content":[{"type":"text","text":"cached"}],"structuredContent":{"count":1},"_meta":{"k":"v"}}""", Json(e["response"])));
    }

    // The steady state: the agent sends back its session and the tool has no structured output, so AgentCat adds nothing.
    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Results_with_nothing_to_add_are_returned_by_reference_on_1x(string? protocolVersion)
    {
        Assert.SkipWhen(McpHarness.McpMajor >= 2, "MCP 2.x stamps resultType on the result that the filters return, so AgentCat always returns a copy.");
        var plain = Mcp.Result("""{"content":[{"type":"text","text":"plain"}]}""");
        var structured = Mcp.Result("""{"content":[{"type":"text","text":"structured"}],"structuredContent":{"count":1}}""");
        var plainJson = Serialize(plain);
        var structuredJson = Serialize(structured);
        var returned = new List<CallToolResult>();
        await using var h = new EngineHarness();
        h.AddHandlerTool("""{"name":"plain","inputSchema":{"type":"object"}}""", (_, _) => new ValueTask<CallToolResult>(plain));
        h.AddHandlerTool(
            """{"name":"structured","inputSchema":{"type":"object"},"outputSchema":{"type":"object","properties":{"count":{"type":"number"}}}}""",
            (_, _) => new ValueTask<CallToolResult>(structured));
        InstallWithOuterFilter(h, async (context, next, ct) =>
        {
            var result = await next(context, ct);
            lock (returned)
            {
                returned.Add(result);
            }

            return result;
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var minted = await h.CallToolAsync("plain");
        var sid = HandleFrom(MintBackOf(minted)!, "session_id");
        var active = await h.CallToolAsync("plain", "{\"session_id\":" + Quote(sid) + "}");
        var mirrored = await h.CallToolAsync("structured", "{\"session_id\":" + Quote(sid) + "}");

        Assert.Equal(3, returned.Count);
        Assert.NotSame(plain, returned[0]);
        Assert.Same(plain, returned[1]);
        Assert.NotSame(structured, returned[2]);
        Assert.Null(MintBackOf(active));
        Assert.Equal("plain", ((TextContentBlock)Assert.Single(active.Content)).Text);
        Assert.Null(MintBackOf(mirrored));
        AssertSeq(Keys(mirrored.StructuredContent!.Value), "mcp_session", "count");
        Assert.Equal(plainJson, Serialize(plain));
        Assert.Equal(structuredJson, Serialize(structured));
        var events = CallEvents(await h.FlushAsync());
        Assert.Equal(3, events.Length);
        Assert.All(events, e => Assert.Equal(sid, (string?)e["sessionId"]));
        Assert.Equal(2, events.Count(e => Json(e["response"]) == plainJson));
        Assert.Single(events, e => Json(e["response"]) == structuredJson);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Arguments_are_restored_after_the_call(string? protocolVersion)
    {
        IDictionary<string, JsonElement>? before = null;
        IDictionary<string, JsonElement>? after = null;
        IDictionary<string, JsonElement>? inner = null;
        string[]? innerKeys = null;
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around((context, next, ct) =>
        {
            inner = context.Params?.Arguments;
            innerKeys = Keys(inner);
            return next(context, ct);
        })));
        InstallWithOuterFilter(h, async (context, next, ct) =>
        {
            before = context.Params?.Arguments;
            var result = await next(context, ct);
            after = context.Params?.Arguments;
            return result;
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("echo", "{\"text\":\"hi\",\"session_id\":" + Quote(Sid("restore")) + ",\"agent_id\":" + Quote(Agent) + ",\"context\":\"why\"}");

        Assert.NotNull(before);
        Assert.Same(before, after);
        AssertSeq(Keys(after), "text", "session_id", "agent_id", "context");
        Assert.NotSame(before, inner);
        AssertSeq(innerKeys!, "text");
        AssertSeq(Keys(Assert.Single(received)), "text");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Task_augmented_calls_on_1x_are_neither_restored_nor_decorated(string? protocolVersion)
    {
        Assert.SkipWhen(McpHarness.McpMajor >= 2, "Core task-augmented tools/call exists only on MCP 1.x.");
        var started = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var createOptions = new McpServerToolCreateOptions { Name = "slow" };
        AllowTasks(createOptions);
        var tool = McpServerTool.Create(
            async (RequestContext<CallToolRequestParams> request) =>
            {
                started.TrySetResult(Keys(request.Params?.Arguments));
                await FakeAgentCatApi.WithinAsync(release.Task, FakeAgentCatApi.WaitBound);
                finished.TrySetResult(Keys(request.Params?.Arguments));
                return "slow-done";
            },
            createOptions);
        using var store = NewTaskStore();
        await using var h = new EngineHarness();
        h.AddTool(tool);
        h.Configure(o => typeof(McpServerOptions).GetProperty("TaskStore")!.SetValue(o, store));
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolParamsAsync("""{"name":"slow","arguments":{"x":1,"context":"because","session_id":"start"},"task":{}}""");
        await FakeAgentCatApi.WithinAsync(started.Task, FakeAgentCatApi.WaitBound);
        release.SetResult(true);
        await FakeAgentCatApi.WithinAsync(finished.Task, FakeAgentCatApi.WaitBound);

        Assert.Contains("\"task\"", Serialize(result), StringComparison.Ordinal);
        Assert.Empty(result.Content);
        AssertSeq(await started.Task, "x");
        AssertSeq(await finished.Task, "x");
        var e = CallEvent(await h.FlushAsync());
        Assert.NotNull(e["response"]?["task"]);
        Assert.Equal("minted", Tag(e, "agentcat_session_id_source"));
        Assert.DoesNotContain(h.Log.Lines, l => l.StartsWith(PostHandlerPrefix, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Duplicate_structured_keys_do_not_break_the_mirror(string? protocolVersion)
    {
        CallToolResult? returned = null;
        await using var h = new EngineHarness();
        h.AddHandlerTool(
            """{"name":"dup","inputSchema":{"type":"object"},"outputSchema":{"type":"object","properties":{"a":{"type":"number"}}}}""",
            (_, _) => new ValueTask<CallToolResult>(Mcp.Result("""{"content":[{"type":"text","text":"dup"}],"structuredContent":{"a":1,"a":2}}""")));
        InstallWithOuterFilter(h, async (context, next, ct) => returned = await next(context, ct));
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("dup");

        AssertSeq(Keys(returned!.StructuredContent!.Value), "mcp_session", "a", "a");
        Assert.Equal("mcp_session", Keys(result.StructuredContent!.Value)[0]);
        Assert.NotNull(MintBackOf(result));
        Assert.DoesNotContain(h.Log.Lines, l => l.StartsWith(PostHandlerPrefix, StringComparison.Ordinal));
        Assert.Equal("""{"a":2}""", Json(CallEvent(await h.FlushAsync())["response"]?["structuredContent"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Matched_primitive_protocol_tool_is_unchanged_after_a_call(string? protocolVersion)
    {
        var toolInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));
        var tool = McpServerTool.Create((string text) => "echo: " + text, new McpServerToolCreateOptions { Name = "attr" });
        var protocolTool = tool.ProtocolTool;
        var snapshot = JsonSerializer.Serialize(protocolTool, toolInfo);
        IMcpServerPrimitive? matched = null;
        await using var h = new EngineHarness();
        h.AddTool(tool);
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around((context, next, ct) =>
        {
            matched = context.MatchedPrimitive;
            return next(context, ct);
        })));
        await h.StartAsync(protocolVersion);
        var listed = Assert.Single((await h.ListToolsAsync()).Tools, t => t.Name == "attr");
        Assert.True(listed.InputSchema.GetProperty("properties").TryGetProperty("session_id", out _));

        await h.CallToolAsync("attr", """{"text":"hi","context":"why","session_id":"start"}""");

        Assert.Same(tool, matched);
        Assert.Same(protocolTool, ((McpServerTool)matched!).ProtocolTool);
        Assert.Equal(snapshot, JsonSerializer.Serialize(protocolTool, toolInfo));
        Assert.False(protocolTool.InputSchema.GetProperty("properties").TryGetProperty("session_id", out _));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task AgentCat_items_are_removed_at_the_start_of_every_call(string? protocolVersion)
    {
        var seen = new List<string[]>();
        await using var h = new EngineHarness();
        h.AddTool((string text) => "echo: " + text, "echo");
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around((context, next, ct) =>
        {
            lock (seen)
            {
                seen.Add(context.Items.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            }

            return next(context, ct);
        })));
        InstallWithOuterFilter(h, (context, next, ct) =>
        {
            context.Items[CallToolSeam.ExceptionItem] = new InvalidOperationException("stale");
            context.Items[CallToolSeam.SessionTextItem] = "stale";
            context.Items["user.key"] = "kept";
            return next(context, ct);
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("echo", """{"text":"hi"}""");

        var keys = Assert.Single(seen);
        Assert.Contains("user.key", keys);
        Assert.DoesNotContain(CallToolSeam.ExceptionItem, keys);
        Assert.DoesNotContain(CallToolSeam.SessionTextItem, keys);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_call_without_arguments_still_mints_and_publishes(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolParamsAsync("""{"name":"echo"}""");

        Assert.Empty(Assert.Single(received));
        Assert.Contains("[session_id issued", MintBackOf(result), StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(HandleFrom(MintBackOf(result)!, "session_id"), (string?)e["sessionId"]);
        Assert.Equal("echo", (string?)RawParams(e)?["name"]);
        Assert.Null(RawArgs(e));
        Assert.Null(e["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Arguments_that_are_only_an_IDictionary_are_read_in_place(string? protocolVersion)
    {
        OnlyDictionary? replaced = null;
        IDictionary<string, JsonElement>? afterCall = null;
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = LowLevel(received);
        InstallWithOuterFilter(h, async (context, next, ct) =>
        {
            replaced = new OnlyDictionary(context.Params!.Arguments!);
            context.Params.Arguments = replaced;
            var result = await next(context, ct);
            afterCall = context.Params.Arguments;
            return result;
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", "{\"text\":\"hi\",\"session_id\":" + Quote(Sid("only")) + ",\"agent_id\":" + Quote(Agent) + ",\"context\":\"why\"}");

        Assert.Null(MintBackOf(result));
        AssertSeq(Keys(Assert.Single(received)), "text");
        Assert.Same(replaced, afterCall);
        AssertSeq(Keys(replaced), "text", "session_id", "agent_id", "context");
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("only"), (string?)e["sessionId"]);
        Assert.Equal(Agent, Tag(e, "agentcat_agent_id"));
        Assert.Equal("why", (string?)e["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Non_string_context_becomes_raw_json_user_intent(string? protocolVersion)
    {
        await using var h = GetMoreToolsHighLevel();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("echo", """{"msg":"a","context":{"why":["x",1]}}""");
        await h.FlushAsync();
        await h.CallToolAsync("echo", """{"msg":"b","context":null}""");
        await h.FlushAsync();
        await h.CallToolAsync("echo", """{"msg":"c","context":12.50}""");

        var events = CallEvents(await h.FlushAsync());
        Assert.Equal("""{"why":["x",1]}""", (string?)events[0]["userIntent"]);
        Assert.Null(events[1]["userIntent"]);
        Assert.Equal("12.50", (string?)events[2]["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Context_off_publishes_no_user_intent_and_never_for_a_customer_get_more_tools(string? protocolVersion)
    {
        await using var off = GetMoreToolsHighLevel(o => o.EnableToolCallContext = false);
        await off.StartAsync(protocolVersion);
        await off.CallToolAsync("echo", """{"msg":"a","context":"why"}""");
        await off.CallToolAsync("get_more_tools", """{"context":"need it"}""");
        var offEvents = CallEvents(await off.FlushAsync());

        await using var own = new EngineHarness(options: new AgentCatOptions { EnableReportMissing = false });
        own.AddHandlerTool("""{"name":"get_more_tools","inputSchema":{"type":"object"}}""", (_, _) => Text("customer answer"));
        await own.StartAsync(protocolVersion);
        await own.CallToolAsync("get_more_tools", """{"context":"need it"}""");
        var ownEvent = CallEvent(await own.FlushAsync());

        Assert.Null(Assert.Single(offEvents, e => (string?)e["resourceName"] == "echo")["userIntent"]);
        Assert.Equal("need it", (string?)Assert.Single(offEvents, e => (string?)e["resourceName"] == "get_more_tools")["userIntent"]);
        Assert.Null(ownEvent["userIntent"]);
    }

    // Stripping on the tracing-off and degraded paths

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tracing_off_strips_injected_context(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = new EngineHarness(options: new AgentCatOptions { EnableTracing = false });
        AddRecorded(h, LooseTool, received);
        await h.StartAsync(protocolVersion);
        var listed = Assert.Single((await h.ListToolsAsync()).Tools, t => t.Name == "loose");
        AssertSeq(Keys(listed.InputSchema.GetProperty("properties")), "text", "context");

        var result = await h.CallToolAsync("loose", """{"text":"hi","context":"why"}""");

        AssertSeq(Keys(Assert.Single(received)), "text");
        Assert.Equal("loose: hi", ((TextContentBlock)Assert.Single(result.Content)).Text);
        Assert.Empty(await h.FlushAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tracing_off_first_call_keeps_a_customer_owned_context(string? protocolVersion)
    {
        var notes = new List<Dictionary<string, JsonElement>>();
        var loose = new List<Dictionary<string, JsonElement>>();
        await using var h = new EngineHarness(options: new AgentCatOptions { EnableTracing = false });
        AddRecorded(h, NotesTool, notes);
        AddRecorded(h, LooseTool, loose);
        await h.StartAsync(protocolVersion);

        var own = await h.CallToolAsync("notes", """{"text":"a","context":"customer value"}""");
        await h.CallToolAsync("loose", """{"text":"b","context":"why"}""");

        var args = Assert.Single(notes);
        AssertSeq(Keys(args), "text", "context");
        Assert.Equal("customer value", args["context"].GetString());
        Assert.Equal("notes: a", ((TextContentBlock)Assert.Single(own.Content)).Text);
        AssertSeq(Keys(Assert.Single(loose)), "text");
        Assert.Single(h.Log.Lines, l => l == RebuiltLine);
        Assert.Empty(await h.FlushAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Degraded_path_strips_by_the_registry(string? protocolVersion)
    {
        var received = new List<Dictionary<string, JsonElement>>();
        await using var h = OwnSessionId(received);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();
        FailAt(h, "prepare", "client info boom");

        var result = await h.CallToolAsync("echo", "{\"text\":\"hi\",\"session_id\":\"cust-1\",\"agent_id\":" + Quote(Agent) + ",\"context\":\"why\"}");

        var args = Assert.Single(received);
        AssertSeq(Keys(args), "text", "session_id");
        Assert.Equal("cust-1", args["session_id"].GetString());
        Assert.Equal("ok", ((TextContentBlock)Assert.Single(result.Content)).Text);
        Assert.Empty(await h.FlushAsync());
        Assert.Single(h.Log.Lines, l => l.StartsWith(TracingFailedPrefix, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Degraded_path_calls_with_raw_arguments_when_the_strip_fails(string? protocolVersion)
    {
        foreach (var tracing in new[] { true, false })
        {
            var received = new List<Dictionary<string, JsonElement>>();
            await using var h = LowLevel(received, o => o.EnableTracing = tracing);
            await h.StartAsync(protocolVersion);
            await h.ListToolsAsync();
            FailAt(h, "strip", "strip boom");

            var result = await h.CallToolAsync("echo", "{\"text\":\"hi\",\"session_id\":" + Quote(Sid("raw")) + ",\"context\":\"why\"}");

            AssertSeq(Keys(Assert.Single(received)), "text", "session_id", "context");
            Assert.NotEqual(true, result.IsError);
            Assert.Equal("echo: hi", ((TextContentBlock)Assert.Single(result.Content)).Text);
            Assert.Empty(await h.FlushAsync());
            Assert.Single(h.Log.Lines, l => l.StartsWith(TracingFailedPrefix, StringComparison.Ordinal));
            Assert.Contains(
                "Warning: AgentCat tracing failed for tool echo, falling back to original handler - InvalidOperationException: strip boom",
                h.Log.Lines);
        }
    }

    // Shared bodies of twin TS tests

    private static async Task AssertFirstCallMints(EngineHarness h)
    {
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", """{"text":"hi","context":"testing intent","session_id":"start"}""");

        var block = MintBackOf(result);
        Assert.Contains("[session_id issued", block, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", block, StringComparison.Ordinal);
        var sessionId = HandleFrom(block!, "session_id");
        Assert.Matches("^ses_", sessionId);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(sessionId, (string?)e["sessionId"]);
        Assert.Equal("minted", Tag(e, "agentcat_session_id_source"));
        Assert.False(HasTag(e, "agentcat_agent_id"));
        Assert.False(HasTag(e, "agentcat_agent_id_source"));
    }

    private static async Task AssertStripsBeforeHandler(EngineHarness h, List<Dictionary<string, JsonElement>> received, string agent)
    {
        await h.ListToolsAsync();

        await h.CallToolAsync("echo", "{\"text\":\"hi\",\"context\":\"why\",\"session_id\":" + Quote(Sid("x")) + ",\"agent_id\":" + Quote(agent) + "}");

        var args = Assert.Single(received);
        AssertSeq(Keys(args), "text");
        Assert.Equal("hi", args["text"].GetString());
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("x"), (string?)RawArgs(e)?["session_id"]);
        Assert.Equal(agent, (string?)RawArgs(e)?["agent_id"]);
        Assert.Equal("why", (string?)RawArgs(e)?["context"]);
    }

    private static async Task AssertMintingCallStaysOffTheEvent(EngineHarness h)
    {
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", """{"text":"hi","context":"testing intent"}""");

        Assert.Contains("[session_id issued", MintBackOf(result), StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", MintBackOf(result), StringComparison.Ordinal);
        var response = CallEvent(await h.FlushAsync())["response"];
        Assert.DoesNotContain("[session_id", Json(response), StringComparison.Ordinal);
        Assert.Equal("""[{"type":"text","text":"echo: hi"}]""", Json(response?["content"]));
    }

    private static async Task AssertMintingErrorCallStaysOffTheEvent(EngineHarness h)
    {
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", """{"text":"explode","context":"testing"}""");

        Assert.True(result.IsError);
        Assert.Contains("[session_id issued", MintBackOf(result), StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", MintBackOf(result), StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(true, (bool?)e["isError"]);
        Assert.DoesNotContain("[session_id", Json(e["response"]), StringComparison.Ordinal);
        Assert.Equal("""{"content":[{"type":"text","text":"boom"}],"isError":true}""", Json(e["response"]));
        Assert.Equal("boom", (string?)e["error"]?["message"]);
        Assert.DoesNotContain("[session_id", Json(e["error"]), StringComparison.Ordinal);
    }

    private static async Task AssertEchoedResponseIsExact(EngineHarness h, string agent)
    {
        await h.ListToolsAsync();

        await h.CallToolAsync("echo", "{\"text\":\"hi\",\"context\":\"testing\",\"session_id\":" + Quote(Sid("fixed")) + ",\"agent_id\":" + Quote(agent) + "}");

        Assert.Equal("""{"content":[{"type":"text","text":"echo: hi"}]}""", Json(CallEvent(await h.FlushAsync())["response"]));
    }

    private static EngineHarness MetaCapturing(List<string?> metas)
    {
        var h = LowLevel(new List<Dictionary<string, JsonElement>>());
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around((context, next, ct) =>
        {
            lock (metas)
            {
                metas.Add(context.JsonRpcRequest.Params?["_meta"]?.ToJsonString());
            }

            return next(context, ct);
        })));
        return h;
    }

    // The reserved keys pass through verbatim on 1.x; a 2.x client replaces them with its own on 2026-07-28, and a
    // 2.x server rejects them on a legacy session.
    private static async Task AssertMetaReachesTheEvent(EngineHarness h, List<string?> metas, string clientName, string clientVersion)
    {
        await h.ListToolsAsync();
        var negotiated = h.Client.NegotiatedProtocolVersion!;
        var meta = new JsonObject { ["app/trace"] = "t1" };
        if (McpHarness.McpMajor == 1 || negotiated != McpHarness.LegacyProtocol)
        {
            meta["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = clientName, ["version"] = clientVersion };
            meta["io.modelcontextprotocol/protocolVersion"] = negotiated;
        }

        var result = await h.CallToolParamsAsync("{\"name\":\"echo\",\"arguments\":{\"text\":\"hi\",\"context\":\"testing\"},\"_meta\":" + meta.ToJsonString() + "}");

        Assert.NotNull(MintBackOf(result));
        var e = CallEvent(await h.FlushAsync());
        var eventMeta = Assert.IsType<JsonObject>(RawParams(e)?["_meta"]);
        Assert.Equal(Assert.Single(metas), eventMeta.ToJsonString());
        Assert.Equal("t1", (string?)eventMeta["app/trace"]);
        var received = (JsonObject)JsonNode.Parse(metas[0]!)!;
        var sent = received["io.modelcontextprotocol/clientInfo"] as JsonObject;
        Assert.Equal((string?)sent?["name"] ?? "harness-client", (string?)e["clientName"]);
        Assert.Equal((string?)sent?["version"] ?? "3.1.4", (string?)e["clientVersion"]);
        Assert.Equal((string?)received["io.modelcontextprotocol/protocolVersion"], Tag(e, "agentcat_protocol_version"));
        if (McpHarness.McpMajor == 1)
        {
            Assert.Equal(clientName, (string?)e["clientName"]);
            Assert.Equal(clientVersion, (string?)e["clientVersion"]);
            Assert.Equal(negotiated, Tag(e, "agentcat_protocol_version"));
            Assert.Equal(clientName, (string?)eventMeta["io.modelcontextprotocol/clientInfo"]?["name"]);
            Assert.Equal(clientVersion, (string?)eventMeta["io.modelcontextprotocol/clientInfo"]?["version"]);
            Assert.Equal(negotiated, (string?)eventMeta["io.modelcontextprotocol/protocolVersion"]);
        }

        Assert.Equal("testing", (string?)e["userIntent"]);
    }

    private static async Task AssertGetMoreToolsCall(string? protocolVersion, string context)
    {
        await using var h = Todos(o =>
        {
            o.EnableReportMissing = true;
            o.EnableTracing = true;
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("get_more_tools", "{\"context\":" + Quote(context) + "}");

        Assert.Contains("Unfortunately", ((TextContentBlock)result.Content[1]).Text, StringComparison.Ordinal);
        var e = Assert.Single(CallEvents(await h.FlushAsync()), x => (string?)x["resourceName"] == "get_more_tools");
        Assert.Equal(context, (string?)RawArgs(e)?["context"]);
    }

    private static void AllowTasks(McpServerToolCreateOptions options)
    {
        var execution = typeof(McpServerToolCreateOptions).GetProperty("Execution")!;
        var value = Activator.CreateInstance(execution.PropertyType)!;
        var support = execution.PropertyType.GetProperty("TaskSupport")!;
        var supportType = Nullable.GetUnderlyingType(support.PropertyType) ?? support.PropertyType;
        support.SetValue(value, Enum.Parse(supportType, "Optional"));
        execution.SetValue(options, value);
    }

    private static IDisposable NewTaskStore()
    {
        var type = typeof(McpServer).Assembly.GetType("ModelContextProtocol.InMemoryMcpTaskStore", throwOnError: true)!;
        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single();
        return (IDisposable)constructor.Invoke(constructor.GetParameters().Select(p => p.DefaultValue).ToArray());
    }

    private sealed class OnlyDictionary(IDictionary<string, JsonElement> inner) : IDictionary<string, JsonElement>
    {
        public ICollection<string> Keys => inner.Keys;

        public ICollection<JsonElement> Values => inner.Values;

        public int Count => inner.Count;

        public bool IsReadOnly => inner.IsReadOnly;

        public JsonElement this[string key]
        {
            get => inner[key];
            set => inner[key] = value;
        }

        public void Add(string key, JsonElement value) => inner.Add(key, value);

        public void Add(KeyValuePair<string, JsonElement> item) => inner.Add(item);

        public void Clear() => inner.Clear();

        public bool Contains(KeyValuePair<string, JsonElement> item) => inner.Contains(item);

        public bool ContainsKey(string key) => inner.ContainsKey(key);

        public void CopyTo(KeyValuePair<string, JsonElement>[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);

        public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator() => inner.GetEnumerator();

        public bool Remove(string key) => inner.Remove(key);

        public bool Remove(KeyValuePair<string, JsonElement> item) => inner.Remove(item);

        public bool TryGetValue(string key, out JsonElement value) => inner.TryGetValue(key, out value);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed record Stats(int Count);

    private sealed record Echoed(string Msg);
}
