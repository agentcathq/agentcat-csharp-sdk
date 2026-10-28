#pragma warning disable CA1861
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry;
using AgentCat.Internal.Telemetry.PostHog;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Telemetry;

public sealed class PostHogExporterTests : IDisposable
{
    private const string ApiKey = "phc_test_key";
    private const string TestSession = "ses_session45600000000000000000";
    private const string TestEvent = "evt_test123";
    private const string GoldenSession = "ses_2yXlShd4ODDcHLYliGW9VgH3m1R";
    private const string GoldenEventId = "evt_2yXlTL6bElM5Whm0HjT0Iz8LL0M";
    private const string InitLine = "PostHogExporter: Initialized with endpoint https://us.i.posthog.com/batch";
    private const string RegisteredLine = "Initialized telemetry exporter: posthog";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly RecordingLog _log = new();
    private readonly RecordingHttpHandler _handler = new();
    private readonly List<AgentCatRuntime> _runtimes = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var runtime in _runtimes)
        {
            runtime.Pipeline.Dispose();
        }
    }

    private AgentCatRuntime Runtime(IPipelineClock? clock = null, HttpMessageHandler? handler = null)
    {
        var runtime = new AgentCatRuntime(
            _log,
            new RecordingSender(),
            clock: clock,
            exportHandler: handler ?? _handler,
            exporterBuilder: ExporterFactory.Build);
        _runtimes.Add(runtime);
        return runtime;
    }

    private ITelemetryExporter Exporter(PostHogExporter? config = null, IPipelineClock? clock = null, HttpMessageHandler? handler = null)
        => ExporterFactory.Create("posthog", config ?? new PostHogExporter(ApiKey), Runtime(clock, handler))
            ?? throw new InvalidOperationException("The PostHog exporter was not built.");

    private ITelemetryExporter Tracing(string apiKey = ApiKey) => Exporter(new PostHogExporter(apiKey) { EnableAITracing = true });

    private async Task DrainAsync()
    {
        foreach (var runtime in _runtimes)
        {
            Assert.True(await runtime.Exports.WaitIdleAsync(Bound, Ct), "The exporter requests did not finish.");
        }
    }

    private async Task<JsonNode> ExportOneAsync(ITelemetryExporter exporter, AgentCatEvent e)
    {
        var before = _handler.Count;
        exporter.Export(e);
        await DrainAsync();
        Assert.Equal(before + 1, _handler.Count);
        return Body(_handler.Requests[before]);
    }

    private static AgentCatEvent MakeEvent(Action<AgentCatEvent>? configure = null)
    {
        var e = new AgentCatEvent
        {
            Id = TestEvent,
            SessionId = TestSession,
            ProjectId = "proj_1",
            EventType = "mcp:tools/call",
            Timestamp = new DateTimeOffset(2025, 1, 15, 10, 0, 0, TimeSpan.Zero),
            ResourceName = "get_weather",
            ServerName = "weather-server",
            ServerVersion = "1.0.0",
            ClientName = "claude-desktop",
            ClientVersion = "2.0.0",
            Duration = 150,
            IsError = false,
        };
        configure?.Invoke(e);
        return e;
    }

    private static AgentCatEvent GoldenEvent(bool isError = false) => new()
    {
        Id = GoldenEventId,
        SessionId = GoldenSession,
        ProjectId = "proj_golden",
        EventType = "mcp:tools/call",
        Timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 5, 123, TimeSpan.Zero),
        Duration = 150,
        AgentCatVersion = "2.1.1",
        ServerName = "weather-server",
        ServerVersion = "1.0.0",
        ClientName = "claude-desktop",
        ClientVersion = "2.0.0",
        IdentifyActorGivenId = "user_abc",
        IdentifyActorName = "Alice",
        IdentifyActorData = new JsonObject { ["email"] = "alice@example.com" },
        ResourceName = "get_weather",
        Parameters = new JsonObject { ["city"] = "London" },
        Response = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "15C" }) },
        UserIntent = "Checking weather",
        IsError = isError,
        Error = isError
            ? new JsonObject { ["message"] = "boom", ["type"] = "TypeError", ["stack"] = "TypeError: boom\n    at x (/a.js:1:1)" }
            : null,
        Tags = new Dictionary<string, string> { ["env"] = "prod", ["Bad Key:x"] = "a,b" },
        Properties = new JsonObject { ["device"] = "desktop" },
    };

    private static AgentCatEvent SessionlessEvent() => new()
    {
        Id = GoldenEventId,
        SessionId = "",
        ProjectId = "",
        EventType = "mcp:tools/list",
        Timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 5, 123, TimeSpan.Zero),
        Duration = 0,
        ServerName = "s",
    };

    private static string[] FixtureLines(string name)
        => Fixtures.Text("exporters/" + name).Replace("\r\n", "\n").Split('\n');

    // The one (URL, HEADERS, BODY) request of "=== {section}" in golden-output.txt.
    private static (string Url, JsonObject Headers, string Body) Golden(string section)
    {
        var lines = FixtureLines("golden-output.txt");
        var at = Array.IndexOf(lines, "=== " + section);
        Assert.True(at >= 0, "Missing fixture section " + section);
        Assert.StartsWith("URL POST ", lines[at + 1], StringComparison.Ordinal);
        Assert.StartsWith("HEADERS ", lines[at + 2], StringComparison.Ordinal);
        Assert.StartsWith("BODY ", lines[at + 3], StringComparison.Ordinal);
        return (
            lines[at + 1].Substring("URL POST ".Length),
            (JsonObject)JsonNode.Parse(lines[at + 2].Substring("HEADERS ".Length))!,
            lines[at + 3].Substring("BODY ".Length));
    }

    private static void AssertHeaders(RecordedHttpRequest request, JsonObject expected)
    {
        foreach (var header in expected)
        {
            Assert.Equal((string)header.Value!, request.Header(header.Key));
        }

        var extra = request.Headers
            .Select(h => h.Key)
            .Where(name => !expected.ContainsKey(name))
            .Select(name => name.ToLowerInvariant())
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(new[] { "content-length", "user-agent" }, extra);
        Assert.StartsWith("agentcat-csharp/", request.Header("User-Agent"), StringComparison.Ordinal);
    }

    private static string Text(RecordedHttpRequest request) => Encoding.UTF8.GetString(request.Body);

    private static JsonNode Body(RecordedHttpRequest request) => JsonNode.Parse(request.Body)!;

    private static JsonObject Item(JsonNode body, int index) => body["batch"]![index]!.AsObject();

    private static JsonObject Props(JsonNode body, int index) => Item(body, index)["properties"]!.AsObject();

    private static JsonObject? SpanOf(JsonNode body)
        => body["batch"]!.AsArray().Select(i => i!.AsObject()).FirstOrDefault(i => (string)i["event"]! == "$ai_span");

    private static void AssertUuidV7(string? value)
    {
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", value);
    }

    // ---- Golden fixtures ----

    [Fact]
    public async Task The_golden_ok_ai_request_matches_the_ts_exporter_byte_for_byte()
    {
        var golden = Golden("posthog ok ai");

        Tracing("phc_x").Export(GoldenEvent());
        await DrainAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(golden.Url, request.Uri.OriginalString);
        AssertHeaders(request, golden.Headers);
        Assert.Equal(golden.Body, Text(request));
    }

    [Fact]
    public async Task The_golden_err_ai_request_matches_the_ts_exporter_byte_for_byte()
    {
        var golden = Golden("posthog err ai");

        Exporter(new PostHogExporter("phc_x") { Host = "https://eu.i.posthog.com/", EnableAITracing = true }).Export(GoldenEvent(isError: true));
        await DrainAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(golden.Url, request.Uri.OriginalString);
        AssertHeaders(request, golden.Headers);
        Assert.Equal(golden.Body, Text(request));
    }

    [Fact]
    public async Task The_golden_custom_request_matches_the_ts_exporter_byte_for_byte()
    {
        var golden = Golden("posthog custom");
        var e = GoldenEvent();
        e.EventType = "agentcat:custom";
        e.ResourceName = null;
        e.SessionId = "";
        e.IdentifyActorGivenId = null;
        e.IdentifyActorName = null;
        e.IdentifyActorData = null;
        e.Tags = null;
        e.Properties = null;
        e.Duration = null;

        Exporter(new PostHogExporter("phc_x")).Export(e);
        await DrainAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(golden.Url, request.Uri.OriginalString);
        AssertHeaders(request, golden.Headers);
        Assert.Equal(golden.Body, Text(request));
    }

    [Fact]
    public async Task The_golden2_sessionless_request_matches_the_ts_exporter_byte_for_byte()
    {
        var lines = FixtureLines("golden2-output.txt");
        var section = Array.IndexOf(lines, "=== posthog");
        Assert.Equal("https://us.i.posthog.com/batch ", lines[section + 1]);
        Assert.StartsWith(" ", lines[section + 2], StringComparison.Ordinal);

        Exporter(new PostHogExporter("phc")).Export(SessionlessEvent());
        await DrainAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://us.i.posthog.com/batch", request.Uri.OriginalString);
        Assert.Equal(lines[section + 2].Substring(1), Text(request));
    }

    // ---- TS posthog-exporter.test.ts ----

    [Fact]
    public async Task Should_send_correct_payload_structure_for_regular_events()
    {
        var exporter = Exporter();

        exporter.Export(MakeEvent());
        await DrainAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://us.i.posthog.com/batch", request.Uri.OriginalString);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/json", request.Header("Content-Type"));
        var body = Body(request);
        Assert.Equal(new[] { "api_key", "batch" }, body.AsObject().Select(p => p.Key));
        Assert.Equal(ApiKey, (string)body["api_key"]!);
        Assert.Single(body["batch"]!.AsArray());

        var item = Item(body, 0);
        Assert.Equal(new[] { "event", "distinct_id", "properties", "timestamp", "type" }, item.Select(p => p.Key));
        Assert.Equal("mcp_tool_call", (string)item["event"]!);
        Assert.Equal("capture", (string)item["type"]!);
        Assert.Equal(TestSession, (string)item["distinct_id"]!);
        Assert.Equal("2025-01-15T10:00:00.000Z", (string)item["timestamp"]!);

        var props = item["properties"]!;
        AssertUuidV7((string?)props["$session_id"]);
        Assert.Equal("get_weather", (string)props["tool_name"]!);
        Assert.Equal("get_weather", (string)props["resource_name"]!);
        Assert.Equal(150, (int)props["duration_ms"]!);
        Assert.Equal("weather-server", (string)props["server_name"]!);
        Assert.Equal("1.0.0", (string)props["server_version"]!);
        Assert.Equal("claude-desktop", (string)props["client_name"]!);
        Assert.Equal("2.0.0", (string)props["client_version"]!);
        Assert.Equal("proj_1", (string)props["project_id"]!);
        Assert.False((bool)props["is_error"]!);
        Assert.Equal(
            new[] { InitLine, RegisteredLine, "PostHogExporter: Sending 1 event(s) for " + TestEvent, "PostHog export success - Event: " + TestEvent },
            _log.Lines);
        Assert.Contains(_log.Written, w => w.Line.StartsWith("PostHogExporter: Sending", StringComparison.Ordinal) && w.Severity == LogSeverity.Information);
        Assert.Contains(_log.Written, w => w.Line.StartsWith("PostHog export success", StringComparison.Ordinal) && w.Severity == LogSeverity.Information);
    }

    [Fact]
    public async Task Should_use_custom_host_when_provided()
    {
        Exporter(new PostHogExporter(ApiKey) { Host = "https://eu.i.posthog.com" }).Export(MakeEvent());
        await DrainAsync();

        Assert.Equal("https://eu.i.posthog.com/batch", Assert.Single(_handler.Requests).Uri.OriginalString);
    }

    [Fact]
    public async Task Should_strip_trailing_slash_from_host()
    {
        Exporter(new PostHogExporter(ApiKey) { Host = "https://my-posthog.example.com/" }).Export(MakeEvent());
        await DrainAsync();

        Assert.Equal("https://my-posthog.example.com/batch", Assert.Single(_handler.Requests).Uri.OriginalString);
        Assert.Equal("PostHogExporter: Initialized with endpoint https://my-posthog.example.com/batch", _log.Lines[0]);
    }

    [Theory]
    [InlineData(null, "https://us.i.posthog.com/batch")]
    [InlineData("", "https://us.i.posthog.com/batch")]
    [InlineData("https://h.test//", "https://h.test//batch")]
    [InlineData("http://localhost:8000", "http://localhost:8000/batch")]
    public void The_host_gives_the_batch_URL(string? host, string expected)
    {
        Exporter(new PostHogExporter(ApiKey) { Host = host });

        Assert.Equal("PostHogExporter: Initialized with endpoint " + expected, _log.Lines[0]);
    }

    [Fact]
    public async Task Should_use_identifyActorGivenId_as_distinct_id_when_available()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e => e.IdentifyActorGivenId = "user_abc123"));

        Assert.Equal("user_abc123", (string)Item(body, 0)["distinct_id"]!);
    }

    [Fact]
    public async Task Should_fall_back_to_sessionId_when_identifyActorGivenId_is_not_set()
    {
        var exporter = Exporter();

        var unset = await ExportOneAsync(exporter, MakeEvent(e => e.IdentifyActorGivenId = null));
        var empty = await ExportOneAsync(exporter, MakeEvent(e => e.IdentifyActorGivenId = ""));

        Assert.Equal(TestSession, (string)Item(unset, 0)["distinct_id"]!);
        Assert.Equal(TestSession, (string)Item(empty, 0)["distinct_id"]!);
    }

    [Fact]
    public async Task Should_fall_back_to_anonymous_when_there_is_no_actor_and_no_session()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e => e.SessionId = ""));

        Assert.Equal("anonymous", (string)Item(body, 0)["distinct_id"]!);
    }

    [Fact]
    public async Task Should_omit_session_id_entirely_for_sessionless_events_empty_sessionId()
    {
        var body = await ExportOneAsync(Tracing(), MakeEvent(e =>
        {
            e.SessionId = "";
            e.IsError = true;
            e.Error = new JsonObject { ["message"] = "boom" };
        }));

        Assert.Equal(3, body["batch"]!.AsArray().Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.False(Props(body, i).ContainsKey("$session_id"));
        }

        var span = SpanOf(body)!["properties"]!.AsObject();
        Assert.False(span.ContainsKey("$ai_session_id"));
        AssertUuidV7((string?)span["$ai_trace_id"]);
        Assert.Equal(UuidV7.FromPrefixedId(TestEvent), (string)span["$ai_trace_id"]!);
    }

    [Fact]
    public async Task Should_send_exception_event_alongside_regular_event_when_isError_is_true()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e =>
        {
            e.IsError = true;
            e.Error = new JsonObject
            {
                ["message"] = "Connection timeout",
                ["type"] = "TimeoutError",
                ["stack"] = "TimeoutError: Connection timeout\n    at fetch (/app/index.js:10:5)",
            };
        }));

        Assert.Equal(2, body["batch"]!.AsArray().Count);
        Assert.Equal("mcp_tool_call", (string)Item(body, 0)["event"]!);
        Assert.True((bool)Props(body, 0)["is_error"]!);

        var exception = Item(body, 1);
        Assert.Equal("$exception", (string)exception["event"]!);
        Assert.Equal(TestSession, (string)exception["distinct_id"]!);
        var props = exception["properties"]!.AsObject();
        Assert.Equal(
            new[]
            {
                "$exception_source", "$session_id", "$exception_message", "$exception_type", "$exception_stacktrace",
                "resource_name", "tool_name", "server_name", "server_version", "client_name", "client_version",
            },
            props.Select(p => p.Key));
        Assert.Equal("Connection timeout", (string)props["$exception_message"]!);
        Assert.Equal("TimeoutError", (string)props["$exception_type"]!);
        Assert.Equal("TimeoutError: Connection timeout\n    at fetch (/app/index.js:10:5)", (string)props["$exception_stacktrace"]!);
        Assert.Equal("backend", (string)props["$exception_source"]!);
        AssertUuidV7((string?)props["$session_id"]);
        Assert.Equal("get_weather", (string)props["resource_name"]!);
        Assert.Equal("get_weather", (string)props["tool_name"]!);
        Assert.Equal("weather-server", (string)props["server_name"]!);
        Assert.Equal(
            new[] { InitLine, RegisteredLine, "PostHogExporter: Sending 2 event(s) for " + TestEvent, "PostHog export success - Event: " + TestEvent },
            _log.Lines);
    }

    [Fact]
    public async Task Should_not_send_exception_event_when_isError_is_false()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e => e.IsError = false));

        Assert.Single(body["batch"]!.AsArray());
        Assert.Equal("mcp_tool_call", (string)Item(body, 0)["event"]!);
    }

    [Fact]
    public async Task Should_not_throw_when_fetch_fails()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("Network error");

        Exporter().Export(MakeEvent());
        await DrainAsync();

        Assert.Equal(1, _handler.Count);
        Assert.Equal(
            new[]
            {
                InitLine,
                RegisteredLine,
                "PostHogExporter: Sending 1 event(s) for " + TestEvent,
                "PostHog export error: HttpRequestException: Network error",
            },
            _log.Lines);
    }

    [Fact]
    public async Task Should_not_throw_when_fetch_returns_non_ok_response()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Bad Request") });

        Exporter().Export(MakeEvent());
        await DrainAsync();

        Assert.Equal(1, _handler.Count);
        Assert.Equal("PostHog export failed - Status: 400, Body: Bad Request", _log.Lines[^1]);
    }

    [Fact]
    public async Task Should_include_set_person_properties_from_identity_data()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e =>
        {
            e.IdentifyActorGivenId = "user_abc";
            e.IdentifyActorName = "Alice";
            e.IdentifyActorData = new JsonObject { ["email"] = "alice@example.com", ["plan"] = "pro" };
        }));

        Assert.Equal("{\"name\":\"Alice\",\"email\":\"alice@example.com\",\"plan\":\"pro\"}", Props(body, 0)["$set"]!.ToJsonString());
    }

    [Fact]
    public async Task Should_not_include_set_when_no_identity_data_is_present()
    {
        var exporter = Exporter();

        var none = await ExportOneAsync(exporter, MakeEvent());
        var empty = await ExportOneAsync(exporter, MakeEvent(e =>
        {
            e.IdentifyActorName = "";
            e.IdentifyActorData = new JsonObject();
        }));

        Assert.False(Props(none, 0).ContainsKey("$set"));
        Assert.False(Props(empty, 0).ContainsKey("$set"));
    }

    [Fact]
    public async Task A_name_in_the_identity_data_replaces_the_actor_name_in_place()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e =>
        {
            e.IdentifyActorName = "Alice";
            e.IdentifyActorData = new JsonObject { ["plan"] = "pro", ["name"] = "Alicia" };
        }));

        Assert.Equal("{\"name\":\"Alicia\",\"plan\":\"pro\"}", Props(body, 0)["$set"]!.ToJsonString());
    }

    [Fact]
    public async Task Should_pass_through_parameters_and_response_as_is_objects_stay_objects()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e =>
        {
            e.Parameters = new JsonObject { ["city"] = "London", ["units"] = "celsius" };
            e.Response = new JsonObject { ["temperature"] = 15, ["condition"] = "cloudy" };
        }));

        var props = Props(body, 0);
        Assert.Equal("{\"city\":\"London\",\"units\":\"celsius\"}", props["parameters"]!.ToJsonString());
        Assert.Equal("{\"temperature\":15,\"condition\":\"cloudy\"}", props["response"]!.ToJsonString());
    }

    [Fact]
    public async Task Should_pass_through_string_parameters_and_response_as_is()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e =>
        {
            e.Parameters = JsonValue.Create("raw input");
            e.Response = JsonValue.Create("raw output");
        }));

        var props = Props(body, 0);
        Assert.Equal("raw input", (string)props["parameters"]!);
        Assert.Equal("raw output", (string)props["response"]!);
    }

    [Fact]
    public async Task Should_only_set_tool_name_for_tools_call_events()
    {
        var exporter = Exporter();

        var call = await ExportOneAsync(exporter, MakeEvent());
        var custom = await ExportOneAsync(exporter, MakeEvent(e =>
        {
            e.EventType = "agentcat:custom";
            e.ResourceName = "my_resource";
        }));
        var read = await ExportOneAsync(exporter, MakeEvent(e =>
        {
            e.EventType = "mcp:resources/read";
            e.ResourceName = "my_resource";
        }));

        Assert.Equal("get_weather", (string)Props(call, 0)["tool_name"]!);
        Assert.Equal("get_weather", (string)Props(call, 0)["resource_name"]!);
        foreach (var body in new[] { custom, read })
        {
            Assert.False(Props(body, 0).ContainsKey("tool_name"));
            Assert.Equal("my_resource", (string)Props(body, 0)["resource_name"]!);
        }

        Assert.Equal("mcp_agentcat:custom", (string)Item(custom, 0)["event"]!);
        Assert.Equal("mcp_resource_read", (string)Item(read, 0)["event"]!);
    }

    [Theory]
    [InlineData("mcp:tools/call", "mcp_tool_call")]
    [InlineData("mcp:tools/list", "mcp_tools_list")]
    [InlineData("mcp:initialize", "mcp_initialize")]
    [InlineData("mcp:resources/read", "mcp_resource_read")]
    [InlineData("mcp:resources/list", "mcp_resources_list")]
    [InlineData("mcp:prompts/get", "mcp_prompt_get")]
    [InlineData("mcp:prompts/list", "mcp_prompts_list")]
    [InlineData("mcp:custom/type", "mcp_custom_type")]
    [InlineData("agentcat:custom", "mcp_agentcat:custom")]
    [InlineData("", "mcp_")]
    [InlineData("a/b/c", "mcp_a_b_c")]
    [InlineData("mcp:mcp:x", "mcp_mcp:x")]
    public void Should_map_event_types_to_PostHog_event_names(string eventType, string expected)
    {
        Assert.Equal(expected, PostHogTelemetryExporter.MapEventType(eventType));
    }

    [Fact]
    public async Task Should_spread_customer_defined_tags_directly_into_properties()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e =>
            e.Tags = new Dictionary<string, string> { ["env"] = "production", ["trace_id"] = "abc-123" }));

        Assert.Equal("production", (string)Props(body, 0)["env"]!);
        Assert.Equal("abc-123", (string)Props(body, 0)["trace_id"]!);
    }

    [Fact]
    public async Task Should_spread_customer_defined_properties_directly_into_properties()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e =>
            e.Properties = new JsonObject { ["device"] = "mobile", ["feature_flags"] = new JsonArray("dark_mode") }));

        Assert.Equal("mobile", (string)Props(body, 0)["device"]!);
        Assert.Equal("[\"dark_mode\"]", Props(body, 0)["feature_flags"]!.ToJsonString());
    }

    [Fact]
    public async Task Should_not_include_customer_tag_or_property_keys_when_not_set_on_event()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent());

        var props = Props(body, 0);
        Assert.Equal("agentcat", (string)props["source"]!);
        Assert.False(props.ContainsKey("env"));
        Assert.False(props.ContainsKey("device"));
    }

    [Fact]
    public async Task Should_include_userIntent_in_properties()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e => e.UserIntent = "Check the weather in London"));

        Assert.Equal("Check the weather in London", (string)Props(body, 0)["user_intent"]!);
    }

    [Fact]
    public async Task Should_emit_ai_span_alongside_regular_event_for_tool_calls_when_enableAITracing_is_true()
    {
        var body = await ExportOneAsync(Tracing(), MakeEvent(e =>
        {
            e.Duration = 250;
            e.Parameters = new JsonObject { ["city"] = "London" };
            e.Response = new JsonObject { ["temp"] = 15 };
        }));

        Assert.Equal(2, body["batch"]!.AsArray().Count);
        Assert.Equal("mcp_tool_call", (string)Item(body, 0)["event"]!);
        var span = Item(body, 1);
        Assert.Equal("$ai_span", (string)span["event"]!);
        Assert.Equal("capture", (string)span["type"]!);
        Assert.Equal(TestSession, (string)span["distinct_id"]!);
        Assert.Equal("2025-01-15T10:00:00.000Z", (string)span["timestamp"]!);

        var props = span["properties"]!.AsObject();
        Assert.Equal(
            new[]
            {
                "$ai_trace_id", "$ai_span_id", "$ai_span_name", "$ai_is_error", "source", "$ai_session_id", "$session_id",
                "$ai_latency", "$ai_input_state", "$ai_output_state", "server_name", "client_name",
            },
            props.Select(p => p.Key));
        Assert.Equal("agentcat_" + TestSession, (string)props["$ai_session_id"]!);
        Assert.NotEqual((string)props["$ai_trace_id"]!, (string)props["$ai_span_id"]!);
        Assert.Equal("get_weather", (string)props["$ai_span_name"]!);
        Assert.Equal("0.25", props["$ai_latency"]!.ToJsonString());
        Assert.False((bool)props["$ai_is_error"]!);
        Assert.Equal("{\"city\":\"London\"}", props["$ai_input_state"]!.ToJsonString());
        Assert.Equal("{\"temp\":15}", props["$ai_output_state"]!.ToJsonString());
        AssertUuidV7((string?)props["$session_id"]);
        Assert.Equal("agentcat", (string)props["source"]!);
        Assert.Equal("weather-server", (string)props["server_name"]!);
        Assert.Equal("claude-desktop", (string)props["client_name"]!);
    }

    [Fact]
    public async Task Should_generate_deterministic_UUIDs_for_ai_span_trace_and_span_IDs()
    {
        var exporter = Tracing();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string Id(string prefix, byte fill)
        {
            var payload = Enumerable.Repeat(fill, 16).ToArray();
            return prefix + "_" + Ksuid.FromParts(now, payload);
        }

        var session = Id("ses", 0x01);
        var evtA = Id("evt", 0x02);
        var evtB = Id("evt", 0x03);

        var a = SpanOf(await ExportOneAsync(exporter, MakeEvent(e => { e.Id = evtA; e.SessionId = session; })))!["properties"]!;
        var b = SpanOf(await ExportOneAsync(exporter, MakeEvent(e => { e.Id = evtB; e.SessionId = session; })))!["properties"]!;
        var c = SpanOf(await ExportOneAsync(exporter, MakeEvent(e => { e.Id = evtA; e.SessionId = session; })))!["properties"]!;

        Assert.Equal("agentcat_" + session, (string)a["$ai_session_id"]!);
        Assert.Equal((string)a["$ai_session_id"]!, (string)b["$ai_session_id"]!);
        Assert.Equal((string)a["$ai_trace_id"]!, (string)b["$ai_trace_id"]!);
        Assert.NotEqual((string)a["$ai_span_id"]!, (string)b["$ai_span_id"]!);
        Assert.Equal((string)a["$ai_span_id"]!, (string)c["$ai_span_id"]!);
        Assert.NotEqual((string)a["$ai_trace_id"]!, (string)a["$ai_span_id"]!);
        AssertUuidV7((string?)a["$ai_trace_id"]);
        AssertUuidV7((string?)a["$ai_span_id"]);
        Assert.Equal(UuidV7.FromPrefixedId(session), (string)a["$ai_trace_id"]!);
        Assert.Equal(UuidV7.FromPrefixedId(evtA), (string)a["$ai_span_id"]!);
    }

    [Fact]
    public async Task Should_NOT_emit_ai_span_when_enableAITracing_is_false_or_unset()
    {
        var unset = await ExportOneAsync(Exporter(), MakeEvent());
        var off = await ExportOneAsync(Exporter(new PostHogExporter(ApiKey) { EnableAITracing = false }), MakeEvent());

        foreach (var body in new[] { unset, off })
        {
            Assert.Single(body["batch"]!.AsArray());
            Assert.Equal("mcp_tool_call", (string)Item(body, 0)["event"]!);
        }
    }

    [Theory]
    [InlineData("mcp:initialize")]
    [InlineData("mcp:tools/list")]
    [InlineData("mcp:resources/read")]
    [InlineData("mcp:resources/list")]
    [InlineData("mcp:prompts/get")]
    [InlineData("mcp:prompts/list")]
    [InlineData("agentcat:custom")]
    public async Task Should_NOT_emit_ai_span_for_non_tool_call_events_even_with_enableAITracing(string eventType)
    {
        var body = await ExportOneAsync(Tracing(), MakeEvent(e => e.EventType = eventType));

        Assert.Null(SpanOf(body));
        Assert.Single(body["batch"]!.AsArray());
    }

    [Fact]
    public async Task Should_spread_customer_tags_and_properties_directly_on_ai_span_not_namespaced()
    {
        var body = await ExportOneAsync(Tracing(), MakeEvent(e =>
        {
            e.Tags = new Dictionary<string, string> { ["env"] = "production", ["region"] = "us-east" };
            e.Properties = new JsonObject { ["feature_flag"] = "new_ui", ["count"] = 42 };
        }));

        var span = SpanOf(body)!["properties"]!;
        Assert.Equal("production", (string)span["env"]!);
        Assert.Equal("us-east", (string)span["region"]!);
        Assert.Equal("new_ui", (string)span["feature_flag"]!);
        Assert.Equal(42, (int)span["count"]!);
        var regular = Props(body, 0);
        Assert.Equal("production", (string)regular["env"]!);
        Assert.Equal("new_ui", (string)regular["feature_flag"]!);
        Assert.Equal(42, (int)regular["count"]!);
    }

    [Fact]
    public async Task Should_allow_customer_tags_to_override_ai_defaults_on_ai_span()
    {
        var body = await ExportOneAsync(Tracing(), MakeEvent(e =>
            e.Tags = new Dictionary<string, string>
            {
                ["$ai_trace_id"] = "custom-trace-uuid-from-customer",
                ["$ai_span_name"] = "custom_name",
            }));

        var span = SpanOf(body)!["properties"]!.AsObject();
        Assert.Equal("custom-trace-uuid-from-customer", (string)span["$ai_trace_id"]!);
        Assert.Equal("custom_name", (string)span["$ai_span_name"]!);
        Assert.Equal(
            new[]
            {
                "$ai_trace_id", "$ai_span_id", "$ai_span_name", "$ai_is_error", "source", "$ai_session_id", "$session_id",
                "$ai_latency", "server_name", "client_name",
            },
            span.Select(p => p.Key));
    }

    [Fact]
    public async Task Should_emit_regular_plus_exception_plus_ai_span_for_error_tool_calls_with_enableAITracing()
    {
        var body = await ExportOneAsync(Tracing(), MakeEvent(e =>
        {
            e.IsError = true;
            e.Error = new JsonObject { ["message"] = "Tool execution failed", ["type"] = "ExecutionError" };
        }));

        Assert.Equal(3, body["batch"]!.AsArray().Count);
        Assert.Equal(new[] { "mcp_tool_call", "$exception", "$ai_span" }, body["batch"]!.AsArray().Select(i => (string)i!["event"]!));
        var span = Props(body, 2);
        Assert.True((bool)span["$ai_is_error"]!);
        Assert.Equal("{\"message\":\"Tool execution failed\",\"type\":\"ExecutionError\"}", span["$ai_error"]!.ToJsonString());
        Assert.Equal(
            new[] { InitLine, RegisteredLine, "PostHogExporter: Sending 3 event(s) for " + TestEvent, "PostHog export success - Event: " + TestEvent },
            _log.Lines);
    }

    // ---- Overrides and presence rules ----

    [Fact]
    public async Task Tags_then_properties_replace_defaults_in_place_and_new_keys_follow_in_order()
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e =>
        {
            e.IdentifyActorName = "Alice";
            e.Tags = new Dictionary<string, string> { ["zeta"] = "z", ["source"] = "tag", ["project_id"] = "tag_project", ["alpha"] = "a" };
            e.Properties = new JsonObject { ["alpha"] = "p", ["$set"] = new JsonObject { ["x"] = 1 }, ["beta"] = "b", ["duration_ms"] = null };
        }));

        var props = Props(body, 0);
        Assert.Equal(
            new[]
            {
                "source", "$session_id", "resource_name", "tool_name", "duration_ms", "server_name", "server_version", "client_name",
                "client_version", "project_id", "is_error", "$set", "zeta", "alpha", "beta",
            },
            props.Select(p => p.Key));
        Assert.Equal("tag", (string)props["source"]!);
        Assert.Equal("tag_project", (string)props["project_id"]!);
        Assert.Equal("p", (string)props["alpha"]!);
        Assert.Equal("{\"x\":1}", props["$set"]!.ToJsonString());
        Assert.Null(props["duration_ms"]);
    }

    [Fact]
    public async Task A_null_tag_value_is_sent_as_null()
    {
        var exporter = Exporter();
        var e = MakeEvent(x => x.Tags = new Dictionary<string, string> { ["k"] = null! });

        exporter.Export(e);
        await DrainAsync();

        Assert.Contains("\"k\":null", Text(Assert.Single(_handler.Requests)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presence_rules_follow_ts_truthiness()
    {
        var e = MakeEvent(x =>
        {
            x.ResourceName = "";
            x.ServerName = "";
            x.ServerVersion = null;
            x.ClientName = "";
            x.ClientVersion = "";
            x.ProjectId = "";
            x.UserIntent = "";
            x.Duration = 0;
            x.IsError = null;
        });

        var body = await ExportOneAsync(Exporter(), e);

        Assert.Equal(
            "{\"source\":\"agentcat\",\"$session_id\":\"" + UuidV7.FromPrefixedId(TestSession) + "\",\"duration_ms\":0}",
            Props(body, 0).ToJsonString());
    }

    [Fact]
    public async Task An_ai_span_without_a_resource_is_named_unknown_tool_and_a_zero_duration_has_zero_latency()
    {
        var body = await ExportOneAsync(Tracing(), MakeEvent(e =>
        {
            e.ResourceName = null;
            e.Duration = 0;
            e.IsError = null;
            e.ServerName = null;
            e.ClientName = null;
        }));

        var span = Props(body, 1);
        Assert.Equal("unknown_tool", (string)span["$ai_span_name"]!);
        Assert.Equal("0", span["$ai_latency"]!.ToJsonString());
        Assert.False((bool)span["$ai_is_error"]!);
    }

    [Theory]
    [InlineData("{\"message\":\"m\",\"type\":\"T\",\"stack\":\"s\"}", "$exception_message:m|$exception_type:T|$exception_stacktrace:s")]
    [InlineData("{\"message\":\"\",\"type\":null,\"stack\":0}", "")]
    [InlineData("{\"message\":5,\"type\":true}", "$exception_message:5|$exception_type:true")]
    [InlineData("{\"Message\":\"m\"}", "")]
    [InlineData("\"plain failure\"", "")]
    [InlineData("[\"a\"]", "")]
    [InlineData("7", "")]
    public async Task The_exception_item_copies_only_truthy_members_of_an_error_object(string error, string expected)
    {
        var body = await ExportOneAsync(Exporter(), MakeEvent(e =>
        {
            e.IsError = true;
            e.Error = JsonNode.Parse(error);
        }));

        Assert.Equal(2, body["batch"]!.AsArray().Count);
        var found = Props(body, 1)
            .Where(p => p.Key.StartsWith("$exception_", StringComparison.Ordinal) && p.Key != "$exception_source")
            .Select(p => p.Key + ":" + (p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : p.Value!.ToJsonString()));
        Assert.Equal(expected, string.Join("|", found));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("\"\"")]
    [InlineData("0")]
    [InlineData("false")]
    public async Task A_failed_event_without_a_truthy_error_sends_no_exception_and_no_ai_error(string? error)
    {
        var body = await ExportOneAsync(Tracing(), MakeEvent(e =>
        {
            e.IsError = true;
            e.Error = error is null ? null : JsonNode.Parse(error);
        }));

        Assert.Equal(new[] { "mcp_tool_call", "$ai_span" }, body["batch"]!.AsArray().Select(i => (string)i!["event"]!));
        Assert.False(Props(body, 1).ContainsKey("$ai_error"));
        Assert.True((bool)Props(body, 1)["$ai_is_error"]!);
    }

    [Fact]
    public async Task A_string_error_is_sent_whole_as_the_ai_error_and_gives_no_exception_members()
    {
        var body = await ExportOneAsync(Tracing(), MakeEvent(e =>
        {
            e.IsError = true;
            e.Error = JsonValue.Create("plain failure");
        }));

        Assert.Equal("plain failure", (string)Props(body, 2)["$ai_error"]!);
        Assert.Equal(new[] { "$exception_source", "$session_id", "resource_name", "tool_name", "server_name", "server_version", "client_name", "client_version" }, Props(body, 1).Select(p => p.Key));
    }

    [Fact]
    public async Task A_default_timestamp_is_the_time_of_the_export()
    {
        var clock = new ManualClock();
        clock.UtcNow = new DateTimeOffset(2026, 10, 23, 8, 30, 0, 123, TimeSpan.Zero);

        var exporter = Exporter(clock: clock);
        exporter.Export(MakeEvent(e => e.Timestamp = default));
        await DrainAsync();

        Assert.Equal("2026-10-23T08:30:00.123Z", (string)Item(Body(Assert.Single(_handler.Requests)), 0)["timestamp"]!);
    }

    // ---- PHP PostHogExportTest ----

    [Fact]
    public void The_constructor_logs_the_endpoint()
    {
        Exporter(new PostHogExporter("phc_x") { Host = "https://eu.i.posthog.com/" });

        Assert.Equal(new[] { "PostHogExporter: Initialized with endpoint https://eu.i.posthog.com/batch", RegisteredLine }, _log.Lines);
    }

    [Fact]
    public async Task A_successful_export_posts_the_batch_and_logs_the_event_id()
    {
        var e = GoldenEvent(isError: true);

        Exporter(new PostHogExporter("phc_x")).Export(e);
        await DrainAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(2, Body(request)["batch"]!.AsArray().Count);
        Assert.Equal(
            new[] { InitLine, RegisteredLine, "PostHogExporter: Sending 2 event(s) for " + GoldenEventId, "PostHog export success - Event: " + GoldenEventId },
            _log.Lines);
    }

    [Fact]
    public async Task A_400_logs_its_status_and_body()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad") });

        Exporter(new PostHogExporter("phc_x")).Export(GoldenEvent(isError: true));
        await DrainAsync();

        Assert.Equal(
            new[] { InitLine, RegisteredLine, "PostHogExporter: Sending 2 event(s) for " + GoldenEventId, "PostHog export failed - Status: 400, Body: bad" },
            _log.Lines);
    }

    [Fact]
    public async Task A_network_error_logs_the_export_error_line()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("connection refused");

        Exporter(new PostHogExporter("phc_x")).Export(GoldenEvent(isError: true));
        await DrainAsync();

        Assert.Equal(
            new[] { InitLine, RegisteredLine, "PostHogExporter: Sending 2 event(s) for " + GoldenEventId, "PostHog export error: HttpRequestException: connection refused" },
            _log.Lines);
    }

    [Fact]
    public async Task Parameters_and_response_are_sent_as_the_event_holds_them()
    {
        var e = GoldenEvent();
        e.Parameters = new JsonObject { ["city"] = "[REDACTED]" };
        e.Response = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "[REDACTED]" }) };

        Exporter().Export(e);
        await DrainAsync();

        var text = Text(Assert.Single(_handler.Requests));
        Assert.Contains("\"parameters\":{\"city\":\"[REDACTED]\"},\"response\":{\"content\":[{\"type\":\"text\",\"text\":\"[REDACTED]\"}]}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("London", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_that_cannot_be_built_sends_nothing_and_logs_the_error_after_the_sending_line()
    {
        var e = MakeEvent();
        e.Parameters = DeepObject();

        Exporter().Export(e);
        await DrainAsync();

        Assert.Equal(0, _handler.Count);
        Assert.Equal(
            new[]
            {
                InitLine,
                RegisteredLine,
                "PostHogExporter: Sending 1 event(s) for " + TestEvent,
                "PostHog export error: InvalidOperationException: JSON nesting exceeds 1000 levels.",
            },
            _log.Lines);
    }

    // ---- Requests ----

    [Fact]
    public async Task A_request_that_gets_no_answer_logs_the_timeout()
    {
        var clock = new ManualClock();
        clock.HoldDelays();
        var handler = RecordingHttpHandler.Silent();

        Exporter(clock: clock, handler: handler).Export(MakeEvent());
        await FakeAgentCatApi.WithinAsync(WaitForRequestsAsync(handler, 1), Bound);
        Assert.Equal(3, _log.Lines.Count);

        clock.ReleaseDelays();
        await DrainAsync();

        Assert.Equal("PostHog export error: Request timed out after 10000 ms", _log.Lines[^1]);
        Assert.Equal(4, _log.Lines.Count);
    }

    [Fact]
    public async Task A_failure_body_is_cut_at_64_KiB()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(new string('x', 100_000)) });

        Exporter().Export(MakeEvent());
        await DrainAsync();

        var prefix = "PostHog export failed - Status: 500, Body: ";
        var line = _log.Lines[^1];
        Assert.StartsWith(prefix, line, StringComparison.Ordinal);
        Assert.Equal(64 * 1024, line.Length - prefix.Length);
    }

    [Fact]
    public async Task A_success_response_body_is_not_read()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ignored") });

        Exporter().Export(MakeEvent());
        await DrainAsync();

        Assert.Equal("PostHog export success - Event: " + TestEvent, _log.Lines[^1]);
    }

    [Fact]
    public async Task The_request_carries_only_the_content_type_and_the_sdk_defaults()
    {
        Exporter().Export(MakeEvent());
        await DrainAsync();

        AssertHeaders(Assert.Single(_handler.Requests), new JsonObject { ["Content-Type"] = "application/json" });
    }

    // TS B-2: every AgentCat attempt exports the same event again. Parameters and response go into two items each time.
    [Fact]
    public async Task Four_exports_of_one_event_send_identical_bytes_and_leave_it_unchanged()
    {
        var exporter = Exporter(new PostHogExporter("phc_x") { EnableAITracing = true });
        var e = GoldenEvent(isError: true);
        var before = e.Clone();

        for (var i = 0; i < 4; i++)
        {
            exporter.Export(e);
        }

        await DrainAsync();

        var bodies = _handler.Requests.Select(Text).ToArray();
        Assert.Equal(4, bodies.Length);
        Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        Assert.Equal(Golden("posthog err ai").Body, bodies[0]);
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("PostHog export error", StringComparison.Ordinal));
        Assert.Null(e.Parameters!.Parent);
        Assert.Null(e.Response!.Parent);
        Assert.Null(e.Error!.Parent);
        Assert.Null(e.Properties!.Parent);
        Assert.Null(e.IdentifyActorData!.Parent);
        Assert.Equal(before.Parameters!.ToJsonString(), e.Parameters.ToJsonString());
        Assert.Equal(before.Response!.ToJsonString(), e.Response.ToJsonString());
        Assert.Equal(before.Error!.ToJsonString(), e.Error.ToJsonString());
        Assert.Equal(before.Properties!.ToJsonString(), e.Properties.ToJsonString());
        Assert.Equal(before.IdentifyActorData!.ToJsonString(), e.IdentifyActorData.ToJsonString());
        Assert.Equal(before.Tags, e.Tags);
        Assert.Equal(before.Id, e.Id);
        Assert.Equal(before.SessionId, e.SessionId);
        Assert.Equal(before.Duration, e.Duration);
        Assert.Equal(before.IsError, e.IsError);
    }

    [Fact]
    public async Task Concurrent_exports_each_send_their_own_batch()
    {
        var exporter = Tracing();

        Parallel.For(0, 40, i =>
        {
            var e = GoldenEvent(isError: true);
            e.Id = "evt_" + i.ToString(CultureInfo.InvariantCulture);
            exporter.Export(e);
        });
        await DrainAsync();

        var requests = _handler.Requests;
        Assert.Equal(40, requests.Count);
        Assert.Equal(40, requests.Select(r => (string)SpanOf(Body(r))!["properties"]!["$ai_span_id"]!).Distinct().Count());
        Assert.Equal(40, _log.Lines.Count(l => l.StartsWith("PostHog export success - Event: evt_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_manager_exports_through_the_exporter_and_registers_it()
    {
        var runtime = Runtime();

        TelemetryManager.Install(runtime, new[] { new KeyValuePair<string, AgentCatExporter?>("ph", new PostHogExporter("phc_x")) });
        runtime.Pipeline.Add(new QueuedEvent(GoldenEvent()));
        Assert.True(await runtime.Pipeline.FlushAsync(Bound, Ct), "The event pipeline did not drain.");

        Assert.Equal(new[] { InitLine, "Initialized telemetry exporter: ph", "Initialized telemetry with 1 exporters" }, _log.Lines.Take(3));
        Assert.Contains(_handler.Requests, r => r.Uri.OriginalString == "https://us.i.posthog.com/batch");
    }

    private static JsonObject DeepObject()
    {
        JsonNode deep = new JsonObject();
        for (var i = 0; i < 1100; i++)
        {
            deep = new JsonObject { ["a"] = deep };
        }

        return (JsonObject)deep;
    }

    private static async Task WaitForRequestsAsync(RecordingHttpHandler handler, int count)
    {
        while (handler.Count < count)
        {
            await Task.Delay(10, Ct);
        }
    }
}
