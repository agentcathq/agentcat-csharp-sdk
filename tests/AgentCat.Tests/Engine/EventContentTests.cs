using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Engine;

public class EventContentTests
{
    private const string ToolCall = "mcp:tools/call";

    private const string ClientInfoKey = "io.modelcontextprotocol/clientInfo";

    private const string ProtocolKey = "io.modelcontextprotocol/protocolVersion";

    private const string ProtocolTag = "agentcat_protocol_version";

    private const string ImageMarker = "[image content redacted - not supported by AgentCat]";

    private const string AudioMarker = "[audio content redacted - not supported by AgentCat]";

    private const string BinaryMarker = "[binary data redacted - not supported by AgentCat]";

    private const int MaxText = 32768;

    private static readonly JsonSerializerOptions Reflection = new();

    private static readonly string[] ExtraKeys = { "sessionId", "requestId", "_meta" };

    private static readonly string[] Intents = { "first reason", "second reason", "third reason" };

    public static TheoryData<string?> Protocols => McpHarness.ProtocolVersions;

    private static string Quote(string text) => JsonSerializer.Serialize(text, Reflection);

    private static JsonObject CallEvent(IReadOnlyList<JsonObject> events) => Assert.Single(events, e => (string?)e["eventType"] == ToolCall);

    private static JsonObject Extra(JsonObject e) => Assert.IsType<JsonObject>(e["parameters"]?["extra"]);

    private static JsonNode? RawParams(JsonObject e) => e["parameters"]?["request"]?["params"];

    private static string Json(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    private static string? Tag(JsonObject e, string key) => (string?)e["tags"]?[key];

    private static string SessionBlockText(CallToolResult result)
        => result.Content.OfType<TextContentBlock>().First(b => b.Text.StartsWith("[session_id", StringComparison.Ordinal)).Text;

    private static string[] OriginalTexts(CallToolResult result)
        => result.Content.OfType<TextContentBlock>().Where(b => !b.Text.StartsWith("[session_id", StringComparison.Ordinal)).Select(b => b.Text).ToArray();

    private static int Utf8(string text) => Encoding.UTF8.GetByteCount(text);

    private static int Tokens(int bytes) => (int)Math.Ceiling(bytes / 3.5);

    private static ValueTask<CallToolResult> Reply(string json) => new(Mcp.Result(json));

    private static string Handlers(string name) => "{\"name\":\"" + name + "\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}}";

    private static EngineHarness WithTool(string name, string resultJson, Action<AgentCatOptions>? configure = null)
    {
        var options = new AgentCatOptions { EnableToolCallContext = false };
        configure?.Invoke(options);
        var h = new EngineHarness(options: options);
        h.AddHandlerTool(Handlers(name), (_, _) => Reply(resultJson));
        return h;
    }

    private static EngineHarness Todos(Action<AgentCatOptions>? configure = null)
    {
        var options = new AgentCatOptions();
        configure?.Invoke(options);
        var h = new EngineHarness(options: options);
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
        return h;
    }

    private static McpRequestFilter<CallToolRequestParams, CallToolResult> Around(
        Func<RequestContext<CallToolRequestParams>, McpRequestHandler<CallToolRequestParams, CallToolResult>, CancellationToken, ValueTask<CallToolResult>> filter)
        => next => (context, cancellationToken) => filter(context, next, cancellationToken);

#pragma warning disable MCP9003
    private static RequestContext<CallToolRequestParams> Context(McpServer server, string paramsJson, string id = "7")
    {
        var request = (JsonRpcRequest)JsonSerializer.Deserialize(
            "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":" + paramsJson + "}",
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage)))!;
        return new RequestContext<CallToolRequestParams>(server, request) { Params = Mcp.CallToolParams(paramsJson) };
    }
