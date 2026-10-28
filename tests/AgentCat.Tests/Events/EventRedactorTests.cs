using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentCat.Internal.Events;

namespace AgentCat.Tests.Events;

public class EventRedactorTests
{
    private const string SessionId = "ses_123000000000000000000000000";

    private static readonly DateTimeOffset Jan1 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<string, CancellationToken, ValueTask<string>> LengthRedactor(List<string> seen)
        => (text, _) =>
        {
            seen.Add(text);
            return new ValueTask<string>("[REDACTED-" + text.Length + "]");
        };

    private static async Task<List<string>> RedactAsync(AgentCatEvent e)
    {
        var seen = new List<string>();
        await EventRedactor.RedactStringsAsync(e, LengthRedactor(seen), Ct);
        return seen;
    }

    private static string Json(AgentCatEvent e) => EventShape.ToTsJson(e).ToJsonString();

    private static void AssertSeen(List<string> seen, params string[] expected) => Assert.Equal(expected, seen);

    [Fact]
    public async Task Redacts_basic_string_fields()
    {
        var e = new AgentCatEvent { SessionId = SessionId, UserIntent = "sensitive user intent", Timestamp = Jan1 };

        var seen = await RedactAsync(e);

        Assert.Equal("[REDACTED-21]", e.UserIntent);
        AssertSeen(seen, "sensitive user intent");
    }

    [Fact]
    public async Task Does_not_redact_protected_fields()
    {
        var tags = new Dictionary<string, string> { ["env"] = "secret tag value" };
        var properties = new JsonObject { ["plan"] = "secret property value", ["list"] = new JsonArray("a", "b") };
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Id = "evt_456",
            ProjectId = "proj_789",
            ServerName = "my-mcp-server",
            ServerVersion = "1.2.3",
            ClientName = "my-mcp-client",
            ClientVersion = "4.5.6",
            IdentifyActorGivenId = "actor_123",
            IdentifyActorName = "John Doe",
            ResourceName = "my-resource",
            EventType = "mcp:tools/call",
            Tags = tags,
            Properties = properties,
        };
        var before = Json(e);

        var seen = await RedactAsync(e);

