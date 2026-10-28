using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Engine;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Engine;

public class ListToolsSeamTests
{
    private const string DefaultContextDescription =
        "Explain why you are calling this tool and how it fits into the user's overall goal. This parameter is used for analytics and user intent tracking. YOU MUST provide 15-25 words (count carefully). NEVER use first person ('I', 'we', 'you') - maintain third-person perspective. NEVER include sensitive information such as credentials, passwords, or personal data. Example (20 words): \"Searching across the organization's repositories to find all open issues related to performance complaints and latency issues for team prioritization.\"";

    private const string SessionIdPattern = "^(start|ses_[0-9A-Za-z]{27})$";

    private const string NoToolsWarning =
        "Warning: No tools found in the original list. This is likely due to the tools not being registered before AgentCat.track().";

    private const string InjectionFailedPrefix =
        "Warning: AgentCat tool-list injection failed; serving the original tool list unmodified - ";

    public static TheoryData<string?> Protocols => McpHarness.ProtocolVersions;

    private static readonly ValueTask<CallToolResult> Ok = new(Mcp.Result("""{"content":[{"type":"text","text":"ok"}]}"""));

    private static Tool Find(ListToolsResult listing, string name) => Assert.Single(listing.Tools, t => t.Name == name);

    private static JsonElement Props(Tool tool) => tool.InputSchema.GetProperty("properties");

    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    private static string[] Required(Tool tool)
        => tool.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static void AssertSeq(IEnumerable<string> actual, params string[] expected)
        => Assert.Equal(expected, actual.ToArray());

    private static int Count(ListToolsResult listing, string name) => listing.Tools.Count(t => t.Name == name);

    private static EngineHarness Harness(AgentCatOptions? options = null) => new(options: options);

    private static void AddHandler(McpHarness h, string name, string inputSchema = """{"type":"object"}""", string? extra = null)
        => h.AddHandlerTool(
            "{\"name\":\"" + name + "\",\"description\":\"" + name + " tool\",\"inputSchema\":" + inputSchema + (extra is null ? "" : "," + extra) + "}",
            (_, _) => Ok);