#pragma warning restore MCP9003

    private static string WithMeta(string metaJson) => "{\"name\":\"echo\",\"arguments\":{},\"_meta\":" + metaJson + "}";

    private static async Task<EngineHarness> Started(string? protocolVersion)
    {
        var h = new EngineHarness();
        h.AddHandlerTool(Handlers("echo"), (_, _) => Reply("""{"content":[{"type":"text","text":"ok"}]}"""));
        await h.StartAsync(protocolVersion);
        return h;
    }

    // client-identity.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Prefers_per_request__meta_clientInfo_modern_client(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);
        var context = Context(h.Server, WithMeta("{\"" + ClientInfoKey + "\":{\"name\":\"modern\",\"version\":\"9.1\"}}"));

        var (name, version) = ClientIdentity.For(context);

        Assert.Equal("modern", name);
        Assert.Equal("9.1", version);
        Assert.NotEqual("modern", h.Server.ClientInfo?.Name);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Falls_back_to_getClientVersion_legacy_initialize_capture(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);

        var (name, version) = ClientIdentity.For(Context(h.Server, "{\"name\":\"echo\",\"arguments\":{}}"));

        Assert.Equal("harness-client", name);
        Assert.Equal("3.1.4", version);
    }

    [Fact]
    public async Task Both_missing_undefined_client_declined_to_identify()
    {
        await using var server = McpServer.Create(new StreamServerTransport(new MemoryStream(), new MemoryStream()), new McpServerOptions());
        Assert.Null(server.ClientInfo);

        var (name, version) = ClientIdentity.For(Context(server, "{\"name\":\"echo\",\"arguments\":{}}"));

        Assert.Null(name);
        Assert.Null(version);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Ignores_malformed__meta_clientInfo(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);
        foreach (var malformed in new[] { "\"nope\"", "42", "[\"a\",\"b\"]", "null", "true", "{}", "{\"other\":\"x\"}" })
        {
            var (name, version) = ClientIdentity.For(Context(h.Server, WithMeta("{\"" + ClientInfoKey + "\":" + malformed + "}")));

            Assert.Equal("harness-client", name);
            Assert.Equal("3.1.4", version);
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Drops_non_string_fields_from_partially_malformed__meta_clientInfo(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);

        Assert.Equal((null, "1.0"), ClientIdentity.For(Context(h.Server, WithMeta("{\"" + ClientInfoKey + "\":{\"name\":5,\"version\":\"1.0\"}}"))));
        Assert.Equal(("modern", null), ClientIdentity.For(Context(h.Server, WithMeta("{\"" + ClientInfoKey + "\":{\"name\":\"modern\",\"version\":{\"v\":1}}}"))));
        Assert.Equal(("only-name", null), ClientIdentity.For(Context(h.Server, WithMeta("{\"" + ClientInfoKey + "\":{\"name\":\"only-name\"}}"))));
        Assert.Equal(("harness-client", "3.1.4"), ClientIdentity.For(Context(h.Server, WithMeta("{\"" + ClientInfoKey + "\":{\"name\":5,\"version\":false}}"))));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Reads__meta_protocolVersion(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);

        Assert.Equal("2026-07-28", ClientIdentity.ProtocolVersion(Context(h.Server, WithMeta("{\"" + ProtocolKey + "\":\"2026-07-28\"}"))));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Absent_undefined(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);

        Assert.Null(ClientIdentity.ProtocolVersion(Context(h.Server, "{\"name\":\"echo\",\"arguments\":{}}")));
        Assert.Null(ClientIdentity.ProtocolVersion(Context(h.Server, WithMeta("{}"))));
        Assert.Null(ClientIdentity.ProtocolVersion(Context(h.Server, WithMeta("{\"" + ProtocolKey + "\":\"\"}"))));
        Assert.Null(ClientIdentity.ProtocolVersion(Context(h.Server, WithMeta("{\"" + ProtocolKey + "\":20251125}"))));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Prefers_ctx_mcpReq_envelope_clientInfo_over__meta_and_handshake(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);
        var session = h.Server.ClientInfo!;

        var (name, version) = ClientIdentity.For(Context(h.Server, WithMeta("{\"" + ClientInfoKey + "\":{\"name\":\"from-meta\",\"version\":\"2.0\"}}")));

        Assert.Equal("from-meta", name);
        Assert.Equal("2.0", version);
        Assert.NotEqual(session.Name, name);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Falls_back_to__meta_v1_passthrough_then_getClientVersion(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);

        Assert.Equal(("from-meta", "2.0"), ClientIdentity.For(Context(h.Server, WithMeta("{\"" + ClientInfoKey + "\":{\"name\":\"from-meta\",\"version\":\"2.0\"}}"))));
        Assert.Equal(("harness-client", "3.1.4"), ClientIdentity.For(Context(h.Server, WithMeta("{}"))));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Rejects_non_string_envelope_fields_field_level_narrowing(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);

        Assert.Equal((null, null), ClientIdentity.For(Context(Bare(), WithMeta("{\"" + ClientInfoKey + "\":{\"name\":[1],\"version\":2}}"))));
        Assert.Equal(("n", null), ClientIdentity.For(Context(h.Server, WithMeta("{\"" + ClientInfoKey + "\":{\"name\":\"n\",\"version\":null}}"))));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task GetProtocolVersion_prefers_the_envelope_then__meta(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);

        Assert.Equal("2026-07-28", ClientIdentity.ProtocolVersion(Context(h.Server, WithMeta("{\"" + ProtocolKey + "\":\"2026-07-28\"}"))));
        Assert.Null(ClientIdentity.ProtocolVersion(Context(h.Server, WithMeta("{}"))));
    }

    private static McpServer Bare() => McpServer.Create(new StreamServerTransport(new MemoryStream(), new MemoryStream()), new McpServerOptions());

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Client_info_prefers_meta_over_the_session(string? protocolVersion)
    {
        var seen = new List<(string? Meta, string? Session)>();
        await using var h = Capturing(seen);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();
        var negotiated = h.Client.NegotiatedProtocolVersion;
        var custom = McpHarness.McpMajor == 1;
        var meta = custom
            ? "{\"" + ClientInfoKey + "\":{\"name\":\"meta-client\",\"version\":\"7.0\"},\"" + ProtocolKey + "\":" + Quote(negotiated!) + "}"
            : null;

        await h.CallToolParamsAsync("{\"name\":\"echo\",\"arguments\":{}" + (meta is null ? "" : ",\"_meta\":" + meta) + "}");

        var e = CallEvent(await h.FlushAsync());
        var received = JsonNode.Parse(Assert.Single(seen).Meta ?? "null") as JsonObject;
        var fromMeta = received?[ClientInfoKey] as JsonObject;
        var expectedName = (string?)fromMeta?["name"] ?? "harness-client";
        var expectedVersion = (string?)fromMeta?["version"] ?? "3.1.4";
        Assert.Equal(expectedName, (string?)e["clientName"]);
        Assert.Equal(expectedVersion, (string?)e["clientVersion"]);
        if (custom)
        {
            Assert.Equal("meta-client", (string?)e["clientName"]);
            Assert.Equal("harness-client", seen[0].Session);
        }
        else if (negotiated != McpHarness.LegacyProtocol)
        {
            Assert.NotNull(fromMeta);
        }
        else
        {
            Assert.Null(fromMeta);
            Assert.Equal("harness-client", (string?)e["clientName"]);
        }
    }

    private static EngineHarness Capturing(List<(string? Meta, string? Session)> seen)
    {
        var h = new EngineHarness();
        h.AddHandlerTool(Handlers("echo"), (_, _) => Reply("""{"content":[{"type":"text","text":"ok"}]}"""));
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around((context, next, ct) =>
        {
            lock (seen)
            {
                seen.Add((context.JsonRpcRequest.Params?["_meta"]?.ToJsonString(), context.Server.ClientInfo?.Name));
            }

            return next(context, ct);
        })));
        return h;
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Stamps_the_connecting_clients_own_identity_onto_captured_events(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);

        await h.CallToolAsync("echo", "{}");

        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("harness-client", (string?)e["clientName"]);
        Assert.Equal("3.1.4", (string?)e["clientVersion"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Stamps_the_server_identity_from_the_options(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddHandlerTool(Handlers("echo"), (_, _) => Reply("""{"content":[]}"""));
        h.Configure(o => o.ServerInfo = (Implementation)JsonSerializer.Deserialize(
            "{\"name\":\"acme-server\",\"version\":\"4.2.0\"}",
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Implementation)))!);
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("echo", "{}");

        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("acme-server", (string?)e["serverName"]);
        Assert.Equal("4.2.0", (string?)e["serverVersion"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Stamps_the_default_server_identity_when_none_is_configured(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);

        await h.CallToolAsync("echo", "{}");

        var e = CallEvent(await h.FlushAsync());
        Assert.False(string.IsNullOrEmpty((string?)e["serverName"]));
        Assert.False(string.IsNullOrEmpty((string?)e["serverVersion"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Stamps_meta_client_info_and_the_protocol_tag_on_the_event(string? protocolVersion)
    {
        var seen = new List<(string? Meta, string? Session)>();
        await using var h = Capturing(seen);
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();
        var negotiated = h.Client.NegotiatedProtocolVersion!;
        var custom = McpHarness.McpMajor == 1 || negotiated != McpHarness.LegacyProtocol;
        var meta = new JsonObject { ["app/trace"] = "t1" };
        if (McpHarness.McpMajor == 1)
        {
            meta[ClientInfoKey] = new JsonObject { ["name"] = "modern-client", ["version"] = "2.0" };
            meta[ProtocolKey] = negotiated;
        }

        await h.CallToolParamsAsync("{\"name\":\"echo\",\"arguments\":{},\"_meta\":" + meta.ToJsonString() + "}");

        var e = CallEvent(await h.FlushAsync());
        var received = (JsonObject)JsonNode.Parse(Assert.Single(seen).Meta!)!;
        var expectedProtocol = (string?)received[ProtocolKey];
        Assert.Equal(expectedProtocol, Tag(e, ProtocolTag));
        if (McpHarness.McpMajor == 1)
        {
            Assert.Equal("modern-client", (string?)e["clientName"]);
            Assert.Equal("2.0", (string?)e["clientVersion"]);
            Assert.Equal(negotiated, Tag(e, ProtocolTag));
        }
        else if (custom)
        {
            Assert.Equal(negotiated, Tag(e, ProtocolTag));
            Assert.Equal((string?)received[ClientInfoKey]?["name"], (string?)e["clientName"]);
        }
        else
        {
            Assert.Null(Tag(e, ProtocolTag));
            Assert.Equal("harness-client", (string?)e["clientName"]);
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Protocol_tag_is_absent_when_the_request_carries_no_protocol_version(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolParamsAsync("{\"name\":\"echo\",\"arguments\":{}}");

        var e = CallEvent(await h.FlushAsync());
        var sent = (string?)RawParams(e)?["_meta"]?[ProtocolKey];
        Assert.Equal(sent, Tag(e, ProtocolTag));
        if (McpHarness.McpMajor == 1)
        {
            Assert.Null(Tag(e, ProtocolTag));
        }
    }

    // Extra projection

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Stdio_extra_has_request_id_and_no_request_info(string? protocolVersion)
    {
        var ids = new List<string>();
        await using var h = new EngineHarness();
        h.AddHandlerTool(Handlers("echo"), (_, _) => Reply("""{"content":[]}"""));
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around((context, next, ct) =>
        {
            lock (ids)
            {
                ids.Add(context.JsonRpcRequest.Id.ToString());
            }

            return next(context, ct);
        })));
        await h.StartAsync(protocolVersion);

        await h.CallToolParamsAsync("{\"name\":\"echo\",\"arguments\":{},\"_meta\":{\"app/trace\":\"t1\"}}");

        var extra = Extra(CallEvent(await h.FlushAsync()));
        Assert.Equal(JsonValueKind.Number, extra["requestId"]!.GetValueKind());
        Assert.Equal(Assert.Single(ids), extra["requestId"]!.ToJsonString());
        Assert.False(extra.ContainsKey("sessionId"));
        Assert.False(extra.ContainsKey("requestInfo"));
        Assert.False(extra.ContainsKey("http"));
        Assert.Equal("t1", (string?)extra["_meta"]?["app/trace"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Leaves_stdio_shaped_extra_no_http_unchanged(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);

        await h.CallToolAsync("echo", "{}");

        var e = CallEvent(await h.FlushAsync());
        var extra = Extra(e);
        Assert.All(extra.Select(p => p.Key), key => Assert.Contains(key, ExtraKeys));
        Assert.False(extra.ContainsKey("requestInfo"));
        Assert.Equal(Json(RawParams(e)?["_meta"]), Json(extra["_meta"]));
    }

    [Fact]
    public async Task Extra_projection_writes_string_ids_and_copies_the_meta()
    {
        await using var server = Bare();
        var context = Context(server, WithMeta("{\"k\":{\"deep\":[1,2]}}"), "\"req-1\"");

        var extra = ExtraProjection.Project(context);

        Assert.Equal("""{"requestId":"req-1","_meta":{"k":{"deep":[1,2]}}}""", extra.ToJsonString());
        Assert.NotSame(context.Params!.Meta, extra["_meta"]);
        Assert.Equal("""{"k":{"deep":[1,2]}}""", context.Params.Meta!.ToJsonString());
    }

    [Fact]
    public async Task Extra_projection_omits_the_meta_when_the_request_has_none()
    {
        await using var server = Bare();

        var extra = ExtraProjection.Project(Context(server, "{\"name\":\"echo\",\"arguments\":{}}", "12"));

        Assert.Equal("""{"requestId":12}""", extra.ToJsonString());
    }

    // Odd results

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Odd_content_blocks_and_meta_survive_the_event(string? protocolVersion)
    {
        await using var h = WithTool("odd", """{"isError":true,"content":[{"type":"text","text":"first"},null,{"type":"text","text":"second"}],"_meta":{"trace":"t-9","n":[1,{"x":null}]}}""");
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("odd", "{}");

        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(
            """{"content":[{"type":"text","text":"first"},null,{"type":"text","text":"second"}],"isError":true,"_meta":{"trace":"t-9","n":[1,{"x":null}]}}""",
            Json(e["response"]));
        Assert.Equal("first second", (string?)e["error"]?["message"]);
    }

    // Independent event fields

    private static JsonObject Deep(int depth)
    {
        var node = new JsonObject();
        var root = node;
        for (var i = 0; i < depth; i++)
        {
            var child = new JsonObject();
            node["d"] = child;
            node = child;
        }

        return root;
    }

    private static ToolCallEvent Direct(EngineHarness h, JsonNode? rawParams)
    {
        var start = h.Runtime.Clock.UtcNow;
        var resolution = AgentCat.Internal.Handles.HandleResolver.Resolve(null, false, false, true, start);
        return new ToolCallEvent(
            rawParams,
            "direct",
            start,
            resolution,
            new Dictionary<string, string>(),
            null,
            new Dictionary<string, JsonElement> { ["text"] = JsonDocument.Parse("\"hi there\"").RootElement },
            new ExtraSnapshot(null, new RequestId(5), null),
            ServerIdentity.Of(h.ServerOptions),
            ("c", "1"),
            null);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_failure_building_Parameters_keeps_the_Response_and_the_other_fields(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);
        var call = Direct(h, new JsonObject { ["name"] = "direct", ["arguments"] = Deep(1_500) });

        call.Publish(h.Tracker!, Mcp.Result("""{"isError":true,"content":[{"type":"text","text":"kept"}]}"""));

        var e = CallEvent(await h.FlushAsync());
        Assert.False(e.ContainsKey("parameters"));
        Assert.Equal("""{"content":[{"type":"text","text":"kept"}],"isError":true}""", Json(e["response"]));
        Assert.Equal("kept", (string?)e["error"]?["message"]);
        Assert.NotNull(e["outputTokens"]);
        Assert.Equal(6, (int?)e["inputTokens"]);
        Assert.Equal("c", (string?)e["clientName"]);
        Assert.Contains(h.Log.Lines, l => l.StartsWith("Failed to resolve pending hook results: ", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_failure_building_Response_keeps_the_Parameters_and_the_other_fields(string? protocolVersion)
    {
        await using var h = await Started(protocolVersion);
        var call = Direct(h, new JsonObject { ["name"] = "direct", ["arguments"] = new JsonObject { ["text"] = "hi there" } });
        var nested = new StringBuilder();
        nested.Append('[', 1_500).Append('1').Append(']', 1_500);
        var result = Mcp.Result("{\"content\":[],\"structuredContent\":{\"v\":1}}");
        result.StructuredContent = JsonDocument.Parse(nested.ToString(), new JsonDocumentOptions { MaxDepth = 4_000 }).RootElement;

        call.Publish(h.Tracker!, result);

        var e = CallEvent(await h.FlushAsync());
        Assert.False(e.ContainsKey("response"));
        Assert.Equal("direct", (string?)RawParams(e)?["name"]);
        Assert.Equal("hi there", (string?)RawParams(e)?["arguments"]?["text"]);
        Assert.Equal("""{"requestId":5}""", Json(Extra(e)));
        Assert.Equal(6, (int?)e["inputTokens"]);
        Assert.Contains(h.Log.Lines, l => l.StartsWith("Failed to resolve pending hook results: ", StringComparison.Ordinal));
    }

    // error-capture.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_successful_tool_calls_without_errors(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"buy milk"}""");

        var e = CallEvent(await h.FlushAsync());
        Assert.False(e.ContainsKey("isError"));
        Assert.False(e.ContainsKey("error"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_publish_exactly_one_event_per_tool_call(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"one"}""");
        await h.CallToolAsync("add_todo", """{"text":"two"}""");

        var events = await h.FlushAsync();
        Assert.Equal(2, events.Count(e => (string?)e["eventType"] == ToolCall));
        Assert.Equal(2, events.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task An_isError_result_publishes_the_captured_error_with_the_result_text(string? protocolVersion)
    {
        await using var h = WithTool("fail", """{"isError":true,"content":[{"type":"text","text":"quota exceeded"},{"type":"text","text":"retry later"}]}""");
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("fail", "{}");

        Assert.Contains("[session_id issued", SessionBlockText(result), StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(true, (bool?)e["isError"]);
        var error = Assert.IsType<JsonObject>(e["error"]);
        Assert.Equal("quota exceeded retry later", (string?)error["message"]);
        Assert.DoesNotContain("[session_id", error.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Error_computation_failure_leaves_the_other_fields(string? protocolVersion)
    {
        await using var h = WithTool("fail", """{"isError":true,"content":[{"type":"text","text":"bad"}]}""");
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("fail", """{"text":"x"}""");

        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("bad", (string?)e["error"]?["message"]);
        Assert.Equal("""{"text":"x"}""", Json(RawParams(e)?["arguments"]));
        Assert.NotNull(e["response"]);
        Assert.NotNull(e["inputTokens"]);
        Assert.NotNull(e["outputTokens"]);
    }

    // event-tags-properties.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Publishes_no_initialize_event_even_when_tag_property_callbacks_are_set(string? protocolVersion)
    {
        await using var h = Todos(o =>
        {
            o.EventTags = (_, _) => new ValueTask<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["k"] = "v" });
            o.EventProperties = (_, _) => new ValueTask<JsonObject?>(new JsonObject { ["p"] = 1 });
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_todo", """{"text":"x"}""");

        var events = await h.FlushAsync();
        Assert.All(events, e => Assert.Equal(ToolCall, (string?)e["eventType"]));
        Assert.Single(events);
    }

    // token-estimates.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Counts_the_raw_arguments_and_the_content_text(string? protocolVersion)
    {
        await using var h = WithTool(
            "token_probe",
            "{\"content\":[{\"type\":\"text\",\"text\":\"hello world\"}],\"structuredContent\":{\"padding\":\"" + new string('y', 1000) + "\"}}");
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("token_probe", """{"text":"hi there"}""");

        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(6, (int?)e["inputTokens"]);
        Assert.Equal(4, (int?)e["outputTokens"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Counts_the_structured_content_when_the_content_list_is_empty(string? protocolVersion)
    {
        await using var h = WithTool("structured_only", """{"content":[],"structuredContent":{"result":"ok"}}""");
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("structured_only", """{"text":"hi there"}""");

        Assert.Equal(5, (int?)CallEvent(await h.FlushAsync())["outputTokens"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Counts_an_error_results_content_including_a_throw_the_SDK_turns_into_one(string? protocolVersion)
    {
        await using var h = WithTool("error_result", """{"isError":true,"content":[{"type":"text","text":"boom!"}]}""");
        h.AddHandlerTool(Handlers("throws"), (_, _) => throw new InvalidOperationException("no"));
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("error_result", """{"text":"hi there"}""");
        var thrownResult = await h.CallToolAsync("throws", """{"text":"hi there"}""");

        Assert.True(thrownResult.IsError);
        var events = await h.FlushAsync();
        var errorResult = Assert.Single(events, e => (string?)e["resourceName"] == "error_result");
        Assert.Equal(true, (bool?)errorResult["isError"]);
        Assert.Equal(6, (int?)errorResult["inputTokens"]);
        Assert.Equal(2, (int?)errorResult["outputTokens"]);
        var thrown = Assert.Single(events, e => (string?)e["resourceName"] == "throws");
        Assert.Equal(true, (bool?)thrown["isError"]);
        Assert.Equal(6, (int?)thrown["inputTokens"]);
        Assert.Equal(Tokens(Utf8("An error occurred invoking 'throws'.")), (int?)thrown["outputTokens"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Keeps_the_raw_counts_when_redaction_rewrites_the_payloads(string? protocolVersion)
    {
        await using var h = new EngineHarness(options: new AgentCatOptions
        {
            EnableToolCallContext = false,
            RedactSensitiveInformation = (text, _) => new ValueTask<string>(text.Contains("secret") ? "[REDACTED]" : text),
        });
        h.AddTool((string text) => text, "secret_echo", "echoes");
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("secret_echo", """{"text":"secret-value-123456789"}""");

        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("[REDACTED]", (string?)e["response"]?["content"]?[0]?["text"]);
        Assert.Equal("[REDACTED]", (string?)RawParams(e)?["arguments"]?["text"]);
        Assert.Equal(10, (int?)e["inputTokens"]);
        Assert.Equal(7, (int?)e["outputTokens"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Keeps_the_raw_counts_when_truncation_shortens_the_response(string? protocolVersion)
    {
        await using var h = WithTool("big", "{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('x', 40_000) + "\"}]}");
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("big", """{"text":"hi there"}""");

        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(MaxText + 3, ((string)e["response"]!["content"]![0]!["text"]!).Length);
        Assert.Equal(11429, (int?)e["outputTokens"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_call_without_arguments_has_no_input_estimate(string? protocolVersion)
    {
        await using var h = WithTool("noargs", """{"content":[{"type":"text","text":"ok"}]}""");
        await h.StartAsync(protocolVersion);

        await h.CallToolParamsAsync("""{"name":"noargs"}""");

        var e = CallEvent(await h.FlushAsync());
        Assert.False(e.ContainsKey("inputTokens"));
        Assert.Equal(1, (int?)e["outputTokens"]);
    }

    // e2e-sanitization.test.ts and v2/pipeline.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_sanitize_image_content_blocks_in_tool_responses(string? protocolVersion)
    {
        await using var h = WithTool("get_image", """{"content":[{"type":"text","text":"Here is the image"},{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}]}""");
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("get_image", "{}");

        Assert.Contains(result.Content, c => c is ImageContentBlock);
        var content = Assert.IsType<JsonArray>(CallEvent(await h.FlushAsync())["response"]?["content"]);
        Assert.Equal(2, content.Count);
        Assert.Equal("""{"type":"text","text":"Here is the image"}""", Json(content[0]));
        Assert.Equal("{\"type\":\"text\",\"text\":\"" + ImageMarker + "\"}", Json(content[1]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_sanitize_audio_content_blocks_in_tool_responses(string? protocolVersion)
    {
        await using var h = WithTool("get_audio", """{"content":[{"type":"audio","data":"UklGRg==","mimeType":"audio/wav"},{"type":"text","text":"transcript"}]}""");
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("get_audio", "{}");

        Assert.Contains(result.Content, c => c is AudioContentBlock);
        var content = Assert.IsType<JsonArray>(CallEvent(await h.FlushAsync())["response"]?["content"]);
        Assert.Equal("{\"type\":\"text\",\"text\":\"" + AudioMarker + "\"}", Json(content[0]));
        Assert.Equal("""{"type":"text","text":"transcript"}""", Json(content[1]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_sanitize_large_base64_strings_in_tool_call_parameters(string? protocolVersion)
    {
        await using var h = WithTool("upload_file", """{"content":[{"type":"text","text":"uploaded"}]}""");
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("upload_file", "{\"filename\":\"photo.png\",\"data\":\"" + new string('A', 12_000) + "=\"}");

        var arguments = CallEvent(await h.FlushAsync())["parameters"]?["request"]?["params"]?["arguments"];
        Assert.Equal(BinaryMarker, (string?)arguments?["data"]);
        Assert.Equal("photo.png", (string?)arguments?["filename"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_preserve_normal_todo_tool_calls_while_sanitizing_problematic_ones(string? protocolVersion)
    {
        await using var h = Todos(o => o.EnableToolCallContext = false);
        h.AddHandlerTool(Handlers("screenshot"), (_, _) => Reply("""{"content":[{"type":"image","data":"abc","mimeType":"image/png"},{"type":"text","text":"shot"}]}"""));
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"buy milk"}""");
        await h.CallToolAsync("screenshot", "{}");

        var events = await h.FlushAsync();
        var todo = Assert.Single(events, e => (string?)e["resourceName"] == "add_todo");
        Assert.Equal("text", (string?)todo["response"]?["content"]?[0]?["type"]);
        Assert.Contains("Added todo", (string?)todo["response"]?["content"]?[0]?["text"], StringComparison.Ordinal);
        var shot = Assert.Single(events, e => (string?)e["resourceName"] == "screenshot");
        Assert.Equal(ImageMarker, (string?)shot["response"]?["content"]?[0]?["text"]);
        Assert.Equal("shot", (string?)shot["response"]?["content"]?[1]?["text"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Sanitizes_image_content_blocks_in_v2_tool_responses(string? protocolVersion)
    {
        await using var h = WithTool("get_image", """{"content":[{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}]}""");
        await h.StartAsync(protocolVersion, withServices: true);

        await h.CallToolAsync("get_image", "{}");

        Assert.Equal(ImageMarker, (string?)CallEvent(await h.FlushAsync())["response"]?["content"]?[0]?["text"]);
    }

    // e2e-truncation.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_truncate_tool_responses_with_text_exceeding_32KB(string? protocolVersion)
    {
        await using var h = WithTool("get_large_report", "{\"content\":[{\"type\":\"text\",\"text\":\"Report: " + new string('x', 50_000) + "\"}]}");
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("get_large_report", """{"topic":"quarterly sales"}""");

        Assert.Equal(50_008, OriginalTexts(result)[0].Length);
        var text = (string)CallEvent(await h.FlushAsync())["response"]!["content"]![0]!["text"]!;
        Assert.True(text.Length <= MaxText + 3);
        Assert.EndsWith("...", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_keep_total_event_size_under_100KB_for_oversized_parameters(string? protocolVersion)
    {
        await using var h = WithTool("analyze", """{"content":[{"type":"text","text":"done"}]}""");
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("analyze", "{\"dataset\":\"" + new string('d', 80_000) + "\",\"metadata\":\"" + new string('m', 40_000) + "\"}");

        var e = CallEvent(await h.FlushAsync());
        Assert.True(Utf8(e.ToJsonString()) <= 102_400);
        Assert.NotNull(RawParams(e)?["arguments"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_preserve_normal_todo_responses_while_truncating_large_ones(string? protocolVersion)
    {
        var log = "{\"content\":[{\"type\":\"text\",\"text\":\"LOG: " + string.Concat(Enumerable.Repeat("entry ", 10_000)) + "\"}]}";
        await using var h = Todos(o => o.EnableToolCallContext = false);
        h.AddHandlerTool(Handlers("verbose_log"), (_, _) => Reply(log));
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("add_todo", """{"text":"buy milk"}""");
        await h.CallToolAsync("verbose_log", "{}");

        var events = await h.FlushAsync();
        var todo = Assert.Single(events, e => (string?)e["resourceName"] == "add_todo");
        Assert.Contains("Added todo", (string?)todo["response"]?["content"]?[0]?["text"], StringComparison.Ordinal);
        var verbose = Assert.Single(events, e => (string?)e["resourceName"] == "verbose_log");
        Assert.True(((string)verbose["response"]!["content"]![0]!["text"]!).Length <= MaxText + 3);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_handle_both_sanitization_and_truncation_in_the_same_tool_call(string? protocolVersion)
    {
        await using var h = WithTool("mixed", "{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('x', 50_000) + "\"},{\"type\":\"image\",\"data\":\"abc\",\"mimeType\":\"image/png\"}]}");
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("mixed", "{}");

        var e = CallEvent(await h.FlushAsync());
        var content = Assert.IsType<JsonArray>(e["response"]?["content"]);
        Assert.Equal(2, content.Count);
        var text = (string)content[0]!["text"]!;
        Assert.True(text.Length <= MaxText + 3);
        Assert.EndsWith("...", text, StringComparison.Ordinal);
        Assert.Equal("{\"type\":\"text\",\"text\":\"" + ImageMarker + "\"}", Json(content[1]));
        Assert.True(Utf8(e.ToJsonString()) <= 102_400);
    }

    // context-parameters.test.ts, custom-context-description.test.ts, v2/options.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_capture_context_parameter_when_tools_are_called_after_tracking(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_todo", """{"text":"Buy groceries","context":"Planning the weekly shopping"}""");

        Assert.Contains("Added todo", OriginalTexts(result)[0], StringComparison.Ordinal);
        Assert.Contains("Added todo", Assert.IsType<TextContentBlock>(result.Content[1]).Text, StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("Planning the weekly shopping", (string?)e["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_work_with_tools_that_have_context_parameter(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddHandlerTool(
            """{"name":"own_context","inputSchema":{"type":"object","properties":{"text":{"type":"string"},"context":{"type":"string","description":"customer owned"}}}}""",
            (_, _) => Reply("""{"content":[{"type":"text","text":"ok"}]}"""));
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("own_context", """{"text":"x","context":"explain why"}""");

        Assert.Equal("explain why", (string?)CallEvent(await h.FlushAsync())["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_capture_userIntent_only_when_context_is_provided(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_todo", """{"text":"no context"}""");
        await h.FlushAsync();
        await h.CallToolAsync("add_todo", """{"text":"with context","context":"because"}""");

        var events = await h.FlushAsync();
        Assert.False(events[0].ContainsKey("userIntent"));
        Assert.Equal("because", (string?)events[1]["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_capture_tool_calls_with_custom_description_configured(string? protocolVersion)
    {
        await using var h = Todos(o => o.CustomContextDescription = "Explain the business reason for this call");
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_todo", """{"text":"x","context":"quarterly planning"}""");

        Assert.Equal("quarterly planning", (string?)CallEvent(await h.FlushAsync())["userIntent"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_work_end_to_end_with_custom_description_and_multiple_tool_calls(string? protocolVersion)
    {
        await using var h = Todos(o => o.CustomContextDescription = "Why is the agent calling this?");
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_todo", """{"text":"a","context":"first reason"}""");
        await h.FlushAsync();
        await h.CallToolAsync("add_todo", """{"text":"b","context":"second reason"}""");
        await h.FlushAsync();
        await h.CallToolAsync("add_todo", """{"text":"c","context":"third reason"}""");

        var events = await h.FlushAsync();
        Assert.Equal(Intents, events.Select(e => (string?)e["userIntent"]).ToArray());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Captures_userIntent_from_calls_made_with_the_custom_description_configured(string? protocolVersion)
    {
        await using var h = Todos(o => o.CustomContextDescription = "Custom words");
        await h.StartAsync(protocolVersion, withServices: true);
        await h.ListToolsAsync();

        await h.CallToolAsync("add_todo", """{"text":"x","context":"custom intent"}""");

        Assert.Equal("custom intent", (string?)CallEvent(await h.FlushAsync())["userIntent"]);
    }

    // v2/session-modes.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tags_a_supplied_session_id_as_supplied_and_reuses_it_verbatim_no_mint_back(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();
        const string id = "ses_abcdefghijklmnopqrstuvwxyz0";

        var result = await h.CallToolAsync("add_todo", "{\"text\":\"x\",\"context\":\"supplied\",\"session_id\":\"" + id + "\"}");

        Assert.DoesNotContain(result.Content.OfType<TextContentBlock>(), b => b.Text.StartsWith("[session_id", StringComparison.Ordinal));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(id, (string?)e["sessionId"]);
        Assert.Equal("supplied", Tag(e, "agentcat_session_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Mints_on_omission_announces_the_id_and_tags_the_source_as_minted(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("add_todo", """{"text":"x","context":"omitted"}""");

        var block = SessionBlockText(result);
        var minted = System.Text.RegularExpressions.Regex.Match(block, "session_id: (\\S+)").Groups[1].Value;
        Assert.StartsWith("ses_", minted, StringComparison.Ordinal);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(minted, (string?)e["sessionId"]);
        Assert.Equal("minted", Tag(e, "agentcat_session_id_source"));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Start_begins_a_new_task_exactly_like_omission_case_insensitively(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var first = await h.CallToolAsync("add_todo", """{"text":"one","context":"start task","session_id":"start"}""");
        await h.FlushAsync();
        var second = await h.CallToolAsync("add_todo", """{"text":"two","context":"start task","session_id":" START "}""");

        var firstId = System.Text.RegularExpressions.Regex.Match(SessionBlockText(first), "session_id: (\\S+)").Groups[1].Value;
        var secondId = System.Text.RegularExpressions.Regex.Match(SessionBlockText(second), "session_id: (\\S+)").Groups[1].Value;
        Assert.StartsWith("ses_", firstId, StringComparison.Ordinal);
        Assert.StartsWith("ses_", secondId, StringComparison.Ordinal);
        Assert.NotEqual(firstId, secondId);
        var events = await h.FlushAsync();
        Assert.Equal(firstId, (string?)events[0]["sessionId"]);
        Assert.Equal(secondId, (string?)events[1]["sessionId"]);
        Assert.All(events, e => Assert.Equal("minted", Tag(e, "agentcat_session_id_source")));
    }
}