        Assert.Empty(seen);
        Assert.Equal(before, Json(e));
        Assert.Same(tags, e.Tags);
        Assert.Same(properties, e.Properties);
    }

    [Fact]
    public async Task Redacts_nested_string_fields()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Parameters = new JsonObject
            {
                ["query"] = "sensitive query",
                ["options"] = new JsonObject { ["apiKey"] = "secret-key-123", ["timeout"] = 5000 },
            },
            Response = new JsonObject
            {
                ["data"] = "sensitive response data",
                ["metadata"] = new JsonObject { ["source"] = "sensitive source" },
            },
        };

        await RedactAsync(e);

        Assert.Equal("{\"query\":\"[REDACTED-15]\",\"options\":{\"apiKey\":\"[REDACTED-14]\",\"timeout\":5000}}", e.Parameters!.ToJsonString());
        Assert.Equal("{\"data\":\"[REDACTED-23]\",\"metadata\":{\"source\":\"[REDACTED-16]\"}}", e.Response!.ToJsonString());
    }

    [Fact]
    public async Task Redacts_arrays_of_strings()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Parameters = new JsonObject
            {
                ["tags"] = new JsonArray("sensitive1", "sensitive2", "sensitive3"),
                ["numbers"] = new JsonArray(1, 2, 3),
            },
        };

        await RedactAsync(e);

        Assert.Equal(
            "{\"tags\":[\"[REDACTED-10]\",\"[REDACTED-10]\",\"[REDACTED-10]\"],\"numbers\":[1,2,3]}",
            e.Parameters!.ToJsonString());
    }

    [Fact]
    public async Task Redacts_mixed_nested_structures()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Parameters = new JsonObject
            {
                ["users"] = new JsonArray(
                    new JsonObject { ["name"] = "John", ["age"] = 30, ["email"] = "john@example.com" },
                    new JsonObject { ["name"] = "Jane", ["age"] = 25, ["email"] = "jane@example.com" }),
            },
        };

        await RedactAsync(e);

        Assert.Equal(
            "{\"users\":[{\"name\":\"[REDACTED-4]\",\"age\":30,\"email\":\"[REDACTED-16]\"},"
            + "{\"name\":\"[REDACTED-4]\",\"age\":25,\"email\":\"[REDACTED-16]\"}]}",
            e.Parameters!.ToJsonString());
    }

    [Fact]
    public async Task Keeps_nulls_numbers_booleans_and_keys_and_redacts_empty_strings()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Parameters = JsonNode.Parse(
                "{\"nullValue\":null,\"emptyString\":\"\",\"int\":42,\"float\":1.50,\"big\":12345678901234567890,"
                + "\"yes\":true,\"no\":false,\"secret-key\":\"v\",\"list\":[null,0,-1e3,true]}"),
        };

        var seen = await RedactAsync(e);

        Assert.Equal(
            "{\"nullValue\":null,\"emptyString\":\"[REDACTED-0]\",\"int\":42,\"float\":1.50,\"big\":12345678901234567890,"
            + "\"yes\":true,\"no\":false,\"secret-key\":\"[REDACTED-1]\",\"list\":[null,0,-1e3,true]}",
            e.Parameters!.ToJsonString());
        AssertSeen(seen, "", "v");
    }

    [Fact]
    public async Task Keeps_timestamp_and_non_string_fields()
    {
        var timestamp = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Timestamp = timestamp,
            Duration = 1000,
            InputTokens = 7,
            OutputTokens = 9,
            IsError = false,
        };

        var seen = await RedactAsync(e);

        Assert.Empty(seen);
        Assert.Equal(timestamp, e.Timestamp);
        Assert.Equal(1000, e.Duration);
        Assert.Equal(7, e.InputTokens);
        Assert.Equal(9, e.OutputTokens);
        Assert.False(e.IsError);
    }

    [Fact]
    public async Task Redacts_identify_actor_data()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            IdentifyActorGivenId = "user-123",
            IdentifyActorName = "John Doe",
            IdentifyActorData = new JsonObject
            {
                ["email"] = "user@example.com",
                ["preferences"] = new JsonObject { ["theme"] = "dark", ["notifications"] = true },
                ["metadata"] = new JsonObject { ["source"] = "webapp", ["version"] = "1.0.0" },
            },
        };

        var seen = await RedactAsync(e);

        Assert.Equal(
            "{\"email\":\"[REDACTED-16]\",\"preferences\":{\"theme\":\"[REDACTED-4]\",\"notifications\":true},"
            + "\"metadata\":{\"source\":\"[REDACTED-6]\",\"version\":\"[REDACTED-5]\"}}",
            e.IdentifyActorData!.ToJsonString());
        AssertSeen(seen, "user@example.com", "dark", "webapp", "1.0.0");
        Assert.Equal("user-123", e.IdentifyActorGivenId);
        Assert.Equal("John Doe", e.IdentifyActorName);
    }

    [Fact]
    public async Task Redacts_error_objects()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            IsError = true,
            Error = new JsonObject
            {
                ["message"] = "Sensitive error message",
                ["code"] = "ERR_001",
                ["stack"] = "Error: Sensitive error message\n    at functionName (file.js:10:5)",
            },
        };

        await RedactAsync(e);

        Assert.True(e.IsError);
        Assert.Equal(
            "{\"message\":\"[REDACTED-23]\",\"code\":\"[REDACTED-7]\",\"stack\":\"[REDACTED-65]\"}",
            e.Error!.ToJsonString());
    }

    [Fact]
    public async Task Protection_is_top_level_only()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Parameters = new JsonObject
            {
                ["nested"] = new JsonObject
                {
                    ["deeply"] = new JsonObject { ["sessionId"] = "This should be redacted", ["data"] = "sensitive data" },
                },
            },
            Response = new JsonObject
            {
                ["id"] = "x",
                ["tags"] = new JsonArray("t"),
                ["properties"] = new JsonObject { ["p"] = "q" },
            },
        };

        await RedactAsync(e);

        Assert.Equal(SessionId, e.SessionId);
        Assert.Equal(
            "{\"nested\":{\"deeply\":{\"sessionId\":\"[REDACTED-23]\",\"data\":\"[REDACTED-14]\"}}}",
            e.Parameters!.ToJsonString());
        Assert.Equal(
            "{\"id\":\"[REDACTED-1]\",\"tags\":[\"[REDACTED-1]\"],\"properties\":{\"p\":\"[REDACTED-1]\"}}",
            e.Response!.ToJsonString());
    }

    [Fact]
    public async Task Redactor_exception_propagates()
    {
        var failure = new InvalidOperationException("Redaction failed");
        var e = new AgentCatEvent { SessionId = SessionId, UserIntent = "sensitive data" };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await EventRedactor.RedactStringsAsync(e, (_, _) => throw failure, Ct));

        Assert.Same(failure, thrown);
        Assert.Equal("sensitive data", e.UserIntent);
    }

    [Fact]
    public async Task Does_not_redact_inside_protected_properties()
    {
        var properties = new JsonObject
        {
            ["nested"] = new JsonObject
            {
                ["deeply"] = new JsonObject
                {
                    ["sensitive"] = "this should NOT be redacted",
                    ["data"] = new JsonArray("array", "of", "strings"),
                },
            },
        };
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Properties = properties,
            Tags = new Dictionary<string, string> { ["k"] = "secret tag" },
            Parameters = new JsonObject
            {
                ["nested"] = new JsonObject { ["deeply"] = new JsonObject { ["sensitive"] = "this SHOULD be redacted" } },
            },
        };
        var propertiesBefore = properties.ToJsonString();

        var seen = await RedactAsync(e);

        AssertSeen(seen, "this SHOULD be redacted");
        Assert.Equal(propertiesBefore, e.Properties!.ToJsonString());
        Assert.Equal("secret tag", e.Tags!["k"]);
        Assert.Equal("{\"nested\":{\"deeply\":{\"sensitive\":\"[REDACTED-23]\"}}}", e.Parameters!.ToJsonString());
    }

    [Fact]
    public async Task Does_not_modify_original_nodes()
    {
        var parameters = new JsonObject { ["query"] = "sensitive query" };
        var response = new JsonArray("sensitive response", new JsonObject { ["n"] = 1 });
        var error = new JsonObject { ["message"] = "sensitive error" };
        var actorData = new JsonObject { ["email"] = "a@b.c" };
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            UserIntent = "sensitive data",
            Parameters = parameters,
            Response = response,
            Error = error,
            IdentifyActorData = actorData,
        };
        var originals = new JsonNode[] { parameters, response, error, actorData };
        var snapshots = originals.Select(n => n.ToJsonString()).ToArray();

        await RedactAsync(e);

        Assert.Equal(snapshots, originals.Select(n => n.ToJsonString()).ToArray());
        Assert.NotSame(parameters, e.Parameters);
        Assert.NotSame(response, e.Response);
        Assert.NotSame(error, e.Error);
        Assert.NotSame(actorData, e.IdentifyActorData);
        Assert.Equal("[REDACTED-14]", e.UserIntent);
        Assert.Equal("{\"query\":\"[REDACTED-15]\"}", e.Parameters!.ToJsonString());
        Assert.Null(e.Parameters.Parent);
    }

    [Fact]
    public async Task Redacts_every_unprotected_string_and_nothing_else()
    {
        var e = new AgentCatEvent
        {
            Id = "evt_123",
            SessionId = SessionId,
            ProjectId = "proj_123",
            EventType = "mcp:tools/call",
            Timestamp = Jan1,
            Duration = 1000,
            InputTokens = 5,
            OutputTokens = 6,
            IpAddress = "10.0.0.1",
            SdkLanguage = "csharp",
            AgentCatVersion = "0.1.0",
            ServerName = "server_name",
            ServerVersion = "1.2.3",
            ClientName = "client_name",
            ClientVersion = "4.5.6",
            IdentifyActorGivenId = "given_id_123",
            IdentifyActorName = "Actor Name",
            IdentifyActorData = new JsonObject { ["key"] = "value" },
            ResourceName = "resource_123",
            Parameters = new JsonObject { ["param1"] = "value1" },
            Response = new JsonObject { ["result"] = "success" },
            UserIntent = "user intent text",
            IsError = false,
            Error = new JsonObject { ["message"] = "error message" },
            Tags = new Dictionary<string, string> { ["env"] = "prod" },
            Properties = new JsonObject { ["plan"] = "pro" },
        };

        var seen = await RedactAsync(e);

        AssertSeen(
            seen,
            "10.0.0.1", "csharp", "0.1.0", "value", "value1", "success", "user intent text", "error message");
        Assert.Equal(
            "{\"id\":\"evt_123\",\"sessionId\":\"" + SessionId + "\",\"projectId\":\"proj_123\",\"eventType\":\"mcp:tools/call\","
            + "\"timestamp\":\"2024-01-01T00:00:00.000Z\",\"duration\":1000,\"inputTokens\":5,\"outputTokens\":6,"
            + "\"ipAddress\":\"[REDACTED-8]\",\"sdkLanguage\":\"[REDACTED-6]\",\"agentcatVersion\":\"[REDACTED-5]\","
            + "\"serverName\":\"server_name\",\"serverVersion\":\"1.2.3\",\"clientName\":\"client_name\",\"clientVersion\":\"4.5.6\","
            + "\"identifyActorGivenId\":\"given_id_123\",\"identifyActorName\":\"Actor Name\","
            + "\"identifyActorData\":{\"key\":\"[REDACTED-5]\"},\"resourceName\":\"resource_123\","
            + "\"parameters\":{\"param1\":\"[REDACTED-6]\"},\"response\":{\"result\":\"[REDACTED-7]\"},"
            + "\"userIntent\":\"[REDACTED-16]\",\"isError\":false,\"error\":{\"message\":\"[REDACTED-13]\"},"
            + "\"tags\":{\"env\":\"prod\"},\"properties\":{\"plan\":\"pro\"}}",
            Json(e));
    }

    [Fact]
    public async Task Redacts_strings_one_at_a_time_in_document_order()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            IpAddress = "s01",
            SdkLanguage = "s02",
            AgentCatVersion = "s03",
            IdentifyActorData = new JsonObject { ["a"] = "s04", ["b"] = new JsonArray("s05", "s06") },
            Parameters = new JsonObject
            {
                ["x"] = new JsonObject { ["y"] = new JsonArray(new JsonObject { ["z"] = "s07" }, "s08"), ["w"] = "s09" },
                ["v"] = "s10",
            },
            Response = new JsonArray("s11", new JsonArray("s12", new JsonArray("s13")), "s14"),
            UserIntent = "s15",
            Error = new JsonObject { ["message"] = "s16", ["frames"] = new JsonArray(new JsonObject { ["function"] = "s17" }) },
        };
        var seen = new List<string>();
        var inFlight = 0;
        var maxInFlight = 0;

        await EventRedactor.RedactStringsAsync(
            e,
            async (text, _) =>
            {
                maxInFlight = Math.Max(maxInFlight, Interlocked.Increment(ref inFlight));
                await Task.Yield();
                seen.Add(text);
                Interlocked.Decrement(ref inFlight);
                return "[" + text + "]";
            },
            Ct);

        Assert.Equal(Enumerable.Range(1, 17).Select(i => "s" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)), seen);
        Assert.Equal(1, maxInFlight);
        Assert.Equal("{\"x\":{\"y\":[{\"z\":\"[s07]\"},\"[s08]\"],\"w\":\"[s09]\"},\"v\":\"[s10]\"}", e.Parameters!.ToJsonString());
        Assert.Equal("[\"[s11]\",[\"[s12]\",[\"[s13]\"]],\"[s14]\"]", e.Response!.ToJsonString());
    }

    [Fact]
    public async Task Redactor_throwing_midway_leaves_original_nodes_untouched()
    {
        var actorData = new JsonObject { ["a"] = "one" };
        var parameters = new JsonObject { ["b"] = "two", ["c"] = new JsonObject { ["d"] = "three", ["e"] = "four" } };
        var response = new JsonObject { ["f"] = "five" };
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            IpAddress = "10.0.0.1",
            IdentifyActorData = actorData,
            Parameters = parameters,
            Response = response,
            UserIntent = "six",
        };
        var before = Json(e);
        var nodeSnapshots = new[] { actorData.ToJsonString(), parameters.ToJsonString(), response.ToJsonString() };
        var seen = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await EventRedactor.RedactStringsAsync(
            e,
            (text, _) =>
            {
                seen.Add(text);
                if (text == "three")
                {
                    throw new InvalidOperationException("midway");
                }

                return new ValueTask<string>("[R]");
            },
            Ct));

        AssertSeen(seen, "10.0.0.1", "one", "two", "three");
        Assert.Equal(before, Json(e));
        Assert.Same(actorData, e.IdentifyActorData);
        Assert.Same(parameters, e.Parameters);
        Assert.Same(response, e.Response);
        Assert.Equal(nodeSnapshots, new[] { actorData.ToJsonString(), parameters.ToJsonString(), response.ToJsonString() });
    }

    [Fact]
    public async Task Redacts_strings_inside_json_values_that_wrap_containers()
    {
        var list = new List<string> { "secret", "x" };
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Parameters = new JsonObject
            {
                ["wrapped"] = JsonValue.Create(
                    new Dictionary<string, List<string>> { ["a"] = list },
                    RedactorTestJsonContext.Default.DictionaryStringListString),
            },
            Response = JsonValue.Create(new List<string> { "secret" }, RedactorTestJsonContext.Default.ListString),
        };
        Assert.Equal(JsonValueKind.Object, e.Parameters["wrapped"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Array, e.Response!.GetValueKind());

        var seen = await RedactAsync(e);

        AssertSeen(seen, "secret", "x", "secret");
        Assert.Equal("{\"wrapped\":{\"a\":[\"[REDACTED-6]\",\"[REDACTED-1]\"]}}", e.Parameters!.ToJsonString());
        Assert.Equal("[\"[REDACTED-6]\"]", e.Response!.ToJsonString());
    }

    [Fact]
    public async Task Redacts_string_valued_json_values_backed_by_any_type()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Parameters = new JsonObject
            {
                ["guid"] = JsonValue.Create(new Guid("0f8fad5b-d9cb-469f-a165-70867728950e")),
                ["date"] = JsonValue.Create(new DateTime(2024, 1, 2, 12, 0, 0, DateTimeKind.Utc)),
                ["char"] = JsonValue.Create('x'),
            },
        };

        var seen = await RedactAsync(e);

        AssertSeen(seen, "0f8fad5b-d9cb-469f-a165-70867728950e", "2024-01-02T12:00:00Z", "x");
        Assert.Equal(
            "{\"guid\":\"[REDACTED-36]\",\"date\":\"[REDACTED-20]\",\"char\":\"[REDACTED-1]\"}",
            e.Parameters!.ToJsonString());
    }

    [Fact]
    public async Task Code_built_deep_trees_do_not_overflow()
    {
        const int depth = 100_000;
        JsonNode node = new JsonArray("leaf secret", 42, true, null);
        for (var i = 0; i < depth; i++)
        {
            node = i % 2 == 0 ? new JsonObject { ["n"] = node } : new JsonArray(node);
        }

        var e = new AgentCatEvent { SessionId = SessionId, Parameters = node };
        var seen = new List<string>();
        var pending = default(ValueTask);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var thread = new Thread(() => pending = EventRedactor.RedactStringsAsync(e, LengthRedactor(seen), Ct), 1024 * 1024);
        thread.Start();
        thread.Join();
        await pending;
        watch.Stop();

        AssertSeen(seen, "leaf secret");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.NotSame(node, e.Parameters);
        var current = e.Parameters;
        for (var i = 0; i < depth; i++)
        {
            current = current is JsonObject wrapper ? wrapper["n"] : ((JsonArray)current!)[0];
        }

        Assert.Equal("[\"[REDACTED-11]\",42,true,null]", current!.ToJsonString());
    }

    [Fact]
    public async Task Redactor_returning_null_stores_null()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            UserIntent = "intent",
            Parameters = new JsonObject { ["a"] = "x", ["b"] = new JsonArray("y") },
        };

        await EventRedactor.RedactStringsAsync(e, (_, _) => new ValueTask<string>((string)null!), Ct);

        Assert.Null(e.UserIntent);
        Assert.Equal("{\"a\":null,\"b\":[null]}", e.Parameters!.ToJsonString());
    }

    [Fact]
    public async Task Redacts_bare_scalar_roots()
    {
        var e = new AgentCatEvent
        {
            SessionId = SessionId,
            Parameters = JsonValue.Create("secret"),
            Response = JsonValue.Create(42),
            Error = JsonValue.Create("boom"),
        };

        var seen = await RedactAsync(e);

        AssertSeen(seen, "secret", "boom");
        Assert.Equal("\"[REDACTED-6]\"", e.Parameters!.ToJsonString());
        Assert.Equal("42", e.Response!.ToJsonString());
        Assert.Equal("\"[REDACTED-4]\"", e.Error!.ToJsonString());
    }

    [Fact]
    public void Shared_and_cyclic_nodes_cannot_be_built()
    {
        var shared = new JsonObject { ["note"] = "shared secret" };
        var parent = new JsonObject { ["a"] = shared };
        Assert.Throws<InvalidOperationException>(() => parent["b"] = shared);

        var req = new JsonObject { ["url"] = "/checkout" };
        var res = new JsonObject { ["statusText"] = "oops" };
        req["res"] = res;
        Assert.Throws<InvalidOperationException>(() => res["req"] = req);
    }

    [Fact]
    public async Task Passes_the_cancellation_token_to_the_redactor_and_the_hook()
    {
        using var cts = new CancellationTokenSource();
        var tokens = new List<CancellationToken>();
        var e = new AgentCatEvent { SessionId = SessionId, UserIntent = "x" };

        await EventRedactor.RedactStringsAsync(
            e,
            (text, ct) =>
            {
                tokens.Add(ct);
                return new ValueTask<string>(text);
            },
            cts.Token);
        await EventRedactor.ApplyEventHookAsync(
            e,
            (ev, ct) =>
            {
                tokens.Add(ct);
                return new ValueTask<AgentCatEvent?>(ev);
            },
            cts.Token);

        Assert.Equal(new[] { cts.Token, cts.Token }, tokens);
    }

    [Fact]
    public async Task Checks_the_cancellation_token_before_each_string()
    {
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();
        var parameters = new JsonObject { ["a"] = "first", ["b"] = "second" };
        var e = new AgentCatEvent { SessionId = SessionId, IpAddress = "10.0.0.1", Parameters = parameters, UserIntent = "intent" };
        var before = Json(e);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await EventRedactor.RedactStringsAsync(
            e,
            (text, _) =>
            {
                seen.Add(text);
                if (text == "first")
                {
                    cts.Cancel();
                }

                return new ValueTask<string>("[x]");
            },
            cts.Token));

        AssertSeen(seen, "10.0.0.1", "first");
        Assert.Equal(before, Json(e));
        Assert.Same(parameters, e.Parameters);
    }

    [Fact]
    public async Task A_cancelled_token_stops_redaction_before_the_first_string()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var seen = new List<string>();
        var e = new AgentCatEvent { SessionId = SessionId, UserIntent = "intent", Parameters = new JsonObject { ["a"] = "x" } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await EventRedactor.RedactStringsAsync(e, LengthRedactor(seen), cts.Token));

        Assert.Empty(seen);
        Assert.Equal("intent", e.UserIntent);
    }

    private static AgentCatEvent BaseEvent() => new()
    {
        Id = "evt_original",
        SessionId = SessionId,
        ProjectId = "proj_789",
        EventType = "mcp:tools/call",
        Timestamp = Jan1,
        ResourceName = "add_todo",
        Parameters = new JsonObject { ["text"] = "sensitive text" },
        Response = new JsonObject { ["content"] = "sensitive response" },
        UserIntent = "sensitive intent",
    };

    [Fact]
    public async Task Applies_a_synchronous_hook_in_place()
    {
        var e = BaseEvent();
        string? seenResourceName = null;

        var kept = await EventRedactor.ApplyEventHookAsync(
            e,
            (ev, _) =>
            {
                seenResourceName = ev.ResourceName;
                ev.Parameters = new JsonObject { ["text"] = "[SCRUBBED]" };
                return new ValueTask<AgentCatEvent?>(ev);
            },
            Ct);

        Assert.True(kept);
        Assert.Equal("add_todo", seenResourceName);
        Assert.Equal("{\"text\":\"[SCRUBBED]\"}", e.Parameters!.ToJsonString());
        Assert.Equal("{\"content\":\"sensitive response\"}", e.Response!.ToJsonString());
    }

    [Fact]
    public async Task Applies_an_asynchronous_hook_in_place()
    {
        var e = BaseEvent();

        var kept = await EventRedactor.ApplyEventHookAsync(
            e,
            async (ev, _) =>
            {
                await Task.Yield();
                var copy = ev.Clone();
                copy.UserIntent = "[SCRUBBED]";
                return copy;
            },
            Ct);

        Assert.True(kept);
        Assert.Equal("[SCRUBBED]", e.UserIntent);
        Assert.Equal("{\"text\":\"sensitive text\"}", e.Parameters!.ToJsonString());
    }

    [Fact]
    public async Task Hook_receives_a_deep_clone()
    {
        var e = BaseEvent();
        e.IdentifyActorData = new JsonObject { ["k"] = "v" };
        e.Error = new JsonObject { ["message"] = "m" };
        e.Properties = new JsonObject { ["p"] = 1 };
        e.Tags = new Dictionary<string, string> { ["t"] = "1" };
        AgentCatEvent? input = null;

        await EventRedactor.ApplyEventHookAsync(
            e,
            (ev, _) =>
            {
                input = ev;
                return new ValueTask<AgentCatEvent?>((AgentCatEvent?)null);
            },
            Ct);

        Assert.NotNull(input);
        Assert.NotSame(e, input);
        Assert.Equal(Json(e), Json(input!));
        Assert.NotSame(e.Parameters, input!.Parameters);
        Assert.NotSame(e.Response, input.Response);
        Assert.NotSame(e.IdentifyActorData, input.IdentifyActorData);
        Assert.NotSame(e.Error, input.Error);
        Assert.NotSame(e.Properties, input.Properties);
        Assert.NotSame(e.Tags, input.Tags);
    }

    [Fact]
    public async Task Null_result_reports_a_drop_and_leaves_the_event_untouched()
    {
        var e = BaseEvent();
        var parameters = e.Parameters;
        var response = e.Response;

        var kept = await EventRedactor.ApplyEventHookAsync(
            e,
            (ev, _) =>
            {
                ((JsonObject)ev.Parameters!)["text"] = "changed";
                ev.Response = null;
                ev.UserIntent = "changed";
                ev.Id = "evt_forged";
                ev.Tags = new Dictionary<string, string> { ["t"] = "x" };
                return new ValueTask<AgentCatEvent?>((AgentCatEvent?)null);
            },
            Ct);

        Assert.False(kept);
        Assert.Equal(Json(BaseEvent()), Json(e));
        Assert.Same(parameters, e.Parameters);
        Assert.Same(response, e.Response);
    }

    [Fact]
    public async Task Fields_the_hook_set_to_null_stay_null()
    {
        var e = BaseEvent();

        var kept = await EventRedactor.ApplyEventHookAsync(
            e,
            (ev, _) =>
            {
                ev.Response = null;
                ev.UserIntent = null;
                return new ValueTask<AgentCatEvent?>(ev);
            },
            Ct);

        Assert.True(kept);
        Assert.Null(e.Response);
        Assert.Null(e.UserIntent);
        Assert.Equal("{\"text\":\"sensitive text\"}", e.Parameters!.ToJsonString());
    }

    [Fact]
    public async Task Restores_system_managed_fields_if_the_hook_changes_them()
    {
        var e = BaseEvent();

        await EventRedactor.ApplyEventHookAsync(
            e,
            (ev, _) =>
            {
                ev.Id = "evt_forged";
                ev.SessionId = "ses_forged000000000000000000000";
                ev.ProjectId = "proj_forged";
                ev.EventType = "forged:event";
                ev.Timestamp = default;
                ev.ServerName = "changed by hook";
                return new ValueTask<AgentCatEvent?>(ev);
            },
            Ct);

        Assert.Equal("evt_original", e.Id);
        Assert.Equal(SessionId, e.SessionId);
        Assert.Equal("proj_789", e.ProjectId);
        Assert.Equal("mcp:tools/call", e.EventType);
        Assert.Equal(Jan1, e.Timestamp);
        Assert.Equal("changed by hook", e.ServerName);
    }

    [Fact]
    public async Task Restores_system_managed_fields_the_hook_removed_or_added()
    {
        var e = BaseEvent();
        e.ProjectId = null;

        await EventRedactor.ApplyEventHookAsync(
            e,
            (ev, _) =>
            {
                var forged = new AgentCatEvent { ProjectId = "proj_forged", Parameters = ev.Parameters };
                forged.Id = null!;
                forged.SessionId = null!;
                forged.EventType = null!;
                return new ValueTask<AgentCatEvent?>(forged);
            },
            Ct);

        Assert.Equal("evt_original", e.Id);
        Assert.Equal(SessionId, e.SessionId);
        Assert.Null(e.ProjectId);
        Assert.Equal("mcp:tools/call", e.EventType);
        Assert.Equal(Jan1, e.Timestamp);
        Assert.Null(e.ResourceName);
        Assert.Equal("{\"text\":\"sensitive text\"}", e.Parameters!.ToJsonString());
    }

    [Fact]
    public async Task Hook_input_carries_no_hooks_and_string_redaction_still_runs_after_it()
    {
        Assert.DoesNotContain(
            typeof(AgentCatEvent).GetProperties(),
            p => typeof(Delegate).IsAssignableFrom(p.PropertyType));

        var e = BaseEvent();
        string? rawText = null;

        var kept = await EventRedactor.ApplyEventHookAsync(
            e,
            (ev, _) =>
            {
                rawText = (string?)ev.Parameters!["text"];
                ev.Parameters = new JsonObject { ["text"] = "hook output secret" };
                return new ValueTask<AgentCatEvent?>(ev);
            },
            Ct);
        var seen = await RedactAsync(e);

        Assert.True(kept);
        Assert.Equal("sensitive text", rawText);
        Assert.Contains("hook output secret", seen);
        Assert.Equal("{\"text\":\"[REDACTED-18]\"}", e.Parameters!.ToJsonString());
    }

    [Fact]
    public async Task Keeps_object_identity_so_observers_see_the_changes()
    {
        var e = BaseEvent();
        var observer = e;

        await EventRedactor.ApplyEventHookAsync(
            e,
            (ev, _) =>
            {
                var copy = ev.Clone();
                copy.Parameters = new JsonObject { ["text"] = "[SCRUBBED]" };
                return new ValueTask<AgentCatEvent?>(copy);
            },
            Ct);

        Assert.Equal("{\"text\":\"[SCRUBBED]\"}", observer.Parameters!.ToJsonString());
    }

    [Fact]
    public async Task Hook_exception_propagates()
    {
        var failure = new InvalidOperationException("Hook failure");
        var e = BaseEvent();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await EventRedactor.ApplyEventHookAsync(e, (_, _) => throw failure, Ct));
        var thrownAsync = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await EventRedactor.ApplyEventHookAsync(
                e,
                async (_, _) =>
                {
                    await Task.Yield();
                    throw failure;
                },
                Ct));

        Assert.Same(failure, thrown);
        Assert.Same(failure, thrownAsync);
        Assert.Equal(Json(BaseEvent()), Json(e));
    }
}

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
internal sealed partial class RedactorTestJsonContext : JsonSerializerContext;