    private static string Serialize(Tool tool)
        => JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool)));

    private static string Serialize(ListToolsResult result)
        => JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListToolsResult)));

    private static void UseListHandler(McpHarness h, Func<string?, ListToolsResult> handler)
        => h.Configure(o => o.Handlers.ListToolsHandler = (context, _) => new ValueTask<ListToolsResult>(handler(context.Params?.Cursor)));

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_tools_with_shorthand_Zod_schema_syntax_reproduces_context_injection_issue(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableToolCallContext = true, EnableTracing = true });
        h.AddTool((string note) => "note:" + note, "seed");
        await h.StartAsync(protocolVersion);

        h.ServerOptions.ToolCollection!.Add(McpServerTool.Create(
            (double a, double b) => (a + b).ToString(System.Globalization.CultureInfo.InvariantCulture),
            new McpServerToolCreateOptions { Name = "calculator_add" }));

        var tool = Find(await h.ListToolsAsync(), "calculator_add");
        var props = Props(tool);
        Assert.Equal("string", props.GetProperty("context").GetProperty("type").GetString());
        Assert.True(props.TryGetProperty("a", out _));
        Assert.True(props.TryGetProperty("b", out _));
        var result = await h.CallToolAsync("calculator_add", """{"context":"Testing calculator with shorthand syntax","a":5,"b":3}""");
        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "8");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_tools_defined_BEFORE_track_with_shorthand_Zod_schema(string? protocolVersion)
    {
        await using var h = Harness();
        h.AddTool((double a, double b) => (a + b).ToString(System.Globalization.CultureInfo.InvariantCulture), "add");
        h.AddTool((string operation, double a, double b) => operation + ":" + (a + b).ToString(System.Globalization.CultureInfo.InvariantCulture), "calculate");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        var add = Props(Find(listing, "add"));
        AssertSeq(Keys(add), "a", "b", "session_id", "context");
        var calculate = Props(Find(listing, "calculate"));
        AssertSeq(Keys(calculate), "operation", "a", "b", "session_id", "context");
        var result = await h.CallToolAsync("add", """{"context":"adding","a":5,"b":3}""");
        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "8");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_inject_context_into_tool_schemas_when_listing_tools(string? protocolVersion)
    {
        await using var h = Harness();
        h.AddTool((string title) => "added:" + title, "add_todo");
        AddHandler(h, "handler_tool", """{"type":"object","properties":{"id":{"type":"string"}}}""");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        foreach (var name in new[] { "add_todo", "handler_tool" })
        {
            var tool = Find(listing, name);
            var context = Props(tool).GetProperty("context");
            Assert.Equal("string", context.GetProperty("type").GetString());
            Assert.Contains("context", Required(tool));
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_use_default_context_description_when_no_custom_description_is_provided_context_parameters(string? protocolVersion)
    {
        await using var h = Harness();
        h.AddTool((string title) => "added:" + title, "add_todo");
        await h.StartAsync(protocolVersion);

        var context = Props(Find(await h.ListToolsAsync(), "add_todo")).GetProperty("context");

        Assert.Equal(DefaultContextDescription, context.GetProperty("description").GetString());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_use_custom_description_instead_of_default_for_JSON_Schema_tools(string? protocolVersion)
    {
        const string custom = "Why are you calling this tool?";
        await using var h = Harness(new AgentCatOptions { CustomContextDescription = custom });
        AddHandler(h, "json_schema_tool", """{"type":"object","properties":{"text":{"type":"string"}}}""");
        await h.StartAsync(protocolVersion);

        var context = Props(Find(await h.ListToolsAsync(), "json_schema_tool")).GetProperty("context");

        Assert.Equal(custom, context.GetProperty("description").GetString());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_use_custom_description_for_Zod_object_schemas(string? protocolVersion)
    {
        const string custom = "Explain your reasoning for this action";
        await using var h = Harness(new AgentCatOptions { CustomContextDescription = custom });
        h.AddTool((string title) => "added:" + title, "add_todo");
        await h.StartAsync(protocolVersion);

        var context = Props(Find(await h.ListToolsAsync(), "add_todo")).GetProperty("context");

        Assert.Equal(custom, context.GetProperty("description").GetString());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_apply_custom_description_to_all_tools_when_listing(string? protocolVersion)
    {
        const string custom = "Describe the goal behind this call";
        await using var h = Harness(new AgentCatOptions { CustomContextDescription = custom });
        h.AddTool((string title) => "added:" + title, "add_todo");
        h.AddTool((string id) => "done:" + id, "complete_todo");
        AddHandler(h, "handler_tool");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        foreach (var tool in listing.Tools.Where(t => t.Name != "get_more_tools"))
        {
            Assert.Equal(custom, Props(tool).GetProperty("context").GetProperty("description").GetString());
        }

        Assert.Equal(4, listing.Tools.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_use_default_description_when_customContextDescription_is_not_provided(string? protocolVersion)
    {
        await using var h = Harness();
        h.AddTool((string title) => "added:" + title, "add_todo");
        await h.StartAsync(protocolVersion);

        var context = Props(Find(await h.ListToolsAsync(), "add_todo")).GetProperty("context");

        Assert.Equal(DefaultContextDescription, context.GetProperty("description").GetString());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_use_custom_description_even_with_long_descriptions(string? protocolVersion)
    {
        const string custom =
            "Please provide a comprehensive explanation of your reasoning, including the broader context of this action within your workflow, the expected outcomes, and how this contributes to your overall objectives. Be as detailed as possible.";
        await using var h = Harness(new AgentCatOptions { CustomContextDescription = custom });
        h.AddTool((string title) => "added:" + title, "add_todo");
        await h.StartAsync(protocolVersion);

        var context = Props(Find(await h.ListToolsAsync(), "add_todo")).GetProperty("context");

        Assert.Equal(custom, context.GetProperty("description").GetString());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_use_custom_description_with_special_characters(string? protocolVersion)
    {
        const string custom = "Why? (explain in detail) - \"Be specific!\"";
        await using var h = Harness(new AgentCatOptions { CustomContextDescription = custom });
        h.AddTool((string title) => "added:" + title, "add_todo");
        await h.StartAsync(protocolVersion);

        var context = Props(Find(await h.ListToolsAsync(), "add_todo")).GetProperty("context");

        Assert.Equal(custom, context.GetProperty("description").GetString());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Passes_through_and_warns_on_empty_or_missing_tools_registries_stay_unset(string? protocolVersion)
    {
        await using var h = Harness();
        UseListHandler(h, _ => Mcp.ListResult("""{"tools":[],"nextCursor":"page-2","_meta":{"origin":"customer"}}"""));
        await h.StartAsync(protocolVersion);

        var first = await h.ListToolsAsync();
        var second = await h.ListToolsAsync();

        foreach (var listing in new[] { first, second })
        {
            Assert.Empty(listing.Tools);
            Assert.Equal("page-2", listing.NextCursor);
            Assert.Equal("customer", (string?)listing.Meta?["origin"]);
        }

        Assert.Equal(2, h.Log.Lines.Count(l => l == NoToolsWarning));
        Assert.False(h.Tracker!.Registries.HasListing);
        Assert.Empty(h.Tracker.Registries.InjectedParams);
        Assert.True(h.Tracker.Registries.MirrorsStructured("anything"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Returns_the_customers_original_listing_when_the_whole_pipeline_fails(string? protocolVersion)
    {
        await using var h = Harness();
        h.Log.ThrowWhen = line => line.Contains("shadowed");
        AddHandler(h, "get_more_tools", """{"type":"object","properties":{"q":{"type":"string"}}}""");
        AddHandler(h, "echo", """{"type":"object","properties":{"text":{"type":"string"}}}""");
        UseListHandler(h, _ => Mcp.ListResult(
            """{"tools":[{"name":"get_more_tools","description":"customer version","inputSchema":{"type":"object","properties":{"q":{"type":"string"}}}},{"name":"echo","description":"echo tool","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}],"nextCursor":"p2"}"""));
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        Assert.Equal("p2", listing.NextCursor);
        AssertSeq(listing.Tools.Select(t => t.Name).ToArray(), "get_more_tools", "echo");
        AssertSeq(Keys(Props(Find(listing, "echo"))), "text");
        Assert.Equal("customer version", Find(listing, "get_more_tools").Description);
        Assert.Contains(h.Log.Lines, l => l == InjectionFailedPrefix + "InvalidOperationException: log boom");
        Assert.False(h.Tracker!.Registries.HasListing);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Publishes_no_tools_list_event_even_when_tag_property_callbacks_are_set(string? protocolVersion)
    {
        var tagCalls = 0;
        var propertyCalls = 0;
        await using var h = Harness(new AgentCatOptions
        {
            EventTags = (_, _) =>
            {
                Interlocked.Increment(ref tagCalls);
                return new ValueTask<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["env"] = "test" });
            },
            EventProperties = (_, _) =>
            {
                Interlocked.Increment(ref propertyCalls);
                return new ValueTask<JsonObject?>(new JsonObject { ["source"] = "list-test" });
            },
        });
        h.AddTool((string msg) => "echo:" + msg, "echo");
        await h.StartAsync(protocolVersion);

        await h.ListToolsAsync();
        var events = await h.FlushAsync();

        Assert.DoesNotContain(events, e => (string?)e["eventType"] == "mcp:tools/list");
        Assert.Empty(events);
        Assert.Equal(0, Volatile.Read(ref tagCalls));
        Assert.Equal(0, Volatile.Read(ref propertyCalls));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Injects_required_handles_into_listed_tool_schemas(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        AddHandler(h, "echo", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        var echo = Find(listing, "echo");
        AssertSeq(Keys(Props(echo)), "text", "session_id", "agent_id", "context");
        AssertSeq(Required(echo), "text", "session_id", "agent_id", "context");
        Assert.Equal(SessionIdPattern, Props(echo).GetProperty("session_id").GetProperty("pattern").GetString());
        var gmt = Find(listing, "get_more_tools");
        Assert.True(Props(gmt).TryGetProperty("session_id", out _));
        Assert.True(Props(gmt).TryGetProperty("agent_id", out _));
        AssertGetMoreToolsAnnotations(gmt);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Publishes_no_tools_list_or_initialize_events(string? protocolVersion)
    {
        await using var h = Harness();
        AddHandler(h, "echo", """{"type":"object","properties":{"text":{"type":"string"}}}""");
        await h.StartAsync(protocolVersion);

        await h.ListToolsAsync();
        await h.CallToolAsync("echo", """{"text":"hi","context":"testing"}""");
        var types = (await h.FlushAsync()).Select(e => (string?)e["eventType"]).ToArray();

        Assert.DoesNotContain("mcp:tools/list", types);
        Assert.DoesNotContain("mcp:initialize", types);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task EnableReportMissing_false_handles_still_inject_no_get_more_tools(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableReportMissing = false, EnableAgentTracking = true });
        AddHandler(h, "echo", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        Assert.Equal(0, Count(listing, "get_more_tools"));
        var echo = Find(listing, "echo");
        Assert.True(Props(echo).TryGetProperty("session_id", out _));
        Assert.True(Props(echo).TryGetProperty("agent_id", out _));
        Assert.Contains("session_id", Required(echo));
        Assert.Contains("agent_id", Required(echo));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Declares_mcp_session_in_a_listed_plain_object_outputSchema(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        AddHandler(h, "stats", extra: """
            "outputSchema":{"type":"object","properties":{"count":{"type":"number"}},"required":["count"],"additionalProperties":false}
            """);
        await h.StartAsync(protocolVersion);

        var stats = Find(await h.ListToolsAsync(), "stats");

        var session = stats.OutputSchema!.Value.GetProperty("properties").GetProperty("mcp_session");
        Assert.Equal("object", session.GetProperty("type").GetString());
        AssertSeq(Keys(session.GetProperty("properties")), "session_id", "agent_id", "status");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_add_report_missing_to_tools_list_when_enableReportMissing_is_true(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableReportMissing = true, EnableTracing = true });
        h.AddTool((string title) => "added:" + title, "add_todo");
        await h.StartAsync(protocolVersion);

        var gmt = Find(await h.ListToolsAsync(), "get_more_tools");

        Assert.Contains("Check for additional tools", gmt.Description);
        Assert.Contains("context", Required(gmt));
        AssertGetMoreToolsAnnotations(gmt);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_NOT_add_get_more_tools_when_enableReportMissing_is_false(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableReportMissing = false, EnableTracing = true });
        h.AddTool((string title) => "added:" + title, "add_todo");
        await h.StartAsync(protocolVersion);

        Assert.Equal(0, Count(await h.ListToolsAsync(), "get_more_tools"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_add_report_missing_WITHOUT_context_injection_even_when_enableToolCallContext_is_true(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableReportMissing = true, EnableToolCallContext = true, EnableTracing = true });
        h.AddTool((string title) => "added:" + title, "add_todo");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        var gmt = Find(listing, "get_more_tools");
        Assert.Contains("context", Required(gmt));
        Assert.NotEqual(DefaultContextDescription, Props(gmt).GetProperty("context").GetProperty("description").GetString());
        var addTodo = Find(listing, "add_todo");
        var context = Props(addTodo).GetProperty("context");
        AssertSeq(Keys(context), "type", "description");
        Assert.Equal("string", context.GetProperty("type").GetString());
        Assert.Equal(DefaultContextDescription, context.GetProperty("description").GetString());
        Assert.Contains("context", Required(addTodo));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tools_list_declares_mcp_session_on_get_stats_schema_less_tools_untouched(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        h.AddTool(McpServerTool.Create(
            () => new Stats(0),
            new McpServerToolCreateOptions { Name = "get_stats", UseStructuredContent = true }));
        h.AddTool((string title) => "added:" + title, "add_todo");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        var session = Find(listing, "get_stats").OutputSchema!.Value.GetProperty("properties").GetProperty("mcp_session");
        Assert.Equal("object", session.GetProperty("type").GetString());
        AssertSeq(Keys(session.GetProperty("properties")), "session_id", "agent_id", "status");
        Assert.Null(Find(listing, "add_todo").OutputSchema);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Advertises_both_handles_as_required_on_every_tool_including_get_more_tools(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        h.AddTool((string msg) => "echo:" + msg, "echo");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        foreach (var name in new[] { "echo", "get_more_tools" })
        {
            var tool = Find(listing, name);
            Assert.True(Props(tool).TryGetProperty("agent_id", out _));
            Assert.True(Props(tool).TryGetProperty("session_id", out _));
            Assert.Contains("agent_id", Required(tool));
            Assert.Contains("session_id", Required(tool));
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Advertises_the_tool_with_required_bespoke_context_handles_and_honest_annotations(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        h.AddTool((string msg) => "echo:" + msg, "echo");
        await h.StartAsync(protocolVersion);

        var gmt = Find(await h.ListToolsAsync(), "get_more_tools");

        Assert.Contains("Check for additional tools", gmt.Description);
        Assert.Contains("context", Required(gmt));
        Assert.Contains("session_id", Required(gmt));
        Assert.Contains("agent_id", Required(gmt));
        Assert.True(Props(gmt).TryGetProperty("session_id", out _));
        Assert.True(Props(gmt).TryGetProperty("agent_id", out _));
        AssertGetMoreToolsAnnotations(gmt);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Injects_required_handles_into_listed_tool_schemas_in_canonical_order(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        AddHandler(h, "echo", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        var echo = Find(listing, "echo");
        AssertSeq(Keys(Props(echo)), "text", "session_id", "agent_id", "context");
        AssertSeq(Required(echo), "text", "session_id", "agent_id", "context");
        var gmt = Find(listing, "get_more_tools");
        Assert.True(Props(gmt).TryGetProperty("session_id", out _));
        Assert.True(Props(gmt).TryGetProperty("agent_id", out _));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Publishes_no_tools_list_or_initialize_events_low_level(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        AddHandler(h, "echo", """{"type":"object","properties":{"text":{"type":"string"}}}""");
        await h.StartAsync(protocolVersion);

        await h.ListToolsAsync();
        await h.CallToolAsync("echo", """{"text":"hi","context":"testing"}""");
        var types = (await h.FlushAsync()).Select(e => (string?)e["eventType"]).ToArray();

        Assert.DoesNotContain("mcp:tools/list", types);
        Assert.DoesNotContain("mcp:initialize", types);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Injects_no_handle_params_into_the_listing(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableTracing = false });
        h.AddTool((string msg) => "echo:" + msg, "echo");
        await h.StartAsync(protocolVersion);

        var props = Props(Find(await h.ListToolsAsync(), "echo"));

        Assert.False(props.TryGetProperty("session_id", out _));
        Assert.False(props.TryGetProperty("agent_id", out _));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Omits_get_more_tools_handle_injection_is_unaffected(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableReportMissing = false });
        h.AddTool((string msg) => "echo:" + msg, "echo");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        Assert.Equal(0, Count(listing, "get_more_tools"));
        var props = Props(Find(listing, "echo"));
        Assert.True(props.TryGetProperty("session_id", out _));
        Assert.True(props.TryGetProperty("context", out _));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Lands_the_custom_text_in_the_advertised_context_param_description(string? protocolVersion)
    {
        const string custom = "Explain your reasoning for this action";
        await using var h = Harness(new AgentCatOptions { EnableToolCallContext = true, CustomContextDescription = custom });
        h.AddTool((string msg) => "echo:" + msg, "echo");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        var context = Props(Find(listing, "echo")).GetProperty("context").GetProperty("description").GetString();
        Assert.Equal(custom, context);
        Assert.NotEqual(DefaultContextDescription, context);
        var gmtContext = Props(Find(listing, "get_more_tools")).GetProperty("context").GetProperty("description").GetString();
        Assert.NotEqual(custom, gmtContext);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Uses_the_default_description_when_customContextDescription_is_not_provided(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableToolCallContext = true });
        h.AddTool((string msg) => "echo:" + msg, "echo");
        await h.StartAsync(protocolVersion);

        var context = Props(Find(await h.ListToolsAsync(), "echo")).GetProperty("context");

        Assert.Equal(DefaultContextDescription, context.GetProperty("description").GetString());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Skips_handle_injection_entirely_on_composed_oneOf_input_schemas(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        AddHandler(h, "composed", """
            {"type":"object","oneOf":[{"type":"object","properties":{"a":{"type":"string"}}},{"type":"object","properties":{"b":{"type":"string"}}}]}
            """);
        await h.StartAsync(protocolVersion);

        var tool = Find(await h.ListToolsAsync(), "composed");

        Assert.True(tool.InputSchema.TryGetProperty("oneOf", out _));
        var serialized = tool.InputSchema.GetRawText();
        Assert.DoesNotContain("session_id", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"context\"", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Builds_a_default_object_schema_for_a_tool_listed_without_one(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        h.AddTool(() => "ok", "bare");
        await h.StartAsync(protocolVersion);

        var tool = Find(await h.ListToolsAsync(), "bare");

        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        var props = Props(tool);
        Assert.Equal(SessionIdPattern, props.GetProperty("session_id").GetProperty("pattern").GetString());
        Assert.True(props.TryGetProperty("agent_id", out _));
        Assert.True(props.TryGetProperty("context", out _));
        Assert.Contains("session_id", Required(tool));
        Assert.Contains("agent_id", Required(tool));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Preserves_a_customer_declared_session_id_agent_id_instead_of_overwriting(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        AddHandler(h, "own_handles", """
            {"type":"object","properties":{"session_id":{"type":"string","description":"customer session id"},"agent_id":{"type":"string","description":"customer agent id"}}}
            """);
        await h.StartAsync(protocolVersion);

        var tool = Find(await h.ListToolsAsync(), "own_handles");

        var props = Props(tool);
        Assert.Equal("customer session id", props.GetProperty("session_id").GetProperty("description").GetString());
        Assert.False(props.GetProperty("session_id").TryGetProperty("pattern", out _));
        Assert.Equal("customer agent id", props.GetProperty("agent_id").GetProperty("description").GetString());
        var required = Required(tool);
        Assert.DoesNotContain("session_id", required);
        Assert.DoesNotContain("agent_id", required);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Drops_additionalProperties_false_so_optional_handles_can_ride_along(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        AddHandler(h, "strict", """{"type":"object","properties":{"text":{"type":"string"}},"additionalProperties":false}""");
        await h.StartAsync(protocolVersion);

        var tool = Find(await h.ListToolsAsync(), "strict");

        Assert.False(tool.InputSchema.TryGetProperty("additionalProperties", out _));
        Assert.True(Props(tool).TryGetProperty("session_id", out _));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Declares_mcp_session_in_a_listed_plain_object_outputSchema_edge(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        AddHandler(h, "stats", extra: """
            "outputSchema":{"type":"object","properties":{"count":{"type":"number"}},"required":["count"],"additionalProperties":false}
            """);
        await h.StartAsync(protocolVersion);

        var stats = Find(await h.ListToolsAsync(), "stats");

        var session = stats.OutputSchema!.Value.GetProperty("properties").GetProperty("mcp_session");
        Assert.Equal("object", session.GetProperty("type").GetString());
        AssertSeq(Keys(session.GetProperty("properties")), "session_id", "agent_id", "status");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Omits_agent_id_from_mcp_session_when_agent_tracking_is_off(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = false });
        AddHandler(h, "stats", extra: """
            "outputSchema":{"type":"object","properties":{"count":{"type":"number"}}}
            """);
        await h.StartAsync(protocolVersion);

        var stats = Find(await h.ListToolsAsync(), "stats");

        var session = stats.OutputSchema!.Value.GetProperty("properties").GetProperty("mcp_session");
        AssertSeq(Keys(session.GetProperty("properties")), "session_id", "status");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Leaves_composed_anyOf_outputSchemas_untouched(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        AddHandler(h, "either", extra: """
            "outputSchema":{"type":"object","anyOf":[{"type":"object","properties":{"ok":{"type":"boolean"}}},{"type":"object","properties":{"err":{"type":"string"}}}]}
            """);
        await h.StartAsync(protocolVersion);

        var tool = Find(await h.ListToolsAsync(), "either");

        var serialized = tool.OutputSchema!.Value.GetRawText();
        Assert.Contains("\"anyOf\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_session", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Never_overwrites_a_customer_declared_mcp_session_output_field(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        AddHandler(h, "claims_key", extra: """
            "outputSchema":{"type":"object","properties":{"mcp_session":{"type":"string","description":"customer-owned field"}}}
            """);
        await h.StartAsync(protocolVersion);

        var tool = Find(await h.ListToolsAsync(), "claims_key");

        var session = tool.OutputSchema!.Value.GetProperty("properties").GetProperty("mcp_session");
        Assert.Equal("string", session.GetProperty("type").GetString());
        Assert.Equal("customer-owned field", session.GetProperty("description").GetString());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Injects_params_get_more_tools_into_tools_list_and_mirrors_outputSchema(string? protocolVersion)
    {
        await using var h = Harness();
        h.AddTool(McpServerTool.Create(
            (string msg) => new Echoed(msg),
            new McpServerToolCreateOptions { Name = "echo", UseStructuredContent = true }));
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        Assert.Equal(1, Count(listing, "get_more_tools"));
        var echo = Find(listing, "echo");
        Assert.True(Props(echo).TryGetProperty("session_id", out _));
        Assert.True(Props(echo).TryGetProperty("context", out _));
        Assert.True(echo.OutputSchema!.Value.GetProperty("properties").TryGetProperty("mcp_session", out _));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Wraps_handlers_registered_via_method_strings_preserves_nextCursor(string? protocolVersion)
    {
        await using var h = Harness();
        AddHandler(h, "echo", """{"type":"object","properties":{"msg":{"type":"string"}},"required":["msg"]}""");
        UseListHandler(h, _ => Mcp.ListResult(
            """{"tools":[{"name":"echo","description":"Echo","inputSchema":{"type":"object","properties":{"msg":{"type":"string"}},"required":["msg"]}}],"nextCursor":"page-2"}"""));
        await h.StartAsync(protocolVersion);

        var page = await h.ListToolsAsync();

        Assert.Equal("page-2", page.NextCursor);
        Assert.Contains("get_more_tools", page.Tools.Select(t => t.Name));
        Assert.True(Props(Find(page, "echo")).TryGetProperty("session_id", out _));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Registries_merge_per_tool_across_options_instances(string? protocolVersion)
    {
        await using var first = Harness();
        AddHandler(first, "alpha", """{"type":"object","properties":{"x":{"type":"string"}}}""");
        await first.StartAsync(protocolVersion);
        var tracker = first.Tracker!;

        await using var second = new McpHarness();
        AddHandler(second, "beta", """{"type":"object","properties":{"y":{"type":"string"}}}""");
        second.Configure(o => Assert.True(EngineInstaller.Install(o, tracker)));
        await second.StartAsync(protocolVersion);

        await first.ListToolsAsync();
        await second.ListToolsAsync();

        var injected = tracker.Registries.InjectedParams;
        AssertSeq(injected["alpha"].OrderBy(s => s, StringComparer.Ordinal).ToArray(), "context", "session_id");
        AssertSeq(injected["beta"].OrderBy(s => s, StringComparer.Ordinal).ToArray(), "context", "session_id");
        Assert.True(injected.ContainsKey("get_more_tools"));

        await using var third = new McpHarness();
        AddHandler(third, "alpha", """{"type":"object","oneOf":[{"type":"object"}]}""");
        third.Configure(o => Assert.True(EngineInstaller.Install(o, tracker)));
        await third.StartAsync(protocolVersion);
        await third.ListToolsAsync();

        var merged = tracker.Registries.InjectedParams;
        Assert.Empty(merged["alpha"]);
        Assert.Equal(2, merged["beta"].Count);
        Assert.True(tracker.Registries.HasListing);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Every_page_gets_get_more_tools(string? protocolVersion)
    {
        await using var h = Harness();
        UseListHandler(h, cursor => cursor is null
            ? Mcp.ListResult("""{"tools":[{"name":"one","inputSchema":{"type":"object"}}],"nextCursor":"2"}""")
            : Mcp.ListResult("""{"tools":[{"name":"two","inputSchema":{"type":"object"}}]}"""));
        await h.StartAsync(protocolVersion);

        var first = await h.ListToolsAsync();
        var second = await h.ListToolsAsync("2");

        Assert.Equal("2", first.NextCursor);
        Assert.Null(second.NextCursor);
        Assert.Equal(1, Count(first, "get_more_tools"));
        Assert.Equal(1, Count(second, "get_more_tools"));
        Assert.True(Props(Find(first, "one")).TryGetProperty("context", out _));
        Assert.True(Props(Find(second, "two")).TryGetProperty("context", out _));
        Assert.True(h.Tracker!.Registries.InjectedParams.ContainsKey("one"));
        Assert.True(h.Tracker.Registries.InjectedParams.ContainsKey("two"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tracing_off_still_injects_context_when_enabled(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableTracing = false, EnableToolCallContext = true });
        h.AddTool((string msg) => "echo:" + msg, "echo");
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        var echo = Find(listing, "echo");
        AssertSeq(Keys(Props(echo)), "msg", "context");
        Assert.Equal(1, Count(listing, "get_more_tools"));
        AssertSeq(h.Tracker!.Registries.InjectedParams["echo"].ToArray(), "context");
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Original_tools_are_never_mutated(string? protocolVersion)
    {
        await using var h = Harness(new AgentCatOptions { EnableAgentTracking = true });
        var attribute = McpServerTool.Create(
            (string msg) => new Echoed(msg),
            new McpServerToolCreateOptions { Name = "attr", UseStructuredContent = true });
        h.AddTool(attribute);
        var protocolTool = attribute.ProtocolTool;
        var protocolToolBefore = Serialize(protocolTool);
        await h.StartAsync(protocolVersion);

        await h.ListToolsAsync();
        await h.ListToolsAsync();

        Assert.Same(protocolTool, attribute.ProtocolTool);
        Assert.Equal(protocolToolBefore, Serialize(protocolTool));
        Assert.DoesNotContain("session_id", protocolToolBefore, StringComparison.Ordinal);

        await using var handlerOnly = Harness(new AgentCatOptions { EnableAgentTracking = true });
        var cached = Mcp.ListResult(
            """{"tools":[{"name":"cached","inputSchema":{"type":"object","properties":{"a":{"type":"string"}}},"outputSchema":{"type":"object","properties":{"n":{"type":"number"}}}}],"nextCursor":"c"}""");
        var cachedTool = cached.Tools[0];
        var cachedBefore = Serialize(cached);
        UseListHandler(handlerOnly, _ => cached);
        await handlerOnly.StartAsync(protocolVersion);

        var listing = await handlerOnly.ListToolsAsync();
        await handlerOnly.ListToolsAsync();

        Assert.True(Props(Find(listing, "cached")).TryGetProperty("session_id", out _));
        Assert.Same(cachedTool, Assert.Single(cached.Tools));
        Assert.Equal(cachedBefore, Serialize(cached));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Customer_owned_get_more_tools_is_shadowed_with_one_warning(string? protocolVersion)
    {
        await using var h = Harness();
        AddHandler(h, "get_more_tools", """{"type":"object","properties":{"q":{"type":"string"}}}""");
        AddHandler(h, "echo", """{"type":"object","properties":{"text":{"type":"string"}}}""");
        UseListHandler(h, _ => Mcp.ListResult(
            """{"tools":[{"name":"get_more_tools","description":"customer version","inputSchema":{"type":"object","properties":{"q":{"type":"string"}}}},{"name":"echo","inputSchema":{"type":"object"}}]}"""));
        await h.StartAsync(protocolVersion);

        var first = await h.ListToolsAsync();
        var second = await h.ListToolsAsync();

        foreach (var listing in new[] { first, second })
        {
            Assert.Equal("customer version", Find(listing, "get_more_tools").Description);
            Assert.Equal(1, Count(listing, "get_more_tools"));
        }

        Assert.Single(h.Log.Lines, l => l.Contains("shadowed"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Listing_preserves_cursor_meta_and_unknown_fields(string? protocolVersion)
    {
        await using var h = Harness();
        var json = McpHarness.McpMajor >= 2
            ? """{"tools":[{"name":"rich","title":"Rich tool","description":"d","inputSchema":{"type":"object","properties":{"a":{"type":"string"}}},"annotations":{"title":"Rich","readOnlyHint":true},"_meta":{"tool":"meta"}}],"nextCursor":"next","_meta":{"list":"meta"},"ttlMs":1234}"""
            : """{"tools":[{"name":"rich","title":"Rich tool","description":"d","inputSchema":{"type":"object","properties":{"a":{"type":"string"}}},"annotations":{"title":"Rich","readOnlyHint":true},"execution":{"taskSupport":"optional"},"_meta":{"tool":"meta"}}],"nextCursor":"next","_meta":{"list":"meta"}}""";
        UseListHandler(h, _ => Mcp.ListResult(json));
        await h.StartAsync(protocolVersion);

        var listing = await h.ListToolsAsync();

        Assert.Equal("next", listing.NextCursor);
        Assert.Equal("meta", (string?)listing.Meta?["list"]);
        var rich = Find(listing, "rich");
        Assert.Equal("Rich tool", rich.Title);
        Assert.Equal("d", rich.Description);
        Assert.Equal("Rich", rich.Annotations?.Title);
        Assert.True(rich.Annotations?.ReadOnlyHint);
        Assert.Equal("meta", (string?)rich.Meta?["tool"]);
        var raw = Serialize(rich);
        if (McpHarness.McpMajor >= 2)
        {
            Assert.Equal(1234, (int?)JsonNode.Parse(Serialize(listing))!["ttlMs"]);
        }
        else
        {
            Assert.Contains("\"execution\"", raw, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Customer_list_handler_exceptions_propagate_unchanged(string? protocolVersion)
    {
        await using var h = Harness();
        h.AddTool((string msg) => "echo:" + msg, "echo");
        h.Configure(o => o.Handlers.ListToolsHandler = (_, _) => throw new McpProtocolException("customer list failure", McpErrorCode.InvalidParams));
        await h.StartAsync(protocolVersion);

        var thrown = await Assert.ThrowsAnyAsync<McpException>(() => h.ListToolsAsync());

        Assert.Contains("customer list failure", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Log.Lines, l => l.StartsWith(InjectionFailedPrefix, StringComparison.Ordinal));
        Assert.False(h.Tracker!.Registries.HasListing);
    }

    private static void AssertGetMoreToolsAnnotations(Tool gmt)
    {
        var annotations = gmt.Annotations!;
        Assert.Equal("Get More Tools", annotations.Title);
        Assert.True(annotations.ReadOnlyHint);
        Assert.False(annotations.DestructiveHint);
        Assert.True(annotations.IdempotentHint);
        Assert.False(annotations.OpenWorldHint);
    }

    private sealed record Stats(int Count);

    private sealed record Echoed(string Msg);
}
