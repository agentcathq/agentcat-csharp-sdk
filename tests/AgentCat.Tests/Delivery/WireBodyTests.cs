using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentCat.Internal;
using AgentCat.Internal.Delivery;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Delivery;

public class WireBodyTests
{
    private const string MinimalBody =
        """{"project_id":"proj_1","id":"evt_1","session_id":"ses_1","event_type":"mcp:tools/call","timestamp":"2026-01-02T03:04:05.123Z"}""";

    private static readonly string[] AllKeysInWriteOrder =
    {
        "project_id", "id", "session_id", "actor_id", "event_type", "is_error", "error", "resource_name", "duration",
        "input_tokens", "output_tokens", "timestamp", "user_intent", "parameters", "response", "identify_actor_given_id",
        "identify_actor_name", "identify_data", "tags", "properties", "ip_address", "sdk_language", "agentcat_version",
        "server_name", "server_version", "client_name", "client_version",
    };

    private static readonly string[] NeverSent = { "event_id" };

    private static AgentCatEvent Event() => new()
    {
        Id = "evt_1",
        SessionId = "ses_1",
        ProjectId = "proj_1",
        EventType = "mcp:tools/call",
        Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 123, TimeSpan.Zero),
    };

    private static AgentCatEvent ToolCallEvent() => new()
    {
        Id = "evt_2Yx",
        SessionId = "ses_2Yx",
        ProjectId = "proj_abc",
        EventType = "mcp:tools/call",
        Timestamp = new DateTimeOffset(2026, 10, 20, 14, 30, 15, 250, TimeSpan.FromHours(-4)),
        Duration = 42,
        InputTokens = 12,
        OutputTokens = 34,
        IpAddress = "203.0.113.7",
        SdkLanguage = SdkInfo.Language,
        AgentCatVersion = "0.1.0",
        ServerName = "météo",
        ServerVersion = "1.2.3",
        ClientName = "claude-ai",
        ClientVersion = "0.1.0",
        IdentifyActorGivenId = "user-1",
        IdentifyActorName = "Ada",
        IdentifyActorData = new JsonObject { ["plan"] = "pro" },
        ResourceName = "get_forecast",
        Parameters = JsonNode.Parse("""{"city":"Zürich","days":3}"""),
        Response = JsonNode.Parse("""{"content":[{"type":"text","text":"Sunny"}],"isError":true}"""),
        UserIntent = "Plan a hike",
        IsError = true,
        Error = JsonNode.Parse("""{"message":"Boom","type":"InvalidOperationException"}"""),
        Tags = new Dictionary<string, string> { ["env"] = "prod", ["region"] = "eu" },
        Properties = new JsonObject { ["tier"] = "gold", ["score"] = 9.5 },
    };

    private static AgentCatEvent CustomEvent() => new()
    {
        Id = "evt_9Zq",
        SessionId = "",
        ProjectId = "proj_abc",
        EventType = "agentcat:custom",
        Timestamp = new DateTimeOffset(2026, 10, 20, 9, 0, 0, 7, TimeSpan.FromHours(5.5)),
        SdkLanguage = SdkInfo.Language,
        AgentCatVersion = "0.1.0",
        ServerName = "shop",
        ServerVersion = "2.0.0",
        ResourceName = "checkout_completed",
        Parameters = new JsonObject { ["cart"] = new JsonArray(1, 2), ["note"] = "line\nbreak \"q\"" },
        Response = new JsonObject { ["ok"] = true },
        UserIntent = "Say \"hi\" <now> & 'later'",
        Tags = new Dictionary<string, string> { ["feature"] = "checkout" },
        Properties = new JsonObject { ["amount"] = 19.99, ["items"] = new JsonArray("a", "b") },
    };

    private static string Body(AgentCatEvent e) => Encoding.UTF8.GetString(WireBody.Build(e));

    private static JsonElement Parsed(AgentCatEvent e)
    {
        using var document = JsonDocument.Parse(WireBody.Build(e));
        return document.RootElement.Clone();
    }

    private static string[] Keys(JsonElement body) => body.EnumerateObject().Select(p => p.Name).ToArray();

    private static JsonElement RequestSchema()
    {
        using var document = JsonDocument.Parse(Fixtures.Text("api/public-schema.json"));
        return document.RootElement.GetProperty("paths").GetProperty("/v1/public/events/").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").Clone();
    }

    [Fact]
    public void Should_publish_an_absent_sessionId_as_empty_string_and_send_it_as_null()
    {
        var e = new AgentCatEvent
        {
            Id = "evt_1",
            ProjectId = "proj_1",
            EventType = "mcp:tools/call",
            ResourceName = "do_thing",
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 123, TimeSpan.Zero),
        };

        Assert.Equal("", e.SessionId);
        Assert.Contains("\"session_id\":null", Body(e), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, Parsed(e).GetProperty("session_id").ValueKind);
    }

    [Fact]
    public void Should_send_inputTokens_and_outputTokens_on_the_wire_request()
    {
        var e = Event();
        e.ResourceName = "do_thing";
        e.InputTokens = 6;
        e.OutputTokens = 4;

        var body = Parsed(e);

        Assert.Equal(6, body.GetProperty("input_tokens").GetInt32());
        Assert.Equal(4, body.GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public void Should_leave_the_token_fields_undefined_when_the_event_has_none()
    {
        var e = Event();
        e.ResourceName = "do_thing";

        var body = Parsed(e);

        Assert.False(body.TryGetProperty("input_tokens", out _));
        Assert.False(body.TryGetProperty("output_tokens", out _));
    }

    [Fact]
    public void Keys_are_snake_case_only()
    {
        var keys = Keys(Parsed(ToolCallEvent()));

        Assert.All(keys, k => Assert.Matches("^[a-z]+(_[a-z]+)*$", k));
        Assert.Equal(AllKeysInWriteOrder, keys);
    }

    [Fact]
    public void Unset_fields_are_absent_and_empty_strings_are_kept()
    {
        Assert.Equal(MinimalBody, Body(Event()));

        var e = Event();
        e.ResourceName = "";
        e.UserIntent = "";
        e.IdentifyActorName = "";
        e.IpAddress = "";
        e.SdkLanguage = "";
        e.AgentCatVersion = "";
        e.ServerName = "";
        e.ServerVersion = "";
        e.ClientName = "";
        e.ClientVersion = "";

        Assert.Equal(
            """{"project_id":"proj_1","id":"evt_1","session_id":"ses_1","event_type":"mcp:tools/call","resource_name":"","timestamp":"2026-01-02T03:04:05.123Z","user_intent":"","identify_actor_name":"","ip_address":"","sdk_language":"","agentcat_version":"","server_name":"","server_version":"","client_name":"","client_version":""}""",
            Body(e));
    }

    [Fact]
    public void Actor_id_mirrors_identify_actor_given_id()
    {
        var given = Event();
        given.IdentifyActorGivenId = "u1";
        var empty = Event();
        empty.IdentifyActorGivenId = "";

        Assert.Equal(
            """{"project_id":"proj_1","id":"evt_1","session_id":"ses_1","actor_id":"u1","event_type":"mcp:tools/call","timestamp":"2026-01-02T03:04:05.123Z","identify_actor_given_id":"u1"}""",
            Body(given));
        Assert.Equal(
            """{"project_id":"proj_1","id":"evt_1","session_id":"ses_1","actor_id":"","event_type":"mcp:tools/call","timestamp":"2026-01-02T03:04:05.123Z","identify_actor_given_id":""}""",
            Body(empty));
        Assert.Equal(MinimalBody, Body(Event()));
    }

    [Fact]
    public void Is_error_false_is_sent_and_null_is_absent()
    {
        var failed = Event();
        failed.IsError = true;
        var succeeded = Event();
        succeeded.IsError = false;

        Assert.Equal(JsonValueKind.True, Parsed(failed).GetProperty("is_error").ValueKind);
        Assert.Equal(JsonValueKind.False, Parsed(succeeded).GetProperty("is_error").ValueKind);
        Assert.Contains("\"is_error\":false", Body(succeeded), StringComparison.Ordinal);
        Assert.False(Parsed(Event()).TryGetProperty("is_error", out _));
    }

    [Fact]
    public void Timestamp_is_utc_with_milliseconds()
    {
        var e = Event();
        e.Timestamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 123, TimeSpan.FromHours(2)).AddTicks(9_999);

        Assert.Equal("2026-03-04T03:06:07.123Z", Parsed(e).GetProperty("timestamp").GetString());

        e.Timestamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        Assert.Equal("2026-03-04T05:06:07.000Z", Parsed(e).GetProperty("timestamp").GetString());
    }

    [Fact]
    public void Event_types_map_to_the_api_enum()
    {
        var e = Event();
        e.EventType = "mcp:tools/call";
        Assert.Equal("mcp:tools/call", Parsed(e).GetProperty("event_type").GetString());

        e.EventType = "agentcat:custom";
        Assert.Equal("agentcat:custom", Parsed(e).GetProperty("event_type").GetString());

        foreach (var declared in RequestSchema().GetProperty("properties").GetProperty("event_type").GetProperty("enum").EnumerateArray())
        {
            e.EventType = declared.GetString()!;
            Assert.Equal(e.EventType, Parsed(e).GetProperty("event_type").GetString());
        }

        e.EventType = "bogus";
        Assert.Throws<ArgumentException>(() => WireBody.Build(e));

        e.EventType = "";
        Assert.Throws<ArgumentException>(() => WireBody.Build(e));
    }

    [Fact]
    public void Identify_data_empty_object_is_sent()
    {
        var e = Event();
        e.IdentifyActorData = new JsonObject();

        Assert.Contains("\"identify_data\":{}", Body(e), StringComparison.Ordinal);
    }

    [Fact]
    public void Tags_keep_insertion_order()
    {
        var e = Event();
        e.Tags = new Dictionary<string, string> { ["z"] = "1", ["a"] = "2", ["m"] = "3" };

        Assert.Contains("\"tags\":{\"z\":\"1\",\"a\":\"2\",\"m\":\"3\"}", Body(e), StringComparison.Ordinal);

        e.Tags = new SortedList<string, string>(StringComparer.Ordinal) { ["z"] = "1", ["a"] = "2" };

        Assert.Contains("\"tags\":{\"a\":\"2\",\"z\":\"1\"}", Body(e), StringComparison.Ordinal);
    }

    [Fact]
    public void Free_form_fields_are_written_by_JsJson()
    {
        var e = Event();
        e.Parameters = JsonNode.Parse("""{"a":1.0,"s":"\ud800"}""");
        e.Properties = new JsonObject
        {
            ["x"] = double.NaN,
            ["d"] = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };
        e.Response = new JsonObject { ["text"] = "<é> & 'x' \udc00", ["inf"] = double.PositiveInfinity };
        e.Error = new JsonObject { ["message"] = "bad \ud83d" };
        e.IdentifyActorData = new JsonObject { ["name"] = "Zoë \udbff" };

        var body = Body(e);

        Assert.Contains("\"parameters\":{\"a\":1.0,\"s\":\"\\ud800\"}", body, StringComparison.Ordinal);
        Assert.Contains("\"properties\":{\"x\":null,\"d\":\"2026-01-02T03:04:05.000Z\"}", body, StringComparison.Ordinal);
        Assert.Contains("\"response\":{\"text\":\"<é> & 'x' \\udc00\",\"inf\":null}", body, StringComparison.Ordinal);
        Assert.Contains("\"error\":{\"message\":\"bad \\ud83d\"}", body, StringComparison.Ordinal);
        Assert.Contains("\"identify_data\":{\"name\":\"Zoë \\udbff\"}", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Scalar_lone_surrogates_become_U_FFFD()
    {
        var e = Event();
        e.ResourceName = "a\ud800b";
        e.Tags = new Dictionary<string, string> { ["k"] = "x\udc00" };

        var body = Body(e);

        Assert.Contains("\"resource_name\":\"a\\uFFFDb\"", body, StringComparison.Ordinal);
        Assert.Contains("\"tags\":{\"k\":\"x\\uFFFD\"}", body, StringComparison.Ordinal);
        Assert.Equal("a\ufffdb", Parsed(e).GetProperty("resource_name").GetString());
    }

    [Fact]
    public void Snapshot_tool_call_event()
        => Assert.Equal(
            """{"project_id":"proj_abc","id":"evt_2Yx","session_id":"ses_2Yx","actor_id":"user-1","event_type":"mcp:tools/call","is_error":true,"error":{"message":"Boom","type":"InvalidOperationException"},"resource_name":"get_forecast","duration":42,"input_tokens":12,"output_tokens":34,"timestamp":"2026-10-20T18:30:15.250Z","user_intent":"Plan a hike","parameters":{"city":"Zürich","days":3},"response":{"content":[{"type":"text","text":"Sunny"}],"isError":true},"identify_actor_given_id":"user-1","identify_actor_name":"Ada","identify_data":{"plan":"pro"},"tags":{"env":"prod","region":"eu"},"properties":{"tier":"gold","score":9.5},"ip_address":"203.0.113.7","sdk_language":"C#","agentcat_version":"0.1.0","server_name":"météo","server_version":"1.2.3","client_name":"claude-ai","client_version":"0.1.0"}""",
            Body(ToolCallEvent()));

    [Fact]
    public void Snapshot_custom_event()
        => Assert.Equal(
            """{"project_id":"proj_abc","id":"evt_9Zq","session_id":null,"event_type":"agentcat:custom","resource_name":"checkout_completed","timestamp":"2026-10-20T03:30:00.007Z","user_intent":"Say \"hi\" <now> & 'later'","parameters":{"cart":[1,2],"note":"line\nbreak \"q\""},"response":{"ok":true},"tags":{"feature":"checkout"},"properties":{"amount":19.99,"items":["a","b"]},"sdk_language":"C#","agentcat_version":"0.1.0","server_name":"shop","server_version":"2.0.0"}""",
            Body(CustomEvent()));

    [Fact]
    public void Body_matches_public_schema()
    {
        var schema = RequestSchema();
        var declared = schema.GetProperty("properties");
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in new[] { ToolCallEvent(), CustomEvent(), Event() })
        {
            var body = Parsed(e);
            Assert.Equal(JsonValueKind.Object, body.ValueKind);
            foreach (var required in schema.GetProperty("required").EnumerateArray())
            {
                Assert.True(body.TryGetProperty(required.GetString()!, out _), required.GetString());
            }

            foreach (var field in body.EnumerateObject())
            {
                used.Add(field.Name);
                AssertMatchesProperty(field.Name, field.Value, declared);
            }
        }

        var unused = declared.EnumerateObject().Select(p => p.Name).Where(n => !used.Contains(n)).ToArray();
        Assert.Equal(NeverSent, unused);
    }

    [Fact]
    public void Missing_project_or_id_throws()
    {
        var noProject = Event();
        noProject.ProjectId = null;
        var emptyProject = Event();
        emptyProject.ProjectId = "";
        var emptyId = Event();
        emptyId.Id = "";
        var nullId = Event();
        nullId.Id = null!;
        var unknownType = Event();
        unknownType.EventType = "mcp:unknown";

        Assert.Throws<ArgumentException>(() => WireBody.Build(noProject));
        Assert.Throws<ArgumentException>(() => WireBody.Build(emptyProject));
        Assert.Throws<ArgumentException>(() => WireBody.Build(emptyId));
        Assert.Throws<ArgumentException>(() => WireBody.Build(nullId));
        Assert.Throws<ArgumentException>(() => WireBody.Build(unknownType));
    }

    private static void AssertMatchesProperty(string name, JsonElement value, JsonElement declared)
    {
        Assert.True(declared.TryGetProperty(name, out var property), name + " is not a declared property");
        if (value.ValueKind == JsonValueKind.Null)
        {
            Assert.True(property.TryGetProperty("nullable", out var nullable) && nullable.ValueKind == JsonValueKind.True, name + " is not nullable");
            return;
        }

        var type = property.GetProperty("type").GetString();
        Assert.True(HasType(value, type), name + " is not of type " + type);
        if (property.TryGetProperty("enum", out var allowed))
        {
            Assert.Contains(value.GetString(), allowed.EnumerateArray().Select(a => a.GetString()));
        }

        if (property.TryGetProperty("format", out var format) && format.GetString() == "date-time")
        {
            var text = value.GetString()!;
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", text);
            Assert.True(DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed), text);
            Assert.Equal(TimeSpan.Zero, parsed.Offset);
        }

        if (property.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            var itemType = extra.GetProperty("type").GetString();
            Assert.All(value.EnumerateObject(), p => Assert.True(HasType(p.Value, itemType), name + "." + p.Name + " is not of type " + itemType));
        }
    }

    private static bool HasType(JsonElement value, string? type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => false,
    };
}
