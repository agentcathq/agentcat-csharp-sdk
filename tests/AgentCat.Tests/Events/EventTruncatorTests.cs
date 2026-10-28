using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Errors;
using AgentCat.Internal.Events;
using AgentCat.Internal.Json;

namespace AgentCat.Tests.Events;

public class EventTruncatorTests
{
    private const string ImageMarker = "[image content redacted - not supported by AgentCat]";
    private const string DataMarker = "[binary data redacted - not supported by AgentCat]";

    private static readonly DateTimeOffset Ts = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static AgentCatEvent Event() => new()
    {
        Id = "evt_1",
        SessionId = "ses_1",
        EventType = "mcp:tools/call",
        Timestamp = Ts,
    };

    private static string Repeat(char c, int count) => new(c, count);

    private static string Repeat(string s, int count) => new StringBuilder(s.Length * count).Insert(0, s, count).ToString();

    private static string Text(JsonNode? node) => node!.GetValue<string>();

    private static string Keys(JsonNode? node) => string.Join(",", node!.AsObject().Select(p => p.Key));

    private static long Size(AgentCatEvent e) => EventTruncator.MeasureBytes(e);

    private static long TsJsonSize(AgentCatEvent e)
    {
        using var document = JsonDocument.Parse(EventShape.ToTsJson(e).ToJsonString());
        return JsJson.Length(document.RootElement);
    }

    private static JsonNode Nest(int levels, Func<JsonNode, JsonNode> wrap, JsonNode leaf)
    {
        var node = leaf;
        for (var i = 0; i < levels; i++)
        {
            node = wrap(node);
        }

        return node;
    }

    private static T OnSmallStack<T>(Func<T> action)
    {
        var result = default(T);
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            1024 * 1024);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The action failed on the small-stack thread.", failure);
        }

        return result!;
    }

    [Fact]
    public void Constants_match_the_TS_SDK()
    {
        Assert.Equal(10, EventTruncator.MaxDepth);
        Assert.Equal(100, EventTruncator.MaxBreadth);
        Assert.Equal(32768, EventTruncator.MaxStringLength);
        Assert.Equal(102400, EventTruncator.MaxEventBytes);
        Assert.Equal(2048, EventTruncator.MaxUserIntentLength);
        Assert.Equal(2048, EventTruncator.MaxErrorMessageLength);
        Assert.Equal(256, EventTruncator.MaxResourceNameLength);
        Assert.Equal(256, EventTruncator.MaxNameVersionLength);
        Assert.Equal(50, EventTruncator.MaxStackFrames);
        Assert.Equal(32768, EventTruncator.MaxContentTextLength);
        Assert.Equal("...", EventTruncator.Suffix);
    }

    [Fact]
    public void Leaves_short_strings_unchanged()
    {
        Assert.Equal("hello", Text(EventTruncator.Normalize(JsonValue.Create("hello"))));
    }

    [Fact]
    public void Truncates_strings_exceeding_max_string_length_with_suffix()
    {
        var result = Text(EventTruncator.Normalize(JsonValue.Create(Repeat('a', 33000))));

        Assert.Equal(32768 + 3, result.Length);
        Assert.EndsWith("...", result, StringComparison.Ordinal);
        Assert.StartsWith(Repeat('a', 100), result, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_strings_at_exactly_max_string_length_unchanged()
    {
        var exact = Repeat('a', 32768);

        Assert.Equal(exact, Text(EventTruncator.Normalize(JsonValue.Create(exact))));
    }

    [Fact]
    public void Converts_code_built_NaN_to_marker()
    {
        Assert.Equal("[NaN]", Text(EventTruncator.Normalize(JsonValue.Create(double.NaN))));
        Assert.Equal("[NaN]", Text(EventTruncator.Normalize(JsonValue.Create(float.NaN))));
    }

    [Fact]
    public void Converts_code_built_infinities_to_markers()
    {
        Assert.Equal("[Infinity]", Text(EventTruncator.Normalize(JsonValue.Create(double.PositiveInfinity))));
        Assert.Equal("[-Infinity]", Text(EventTruncator.Normalize(JsonValue.Create(double.NegativeInfinity))));
        Assert.Equal("[Infinity]", Text(EventTruncator.Normalize(JsonValue.Create(float.PositiveInfinity))));
        Assert.Equal(
            "{\"a\":[\"[NaN]\",\"[-Infinity]\"," + JsonValue.Create(1.5).ToJsonString() + "]}",
            EventTruncator.Normalize(new JsonObject { ["a"] = new JsonArray(double.NaN, double.NegativeInfinity, 1.5) })!.ToJsonString());
    }

    [Fact]
    public void Properties_with_NaN_measure_like_TS()
    {
        var e = Event();
        e.Properties = new JsonObject { ["x"] = double.NaN, ["y"] = double.PositiveInfinity };
        const string tsJson = "{\"id\":\"evt_1\",\"sessionId\":\"ses_1\",\"eventType\":\"mcp:tools/call\","
            + "\"timestamp\":\"2025-01-15T12:00:00.000Z\",\"properties\":{\"x\":null,\"y\":null}}";

        Assert.Equal(Encoding.UTF8.GetByteCount(tsJson), EventTruncator.MeasureBytes(e));

        EventTruncator.Truncate(e);

        Assert.Equal("{\"x\":null,\"y\":null}", Encoding.UTF8.GetString(JsJson.ToUtf8(e.Properties)));
    }

    [Fact]
    public void Passes_through_numbers_unchanged()
    {
        Assert.Equal("42", EventTruncator.Normalize(JsonValue.Create(42))!.ToJsonString());
        Assert.Equal("0", EventTruncator.Normalize(JsonValue.Create(0))!.ToJsonString());
        var negative = EventTruncator.Normalize(JsonValue.Create(-3.14))!;
        Assert.Equal(-3.14, negative.GetValue<double>());
        Assert.Equal(JsonValue.Create(-3.14).ToJsonString(), negative.ToJsonString());
        Assert.Equal("[42,0,-3.14,1E400]", EventTruncator.Normalize(JsonNode.Parse("[42,0,-3.14,1E400]"))!.ToJsonString());
    }

    [Fact]
    public void Passes_through_booleans_unchanged()
    {
        Assert.True(EventTruncator.Normalize(JsonValue.Create(true))!.GetValue<bool>());
        Assert.False(EventTruncator.Normalize(JsonValue.Create(false))!.GetValue<bool>());
    }

    [Fact]
    public void Passes_through_null_as_null()
    {
        Assert.Null(EventTruncator.Normalize(null));
        Assert.Equal("{\"a\":null,\"b\":[null]}", EventTruncator.Normalize(JsonNode.Parse("{\"a\":null,\"b\":[null]}"))!.ToJsonString());
    }

    [Fact]
    public void Code_built_string_values_of_any_type_become_strings()
    {
        var result = EventTruncator.Normalize(new JsonObject
        {
            ["guid"] = JsonValue.Create(new Guid("0f8fad5b-d9cb-469f-a165-70867728950e")),
            ["date"] = JsonValue.Create(new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc)),
            ["char"] = JsonValue.Create('x'),
        })!;

        Assert.Equal(
            "{\"guid\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"date\":\"2025-01-15T12:00:00Z\",\"char\":\"x\"}",
            result.ToJsonString());
        Assert.Equal("2025-01-15T12:00:00Z", result["date"]!.GetValue<string>());
    }

    [Fact]
    public void Collapses_objects_beyond_max_depth_to_Object_placeholder()
    {
        var obj = Nest(15, n => new JsonObject { ["nested"] = n }, new JsonObject { ["value"] = "deep" });

        var result = EventTruncator.Normalize(obj, 5)!;

        Assert.Equal("[Object]", Text(result["nested"]!["nested"]!["nested"]!["nested"]!["nested"]));
    }

    [Fact]
    public void Collapses_arrays_beyond_max_depth_to_Array_placeholder()
    {
        var arr = Nest(15, n => new JsonArray(n), new JsonArray("leaf"));

        var result = EventTruncator.Normalize(arr, 3)!;

        Assert.Equal("[Array]", Text(result[0]![0]![0]));
    }

    [Fact]
    public void Depth_zero_collapses_top_level_containers()
    {
        Assert.Equal("[Object]", Text(EventTruncator.Normalize(new JsonObject { ["a"] = 1 }, 0)));
        Assert.Equal("[Array]", Text(EventTruncator.Normalize(new JsonArray(1, 2), 0)));
    }

    [Fact]
    public void Does_not_collapse_primitives_regardless_of_depth()
    {
        Assert.Equal("hello", Text(EventTruncator.Normalize(JsonValue.Create("hello"), 0)));
        Assert.Equal(42, EventTruncator.Normalize(JsonValue.Create(42), 0)!.GetValue<int>());
    }

    [Fact]
    public void Default_depth_keeps_ten_levels_and_collapses_the_eleventh()
    {
        var obj = Nest(15, n => new JsonObject { ["nested"] = n }, new JsonObject { ["value"] = "deep" });

        var result = EventTruncator.Normalize(obj)!;

        var node = result;
        for (var i = 0; i < 9; i++)
        {
            node = node["nested"]!;
            Assert.IsType<JsonObject>(node);
        }

        Assert.Equal("[Object]", Text(node["nested"]));
    }

    [Fact]
    public void Limits_object_properties_to_max_breadth()
    {
        var wide = new JsonObject();
        for (var i = 0; i < 150; i++)
        {
            wide["key" + i] = i;
        }

        var result = (JsonObject)EventTruncator.Normalize(wide, 10, 5)!;

        Assert.Equal(6, result.Count);
        Assert.Equal("key0,key1,key2,key3,key4,...", Keys(result));
        Assert.Equal("[MaxProperties ~]", Text(result["..."]));
    }

    [Fact]
    public void Limits_array_elements_to_max_breadth()
    {
        var wide = new JsonArray(Enumerable.Range(0, 150).Select(i => (JsonNode?)i).ToArray());

        var result = (JsonArray)EventTruncator.Normalize(wide, 10, 5)!;

        Assert.Equal(6, result.Count);
        Assert.Equal("[0,1,2,3,4,\"[MaxProperties ~]\"]", result.ToJsonString());
    }

    [Fact]
    public void Leaves_objects_within_breadth_limit_unchanged()
    {
        var obj = new JsonObject { ["a"] = 1, ["b"] = 2, ["c"] = 3 };

        Assert.Equal("{\"a\":1,\"b\":2,\"c\":3}", EventTruncator.Normalize(obj, 10, 100)!.ToJsonString());
    }

    [Fact]
    public void Exactly_max_breadth_items_get_no_marker()
    {
        var hundred = new JsonArray(Enumerable.Range(0, 100).Select(i => (JsonNode?)i).ToArray());
        var hundredOne = new JsonArray(Enumerable.Range(0, 101).Select(i => (JsonNode?)i).ToArray());

        Assert.Equal(100, ((JsonArray)EventTruncator.Normalize(hundred)!).Count);
        var limited = (JsonArray)EventTruncator.Normalize(hundredOne)!;
        Assert.Equal(101, limited.Count);
        Assert.Equal("[MaxProperties ~]", Text(limited[100]));
    }

    [Fact]
    public void Breadth_marker_replaces_an_existing_ellipsis_key_in_place()
    {
        var obj = new JsonObject { ["..."] = "orig" };
        for (var i = 0; i < 10; i++)
        {
            obj["k" + i] = i;
        }

        Assert.Equal(
            "{\"...\":\"[MaxProperties ~]\",\"k0\":0,\"k1\":1,\"k2\":2,\"k3\":3}",
            EventTruncator.Normalize(obj, 10, 5)!.ToJsonString());
    }

    [Fact]
    public void Equal_subtrees_in_sibling_branches_are_both_kept()
    {
        var obj = new JsonObject
        {
            ["a"] = new JsonObject { ["value"] = "shared" },
            ["b"] = new JsonObject { ["value"] = "shared" },
        };

        Assert.Equal("{\"a\":{\"value\":\"shared\"},\"b\":{\"value\":\"shared\"}}", EventTruncator.Normalize(obj)!.ToJsonString());
    }

    [Fact]
    public void Normalize_expands_json_values_that_wrap_containers()
    {
        var wrapped = new JsonObject
        {
            ["w"] = JsonValue.Create(
                new Dictionary<string, List<string>> { ["a"] = new() { Repeat('x', 40), "y" } },
                RedactorTestJsonContext.Default.DictionaryStringListString),
        };

        Assert.Equal("{\"w\":{\"a\":\"[Array]\"}}", EventTruncator.Normalize(wrapped, 2)!.ToJsonString());
        Assert.Equal("{\"w\":{\"a\":[\"xxxxx...\",\"y\"]}}", EventTruncator.Normalize(wrapped, 10, 100, 5)!.ToJsonString());
    }

    [Fact]
    public void Normalize_returns_detached_case_sensitive_copies()
    {
        var source = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true })
        {
            ["a"] = new JsonObject { ["b"] = "x" },
            ["n"] = 1,
        };
        var before = source.ToJsonString();

        var result = (JsonObject)EventTruncator.Normalize(source)!;
        var host = new JsonObject { ["copy"] = result };
        result["A"] = 2;

        Assert.Same(host, result.Parent);
        Assert.NotSame(source["a"], result["a"]);
        Assert.Equal(before, source.ToJsonString());
        Assert.Equal("{\"a\":{\"b\":\"x\"},\"n\":1,\"A\":2}", result.ToJsonString());
    }

    [Fact]
    public void Truncates_userIntent_exceeding_2048_chars()
    {
        var e = Event();
        e.UserIntent = Repeat('x', 3000);

        EventTruncator.Truncate(e);

        Assert.Equal(2048 + 3, e.UserIntent!.Length);
        Assert.EndsWith("...", e.UserIntent, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncates_resourceName_exceeding_256_chars()
    {
        var e = Event();
        e.ResourceName = Repeat('t', 300);

        EventTruncator.Truncate(e);

        Assert.Equal(256 + 3, e.ResourceName!.Length);
        Assert.EndsWith("...", e.ResourceName, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncates_server_and_client_names_and_versions_exceeding_256_chars()
    {
        var e = Event();
        e.ServerName = Repeat('s', 300);
        e.ServerVersion = Repeat('v', 300);
        e.ClientName = Repeat('c', 300);
        e.ClientVersion = Repeat("cv", 200);

        EventTruncator.Truncate(e);

        Assert.Equal(256 + 3, e.ServerName!.Length);
        Assert.Equal(256 + 3, e.ServerVersion!.Length);
        Assert.Equal(256 + 3, e.ClientName!.Length);
        Assert.Equal(256 + 3, e.ClientVersion!.Length);
        Assert.Equal(Repeat("cv", 128) + "...", e.ClientVersion);
    }

    [Fact]
    public void Leaves_short_field_values_unchanged()
    {
        var e = Event();
        e.UserIntent = "Get weather";
        e.ResourceName = "fetch_weather";
        e.ServerName = "my-server";
        e.IdentifyActorGivenId = Repeat('g', 300);

        EventTruncator.Truncate(e);

        Assert.Equal("Get weather", e.UserIntent);
        Assert.Equal("fetch_weather", e.ResourceName);
        Assert.Equal("my-server", e.ServerName);
        Assert.Equal(Repeat('g', 300), e.IdentifyActorGivenId);
    }

    [Fact]
    public void Handles_null_fields_gracefully()
    {
        var e = Event();

        EventTruncator.Truncate(e);

        Assert.Null(e.UserIntent);
        Assert.Null(e.ResourceName);
        Assert.Null(e.ServerName);
        Assert.Null(e.ServerVersion);
        Assert.Null(e.ClientName);
        Assert.Null(e.ClientVersion);
        Assert.Null(e.Error);
    }

    [Fact]
    public void Truncates_error_message_exceeding_2048_chars()
    {
        var e = Event();
        e.Error = new JsonObject { ["message"] = Repeat('e', 3000), ["type"] = "Error" };

        EventTruncator.Truncate(e);

        var message = Text(e.Error["message"]);
        Assert.Equal(2048 + 3, message.Length);
        Assert.EndsWith("...", message, StringComparison.Ordinal);
        Assert.Equal("message,type", Keys(e.Error));
    }

    private static JsonArray Frames(int count)
        => new(Enumerable.Range(0, count).Select(i => (JsonNode?)new JsonObject
        {
            ["filename"] = "file" + i + ".ts",
            ["function"] = "func" + i,
            ["lineno"] = i + 1,
            ["in_app"] = true,
        }).ToArray());

    [Fact]
    public void Limits_error_frames_to_first_25_and_last_25()
    {
        var e = Event();
        e.Error = new JsonObject { ["message"] = "test", ["frames"] = Frames(80) };

        EventTruncator.Truncate(e);

        var frames = e.Error["frames"]!.AsArray();
        Assert.Equal(50, frames.Count);
        Assert.Equal("file0.ts", Text(frames[0]!["filename"]));
        Assert.Equal("file24.ts", Text(frames[24]!["filename"]));
        Assert.Equal("file55.ts", Text(frames[25]!["filename"]));
        Assert.Equal("file79.ts", Text(frames[49]!["filename"]));
    }

    [Fact]
    public void Leaves_frames_at_exactly_50_unchanged()
    {
        var e = Event();
        var frames = Frames(50);
        var before = frames.ToJsonString();
        e.Error = new JsonObject { ["message"] = "test", ["frames"] = frames };

        EventTruncator.Truncate(e);

        Assert.Equal(before, e.Error["frames"]!.ToJsonString());
    }

    [Fact]
    public void Keeps_frames_0_to_24_and_26_to_50_of_51()
    {
        var e = Event();
        e.Error = new JsonObject { ["message"] = "m", ["frames"] = new JsonArray(Enumerable.Range(0, 51).Select(i => (JsonNode?)i).ToArray()) };

        EventTruncator.Truncate(e);

        var expected = Enumerable.Range(0, 25).Concat(Enumerable.Range(26, 25));
        Assert.Equal("[" + string.Join(",", expected) + "]", e.Error["frames"]!.ToJsonString());
    }

    [Fact]
    public void Handles_error_without_frames()
    {
        var e = Event();
        e.Error = new JsonObject { ["message"] = "test" };

        EventTruncator.Truncate(e);

        Assert.Equal("{\"message\":\"test\"}", e.Error.ToJsonString());
    }

    [Fact]
    public void Chained_error_frames_are_not_frame_limited()
    {
        var e = Event();
        e.Error = new JsonObject
        {
            ["message"] = "outer",
            ["chained_errors"] = new JsonArray(new JsonObject { ["message"] = "inner", ["frames"] = Frames(80) }),
        };

        EventTruncator.Truncate(e);

        Assert.Equal(80, e.Error["chained_errors"]![0]!["frames"]!.AsArray().Count);
    }

    [Fact]
    public void String_frames_longer_than_50_spread_into_characters()
    {
        var e = Event();
        e.Error = new JsonObject { ["message"] = "m", ["frames"] = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ" };

        EventTruncator.Truncate(e);

        var expected = "abcdefghijklmnopqrstuvwxy" + "BCDEFGHIJKLMNOPQRSTUVWXYZ";
        Assert.Equal(expected.Select(c => c.ToString()).ToArray(), e.Error["frames"]!.AsArray().Select(Text).ToArray());

        var emoji = Event();
        emoji.Error = new JsonObject { ["message"] = "m", ["frames"] = Repeat("\U0001F600", 30) };

        EventTruncator.Truncate(emoji);

        var spread = emoji.Error["frames"]!.AsArray().Select(Text).ToArray();
        var pairs = Enumerable.Repeat("\U0001F600", 12).ToArray();
        Assert.Equal(pairs.Append("\ud83d").Append("\ude00").Concat(pairs).ToArray(), spread);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("\"\"")]
    [InlineData("\"short\"")]
    public void Falsy_and_short_frames_values_are_kept(string json)
    {
        var e = Event();
        e.Error = JsonNode.Parse("{\"message\":\"m\",\"frames\":" + json + "}");

        EventTruncator.Truncate(e);

        Assert.Equal("{\"message\":\"m\",\"frames\":" + json + "}", e.Error!.ToJsonString());
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    public void Truthy_frames_that_are_neither_arrays_nor_strings_throw(string json)
    {
        var e = Event();
        var error = JsonNode.Parse("{\"message\":\"m\",\"frames\":" + json + "}");
        e.Error = error;
        e.UserIntent = Repeat('x', 3000);

        Assert.Throws<InvalidOperationException>(() => EventTruncator.Truncate(e));

        Assert.Same(error, e.Error);
        Assert.Equal(3000, e.UserIntent.Length);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{\"a\":1}")]
    public void Non_string_error_messages_throw_and_leave_the_event_untouched(string json)
    {
        var e = Event();
        var error = JsonNode.Parse("{\"message\":" + json + "}");
        var parameters = new JsonObject { ["q"] = "x" };
        e.Error = error;
        e.Parameters = parameters;
        e.UserIntent = Repeat('x', 3000);

        Assert.Throws<InvalidOperationException>(() => EventTruncator.Truncate(e));

        Assert.Same(error, e.Error);
        Assert.Same(parameters, e.Parameters);
        Assert.Equal(3000, e.UserIntent.Length);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public void Null_and_short_array_error_messages_are_kept(string json)
    {
        var e = Event();
        e.Error = JsonNode.Parse("{\"message\":" + json + "}");

        EventTruncator.Truncate(e);

        Assert.Equal("{\"message\":" + json + "}", e.Error!.ToJsonString());
    }

    [Fact]
    public void Error_and_response_arrays_stay_arrays()
    {
        var e = Event();
        e.Error = new JsonArray("a", "b");
        e.Response = new JsonArray("a", "b");

        EventTruncator.Truncate(e);

        Assert.Equal("[\"a\",\"b\"]", e.Error.ToJsonString());
        Assert.Equal("[\"a\",\"b\"]", e.Response.ToJsonString());
    }

    [Fact]
    public void String_error_is_limited_like_any_normalized_string()
    {
        var e = Event();
        e.Error = Repeat('x', 40000);

        EventTruncator.Truncate(e);

        Assert.Equal(32771, Text(e.Error).Length);
    }

    [Fact]
    public void Truncates_text_content_blocks_exceeding_32KB()
    {
        var e = Event();
        e.Response = new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = Repeat('x', 40000) },
                new JsonObject { ["type"] = "text", ["text"] = "short" }),
        };

        EventTruncator.Truncate(e);

        var first = Text(e.Response["content"]![0]!["text"]);
        Assert.Equal(32768 + 3, first.Length);
        Assert.EndsWith("...", first, StringComparison.Ordinal);
        Assert.Equal("short", Text(e.Response["content"]![1]!["text"]));
    }

    [Fact]
    public void Content_limits_touch_only_text_blocks_and_keep_key_order()
    {
        var e = Event();
        e.Response = new JsonObject
        {
            ["isError"] = false,
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "image", ["text"] = Repeat('x', 40000) },
                new JsonObject { ["type"] = "text", ["text"] = 5 },
                new JsonArray("x"),
                null,
                new JsonObject { ["type"] = "text", ["text"] = Repeat('y', 40000), ["extra"] = 1 }),
            ["structuredContent"] = new JsonObject { ["n"] = 1 },
        };

        EventTruncator.Truncate(e);

        var content = e.Response["content"]!.AsArray();
        Assert.Equal(32771, Text(content[0]!["text"]).Length);
        Assert.Equal("[{\"type\":\"text\",\"text\":5},[\"x\"],null]", new JsonArray(content[1]!.DeepClone(), content[2]!.DeepClone(), null).ToJsonString());
        Assert.Equal("type,text,extra", Keys(content[4]));
        Assert.Equal(Repeat('y', 32768) + "...", Text(content[4]!["text"]));
        Assert.Equal("isError,content,structuredContent", Keys(e.Response));
    }

    [Fact]
    public void Response_content_with_150_blocks_keeps_100_and_the_marker()
    {
        var e = Event();
        e.Response = new JsonObject
        {
            ["content"] = new JsonArray(Enumerable.Range(0, 150).Select(i => (JsonNode?)new JsonObject { ["type"] = "text", ["text"] = "t" + i }).ToArray()),
        };

        EventTruncator.Truncate(e);

        var content = e.Response["content"]!.AsArray();
        Assert.Equal(101, content.Count);
        Assert.Equal("t99", Text(content[99]!["text"]));
        Assert.Equal("[MaxProperties ~]", Text(content[100]));
    }

    [Fact]
    public void Normalizes_identify_actor_data_at_depth_10()
    {
        var e = Event();
        e.IdentifyActorData = JsonNode.Parse("{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":{\"i\":{\"j\":{\"k\":1}}}}}}}}}}}")!.AsObject();

        EventTruncator.Truncate(e);

        Assert.Equal(
            "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":{\"i\":{\"j\":\"[Object]\"}}}}}}}}}}",
            e.IdentifyActorData!.ToJsonString());
    }

    [Fact]
    public void Tags_and_properties_are_not_normalized()
    {
        var e = Event();
        var tags = new Dictionary<string, string> { ["t"] = Repeat('t', 40000) };
        var wide = new JsonObject();
        for (var i = 0; i < 150; i++)
        {
            wide["k" + i] = i;
        }

        var properties = new JsonObject
        {
            ["deep"] = Nest(12, n => new JsonObject { ["n"] = n }, JsonValue.Create(1)),
            ["wide"] = wide,
            ["long"] = Repeat('p', 40000),
        };
        var before = properties.ToJsonString();
        e.Tags = tags;
        e.Properties = properties;

        EventTruncator.Truncate(e);

        Assert.Same(tags, e.Tags);
        Assert.Same(properties, e.Properties);
        Assert.Equal(before, e.Properties!.ToJsonString());
        Assert.Equal(40000, e.Tags!["t"].Length);
    }

    [Fact]
    public void Leaves_small_events_unchanged()
    {
        var e = Event();
        e.Parameters = new JsonObject { ["query"] = "hello" };
        e.Response = JsonNode.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"world\"}]}");

        EventTruncator.Truncate(e);

        Assert.True(Size(e) < 102400);
        Assert.Equal("hello", Text(e.Parameters!["query"]));
        Assert.Equal("{\"content\":[{\"type\":\"text\",\"text\":\"world\"}]}", e.Response!.ToJsonString());
    }

    [Fact]
    public void Reduces_depth_progressively_for_events_exceeding_100KB()
    {
        var root = new JsonObject();
        var current = root;
        for (var i = 0; i < 8; i++)
        {
            var next = new JsonObject();
            current["data"] = Repeat('x', 15000);
            current["next"] = next;
            current = next;
        }

        current["data"] = "leaf";
        var e = Event();
        e.Parameters = root;

        EventTruncator.Truncate(e);

        var node = e.Parameters!;
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(15000, Text(node["data"]).Length);
            node = node["next"]!;
        }

        Assert.Equal("[Object]", Text(node));
        Assert.Equal(90240, Size(e));
    }

    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The action did not throw.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailAfter(int calls, Exception? inner)
    {
        if (calls == 0)
        {
            throw new InvalidOperationException("Tool failed", inner);
        }

        FailAfter(calls - 1, inner);
    }

    private static void AssertParseableFrames(JsonNode? frames)
    {
        var array = Assert.IsType<JsonArray>(frames);
        Assert.True(array.Count > 1);
        Assert.All(array, frame =>
        {
            var obj = Assert.IsType<JsonObject>(frame);
            Assert.Equal(JsonValueKind.String, obj["filename"]!.GetValueKind());
            Assert.Equal(JsonValueKind.String, obj["function"]!.GetValueKind());
        });
    }

    [Fact]
    public void Depth_reduction_keeps_captured_error_frames_parseable()
    {
        var inner = Thrown(() => FailAfter(3, null));
        var e = Event();
        e.Error = ExceptionCapture.Capture(Thrown(() => FailAfter(4, inner)));
        e.Response = new JsonObject
        {
            ["a"] = Repeat('a', 32768),
            ["b"] = Repeat('b', 32768),
            ["c"] = Repeat('c', 32768),
            ["d"] = Repeat('d', 32768),
        };

        EventTruncator.Truncate(e);

        Assert.True(Text(e.Response!["a"]).Length < 32768);
        AssertParseableFrames(e.Error!["frames"]);
        AssertParseableFrames(e.Error["chained_errors"]![0]!["frames"]);
        Assert.Equal("Tool failed", Text(e.Error["message"]));
        Assert.True(Size(e) <= EventTruncator.MaxEventBytes, Size(e).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Depth_reduction_normalizes_error_at_depth_5_and_other_fields_lower()
    {
        var e = Event();
        e.Error = Nest(7, inner => new JsonObject { ["n"] = inner }, JsonValue.Create("leaf"));
        e.Parameters = Nest(7, inner => new JsonObject { ["n"] = inner }, JsonValue.Create("leaf"));
        e.Response = new JsonObject
        {
            ["a"] = Repeat('a', 32768),
            ["b"] = Repeat('b', 32768),
            ["c"] = Repeat('c', 32768),
            ["d"] = Repeat('d', 32768),
        };

        EventTruncator.Truncate(e);

        Assert.Equal("{\"n\":{\"n\":{\"n\":{\"n\":{\"n\":\"[Object]\"}}}}}", e.Error!.ToJsonString());
        Assert.Equal("{\"n\":\"[Object]\"}", e.Parameters!.ToJsonString());
        Assert.True(Size(e) <= EventTruncator.MaxEventBytes);
    }

    private static JsonObject Numbers() => new() { ["n"] = new JsonArray(Enumerable.Range(0, 100).Select(r => (JsonNode?)new JsonArray(Enumerable.Range(0, 100).Select(c => (JsonNode?)(1000000 + r * 100 + c)).ToArray())).ToArray()) };

    [Fact]
    public void Error_at_the_floor_is_reduced_when_still_over_budget()
    {
        var e = Event();
        e.EventType = "custom";
        var error = Numbers();
        error["m"] = Numbers()["n"]!.DeepClone();
        e.Error = error;

        EventTruncator.Truncate(e);

        Assert.True(Size(e) <= EventTruncator.MaxEventBytes);
        Assert.Equal("[Array]", Text(e.Error!["n"]));
    }

    [Fact]
    public void Error_stays_at_depth_5_when_the_largest_strings_pass_fits_the_event()
    {
        var e = Event();
        e.Error = Numbers();
        e.Parameters = new JsonObject { ["data"] = Repeat('x', 40000) };

        EventTruncator.Truncate(e);

        Assert.True(Size(e) <= EventTruncator.MaxEventBytes);
        Assert.IsType<JsonArray>(e.Error!["n"]);
        Assert.IsType<JsonArray>(e.Error["n"]![0]);
    }

    [Fact]
    public void Truncate_keeps_events_with_lone_surrogates_in_parsed_strings()
    {
        var e = Event();
        e.Parameters = JsonNode.Parse("{\"s\":\"\\ud800\"}");

        EventTruncator.Truncate(e);

        Assert.Equal("\ud800", Text(e.Parameters!["s"]));
    }

    [Fact]
    public void Truncates_largest_string_fields_as_last_resort()
    {
        var e = Event();
        e.Parameters = new JsonObject { ["data"] = Repeat('x', 120000) };

        EventTruncator.Truncate(e);

        Assert.Equal(32771, Text(e.Parameters!["data"]).Length);
        Assert.Equal(32898, Size(e));
    }

    [Fact]
    public void Guarantees_100KB_max_for_pathological_payloads()
    {
        var wide = new JsonObject();
        for (var i = 0; i < 50; i++)
        {
            wide["key" + i] = Repeat('v', 3000);
        }

        var e = Event();
        e.Parameters = wide;
        e.Response = new JsonObject { ["data"] = Repeat('r', 30000) };

        EventTruncator.Truncate(e);

        var lengths = e.Parameters!.AsObject().Select(p => Text(p.Value).Length).ToArray();
        var expected = Enumerable.Repeat(1503, 42).Append(2523).Concat(Enumerable.Repeat(3000, 7)).ToArray();
        Assert.Equal(expected, lengths);
        Assert.Equal(15003, Text(e.Response!["data"]).Length);
        Assert.Equal(102332, Size(e));
    }

    [Fact]
    public void Preserves_timestamp_when_last_resort_truncation_runs()
    {
        var e = Event();
        e.Timestamp = new DateTimeOffset(2025, 1, 15, 14, 0, 0, 123, TimeSpan.FromHours(2));
        e.Parameters = new JsonObject
        {
            ["a"] = Repeat('x', 60000),
            ["b"] = Repeat('y', 60000),
            ["c"] = Repeat('z', 60000),
            ["d"] = Repeat('w', 60000),
        };

        EventTruncator.Truncate(e);

        Assert.Equal(new DateTimeOffset(2025, 1, 15, 14, 0, 0, 123, TimeSpan.FromHours(2)), e.Timestamp);
        Assert.Equal(TimeSpan.FromHours(2), e.Timestamp.Offset);
        Assert.Equal(16389, Text(e.Parameters!["a"]).Length);
        Assert.Equal(20130, Text(e.Parameters["b"]).Length);
        Assert.Equal(32771, Text(e.Parameters["c"]).Length);
        Assert.Equal(32771, Text(e.Parameters["d"]).Length);
        Assert.Equal(102206, Size(e));
    }

    [Fact]
    public void Measures_multi_byte_strings_in_UTF8_but_cuts_in_UTF16_units()
    {
        var e = Event();
        e.Parameters = new JsonObject { ["a"] = Repeat('中', 30000), ["b"] = Repeat('文', 30000) };

        EventTruncator.Truncate(e);

        Assert.Equal(15003, Text(e.Parameters!["a"]).Length);
        Assert.Equal(15003, Text(e.Parameters["b"]).Length);
        Assert.Equal(90137, Size(e));
    }

    [Fact]
    public void Cuts_can_split_surrogate_pairs()
    {
        var e = Event();
        e.Parameters = new JsonObject { ["a"] = Repeat("\U0001F600", 20000), ["b"] = Repeat("\U0001F600", 20000) };

        EventTruncator.Truncate(e);

        var a = Text(e.Parameters!["a"]);
        var b = Text(e.Parameters["b"]);
        Assert.Equal(16389, a.Length);
        Assert.Equal(20150, b.Length);
        Assert.Equal('\ud83d', b[b.Length - 4]);
        Assert.EndsWith("\ud83d...", b, StringComparison.Ordinal);
        Assert.Equal(73207, Size(e));
    }

    [Fact]
    public void Last_resort_pass_cuts_tags_and_properties_into_new_instances()
    {
        var tags = new Dictionary<string, string> { ["env"] = "prod", ["big"] = Repeat('t', 150000) };
        var properties = new JsonObject { ["note"] = Repeat('p', 50000), ["n"] = 1 };
        var propertiesBefore = properties.ToJsonString();
        var e = Event();
        e.Tags = tags;
        e.Properties = properties;
        e.Parameters = new JsonObject { ["q"] = Repeat('x', 20000) };

        EventTruncator.Truncate(e);

        Assert.NotSame(tags, e.Tags);
        Assert.NotSame(properties, e.Properties);
        Assert.Equal(150000, tags["big"].Length);
        Assert.Equal(propertiesBefore, properties.ToJsonString());
        Assert.Equal("env,big", string.Join(",", e.Tags!.Keys));
        Assert.Equal("prod", e.Tags["env"]);
        Assert.Equal(67011, e.Tags["big"].Length);
        Assert.Equal(25003, Text(e.Properties!["note"]).Length);
        Assert.Equal(1, e.Properties["n"]!.GetValue<int>());
        Assert.Equal(10003, Text(e.Parameters!["q"]).Length);
        Assert.Equal(102203, Size(e));
    }

    [Fact]
    public void Last_resort_pass_keeps_tags_and_properties_instances_when_not_cut()
    {
        var tags = new Dictionary<string, string> { ["env"] = "prod" };
        var properties = new JsonObject { ["note"] = "short" };
        var e = Event();
        e.Tags = tags;
        e.Properties = properties;
        e.Parameters = new JsonObject
        {
            ["a"] = Repeat('x', 60000),
            ["b"] = Repeat('y', 60000),
            ["c"] = Repeat('z', 60000),
            ["d"] = Repeat('w', 60000),
        };

        EventTruncator.Truncate(e);

        Assert.Same(tags, e.Tags);
        Assert.Same(properties, e.Properties);
        Assert.True(Size(e) <= 102400);
    }

    [Fact]
    public void Last_resort_pass_gives_up_when_no_string_is_over_100_chars()
    {
        var properties = new JsonObject();
        for (var i = 0; i < 1100; i++)
        {
            properties["k" + i] = Repeat('v', 100);
        }

        var e = Event();
        e.Properties = properties;

        EventTruncator.Truncate(e);

        Assert.Same(properties, e.Properties);
        Assert.All(e.Properties!, p => Assert.Equal(100, Text(p.Value).Length));
        Assert.Equal(121107, Size(e));
    }

    [Fact]
    public void Last_resort_pass_gives_up_once_the_cut_strings_are_100_chars_or_shorter()
    {
        var properties = new JsonObject();
        for (var i = 0; i < 2000; i++)
        {
            properties["k" + i] = Repeat('v', 105);
        }

        var before = properties.ToJsonString();
        var e = Event();
        e.Properties = properties;

        EventTruncator.Truncate(e);

        Assert.NotSame(properties, e.Properties);
        Assert.Equal(before, properties.ToJsonString());
        Assert.Equal(2000, e.Properties!.Count);
        Assert.All(e.Properties, p => Assert.Equal(Repeat('v', 53) + "...", Text(p.Value)));
        Assert.Equal(133007, Size(e));
    }

    [Fact]
    public void Last_resort_pass_cuts_the_largest_strings_first()
    {
        var e = Event();
        e.UserIntent = Repeat('u', 3000);
        e.ResourceName = Repeat('r', 300);
        e.Properties = new JsonObject { ["a"] = Repeat('p', 150000) };

        EventTruncator.Truncate(e);

        Assert.Equal(2051, e.UserIntent!.Length);
        Assert.Equal(259, e.ResourceName!.Length);
        Assert.Equal(99735, Text(e.Properties!["a"]).Length);
        Assert.Equal(102203, Size(e));
    }

    [Fact]
    public void Last_resort_pass_can_cut_user_intent_and_SDK_fields()
    {
        var properties = new JsonObject();
        for (var i = 0; i < 60; i++)
        {
            properties["p" + i] = Repeat('p', 2000);
        }

        var e = Event();
        e.UserIntent = Repeat('u', 3000);
        e.Properties = properties;

        EventTruncator.Truncate(e);

        Assert.Equal(1029, e.UserIntent!.Length);
        var expected = Enumerable.Repeat(1003, 19).Append(1514).Concat(Enumerable.Repeat(2000, 40)).ToArray();
        Assert.Equal(expected, e.Properties!.Select(p => Text(p.Value).Length).ToArray());
        Assert.Equal(102263, Size(e));

        var longSession = Event();
        longSession.SessionId = Repeat('s', 200000);
        longSession.UserIntent = Repeat('u', 3000);

        EventTruncator.Truncate(longSession);

        Assert.Equal(100039, longSession.SessionId.Length);
        Assert.Equal(2051, longSession.UserIntent!.Length);
        Assert.Equal(102203, Size(longSession));
    }

    [Fact]
    public void Does_not_mutate_the_original_nodes()
    {
        var parameters = new JsonObject { ["deep"] = new JsonObject { ["nested"] = new JsonObject { ["data"] = Repeat('y', 40000) } } };
        var error = new JsonObject { ["message"] = Repeat('e', 3000), ["frames"] = Frames(80) };
        var response = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Repeat('z', 40000) }) };
        var actor = new JsonObject { ["a"] = Repeat('a', 40000) };
        var snapshots = new[] { parameters.ToJsonString(), error.ToJsonString(), response.ToJsonString(), actor.ToJsonString() };
        var e = Event();
        e.UserIntent = Repeat('x', 3000);
        e.Parameters = parameters;
        e.Error = error;
        e.Response = response;
        e.IdentifyActorData = actor;

        EventTruncator.Truncate(e);

        Assert.Equal(snapshots, new[] { parameters.ToJsonString(), error.ToJsonString(), response.ToJsonString(), actor.ToJsonString() });
        Assert.NotSame(parameters, e.Parameters);
        Assert.NotSame(error, e.Error);
        Assert.NotSame(response, e.Response);
        Assert.NotSame(actor, e.IdentifyActorData);
        Assert.Equal("{\"deep\":{\"nested\":\"[Object]\"}}", e.Parameters!.ToJsonString());
        Assert.Equal(50, e.Error!["frames"]!.AsArray().Count);
        Assert.Equal("{\"filename\":\"file0.ts\",\"function\":\"func0\",\"lineno\":1,\"in_app\":true}", e.Error["frames"]![0]!.ToJsonString());
        Assert.Equal("{\"content\":[\"[Object]\"]}", e.Response!.ToJsonString());
        Assert.Equal(32771, Text(e.IdentifyActorData!["a"]).Length);
        Assert.Equal(40655, Size(e));
    }

    [Fact]
    public void Handles_empty_event_with_minimal_fields()
    {
        var e = Event();

        EventTruncator.Truncate(e);

        Assert.Equal("evt_1", e.Id);
        Assert.Equal("ses_1", e.SessionId);
        Assert.Equal(EventShape.ToTsJson(Event()).ToJsonString(), EventShape.ToTsJson(e).ToJsonString());
    }

    [Fact]
    public void Passes_through_already_small_events_unchanged()
    {
        var e = Event();
        e.UserIntent = "Get weather";
        e.ResourceName = "fetch_weather";
        e.Parameters = new JsonObject { ["location"] = "SF" };
        e.Response = JsonNode.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"65F\"}]}");
        var before = EventShape.ToTsJson(e).ToJsonString();

        EventTruncator.Truncate(e);

        Assert.Equal("Get weather", e.UserIntent);
        Assert.Equal("fetch_weather", e.ResourceName);
        Assert.Equal("SF", Text(e.Parameters!["location"]));
        Assert.Equal(before, EventShape.ToTsJson(e).ToJsonString());
    }

    [Fact]
    public void Handles_event_with_null_parameters_and_response()
    {
        var e = Event();

        EventTruncator.Truncate(e);

        Assert.Null(e.Parameters);
        Assert.Null(e.Response);
        Assert.Null(e.Error);
        Assert.Null(e.IdentifyActorData);
    }

    [Fact]
    public void Preserves_SDK_controlled_fields_exactly()
    {
        var e = new AgentCatEvent
        {
            Id = "evt_abc123",
            SessionId = "ses_xyz789000000000000000000000",
            ProjectId = "proj_test",
            EventType = "mcp:tools/call",
            Timestamp = Ts,
            Duration = 342,
            InputTokens = 12,
            OutputTokens = 34,
            SdkLanguage = "csharp",
            AgentCatVersion = "0.1.12",
            IpAddress = "192.168.1.1",
            IsError = false,
            Parameters = new JsonObject { ["data"] = Repeat('x', 120000) },
        };

        EventTruncator.Truncate(e);

        Assert.Equal("evt_abc123", e.Id);
        Assert.Equal("ses_xyz789000000000000000000000", e.SessionId);
        Assert.Equal("proj_test", e.ProjectId);
        Assert.Equal("mcp:tools/call", e.EventType);
        Assert.Equal(Ts, e.Timestamp);
        Assert.Equal(342, e.Duration);
        Assert.Equal(12, e.InputTokens);
        Assert.Equal(34, e.OutputTokens);
        Assert.Equal("csharp", e.SdkLanguage);
        Assert.Equal("0.1.12", e.AgentCatVersion);
        Assert.Equal("192.168.1.1", e.IpAddress);
        Assert.False(e.IsError);
    }

    [Fact]
    public void Works_after_sanitization_in_the_pipeline()
    {
        var e = Event();
        e.UserIntent = Repeat('x', 3000);
        e.Parameters = new JsonObject
        {
            ["imageData"] = Repeat('A', 12000) + "=",
            ["query"] = "hello",
            ["nested"] = new JsonObject { ["deep"] = new JsonObject { ["value"] = Repeat('y', 40000) } },
        };
        e.Response = new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = Repeat('z', 40000) },
                new JsonObject { ["type"] = "image", ["data"] = "base64img", ["mimeType"] = "image/png" }),
        };

        EventSanitizer.Sanitize(e);
        EventTruncator.Truncate(e);

        Assert.Equal(DataMarker, Text(e.Parameters!["imageData"]));
        Assert.Equal("{\"type\":\"text\",\"text\":\"" + ImageMarker + "\"}", e.Response!["content"]![1]!.ToJsonString());
        Assert.Equal(2048 + 3, e.UserIntent!.Length);
        Assert.True(Text(e.Response["content"]![0]!["text"]).Length <= 32768 + 3);
        Assert.Equal("hello", Text(e.Parameters["query"]));
        Assert.Equal(DataMarker, Text(e.Parameters["nested"]!["deep"]!["value"]));
        Assert.Equal(35246, Size(e));
    }

    [Fact]
    public void MeasureBytes_equals_the_JS_length_of_the_TS_shaped_event()
    {
        var e = AgentCatEventTests.Full();
        e.UserIntent = "café 中文 \U0001F600 <tag> & \"quoted\" \\ \n\t\u0001";
        e.Tags = new Dictionary<string, string> { ["kéy"] = "v\U0001F600", ["n"] = null! };
        e.Parameters = JsonNode.Parse("{\"q\":\"\\u00e9\\ud83d\\ude00\",\"n\":[1.50,-0,2e3,true,false,null],\"e\":\"\\\"\\/\\b\"}");

        Assert.Equal(TsJsonSize(e), Size(e));
        Assert.Equal(TsJsonSize(Event()), Size(Event()));
        Assert.Equal(102, Size(Event()));
    }

    [Fact]
    public void MeasureBytes_counts_each_JS_byte_of_strings()
    {
        var e = Event();
        e.Parameters = JsonValue.Create("é中\U0001F600\n\u0001");

        Assert.Equal(102 + ",\"parameters\":".Length + 2 + 2 + 3 + 4 + 2 + 6, Size(e));
    }

    // Loaded first runs have taken 1.4 s; a walk that is quadratic in any of these inputs takes minutes.
    private static readonly TimeSpan LinearBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public void Huge_payloads_truncate_within_budget_quickly()
    {
        var hugeString = JsonNode.Parse("{\"blob\":\"" + Repeat('x', 10_000_000) + "\"}");
        var hugeArray = JsonNode.Parse("{\"items\":[" + string.Join(",", Enumerable.Range(0, 200_000)) + "]}");
        var item = Repeat('s', 2000);
        var hugeStringArray = new JsonArray(Enumerable.Range(0, 200_000).Select(_ => (JsonNode?)item).ToArray());
        var largeProperties = JsonNode.Parse("{\"blob\":\"" + Repeat('p', 2_000_000) + "\"}")!.AsObject();
        var cases = new Func<AgentCatEvent>[]
        {
            () => { var e = Event(); e.Parameters = hugeString; return e; },
            () => { var e = Event(); e.Parameters = hugeArray; return e; },
            () => { var e = Event(); e.Parameters = hugeStringArray; return e; },
            () => { var e = Event(); e.Properties = largeProperties; return e; },
        };

        foreach (var build in cases)
        {
            var e = build();
            var watch = Stopwatch.StartNew();
            EventTruncator.Truncate(e);
            watch.Stop();

            Assert.True(watch.Elapsed < LinearBudget, watch.Elapsed.ToString());
            Assert.True(Size(e) <= 102400, Size(e).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var stringCase = cases[0]();
        EventTruncator.Truncate(stringCase);
        Assert.Equal(32771, Text(stringCase.Parameters!["blob"]).Length);
        Assert.Equal(32898, Size(stringCase));

        var arrayCase = cases[1]();
        EventTruncator.Truncate(arrayCase);
        Assert.Equal(101, arrayCase.Parameters!["items"]!.AsArray().Count);

        var stringArrayCase = cases[2]();
        EventTruncator.Truncate(stringArrayCase);
        var items = stringArrayCase.Parameters!.AsArray();
        Assert.Equal(101, items.Count);
        Assert.Equal(1003, Text(items[0]).Length);
        Assert.Equal(1706, Text(items[99]).Length);
        Assert.Equal("[MaxProperties ~]", Text(items[100]));
        Assert.Equal(102203, Size(stringArrayCase));

        var propertiesCase = cases[3]();
        EventTruncator.Truncate(propertiesCase);
        Assert.Equal(102076, Text(propertiesCase.Properties!["blob"]).Length);
        Assert.Equal(102203, Size(propertiesCase));
        Assert.Equal(2_000_000, Text(largeProperties["blob"]).Length);
    }

    [Fact]
    public void Code_built_deep_trees_do_not_overflow()
    {
        const int depth = 100_000;
        var deep = Nest(depth, n => new JsonObject { ["n"] = n }, new JsonArray("leaf", 1));

        var e = Event();
        e.Parameters = deep;
        OnSmallStack(() =>
        {
            EventTruncator.Truncate(e);
            return 0;
        });

        var node = e.Parameters!;
        for (var i = 0; i < 9; i++)
        {
            node = node["n"]!;
        }

        Assert.Equal("[Object]", Text(node["n"]));

        var copy = OnSmallStack(() => EventTruncator.Normalize(deep, int.MaxValue)!);
        var current = copy;
        for (var i = 0; i < depth; i++)
        {
            current = current["n"]!;
        }

        Assert.Equal("[\"leaf\",1]", current.ToJsonString());

        var properties = (JsonObject)Nest(10_000, n => new JsonObject { ["n"] = n }, JsonValue.Create(1));
        var withDeepProperties = Event();
        withDeepProperties.Properties = properties;
        var thrown = OnSmallStack(() => Record.Exception(() => EventTruncator.Truncate(withDeepProperties)));

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Same(properties, withDeepProperties.Properties);
    }
}
