using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Json;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol;

namespace AgentCat.Tests.Engine;

public class EventNodesTests
{
    private const string NestingMessage = "JSON nesting exceeds 1000 levels.";

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static JsonElement Element(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 2000 });
        return JsonElement.ParseValue(ref reader);
    }

    private static void AssertSeq(IEnumerable<string?> actual, params string[] expected) => Assert.Equal(expected, actual.ToArray());

    private static string Text(JsonNode? node) => Encoding.UTF8.GetString(JsJson.ToUtf8(node));

    private static JsonNode Nested(int depth)
    {
        JsonNode node = new JsonArray();
        for (var i = 1; i < depth; i++)
        {
            node = new JsonArray(node);
        }

        return node;
    }

    private static string NestedText(int depth) => new string('[', depth) + new string(']', depth);

    private static int ArrayDepth(JsonNode? node)
    {
        var depth = 0;
        while (node is JsonArray array)
        {
            depth++;
            node = array.Count == 0 ? null : array[0];
        }

        return depth;
    }

    // Thread-pool threads on macOS get 512 KB; a smaller stack makes a recursive walk fail on every platform.
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
            256 * 1024);
        thread.Start();
        Assert.True(thread.Join(FakeAgentCatApi.WaitBound), "The small-stack thread did not finish.");
        if (failure is not null)
        {
            throw new InvalidOperationException("The action failed on the small-stack thread.", failure);
        }

        return result!;
    }

    [Fact]
    public void EventNodes_keep_lone_surrogate_keys_and_strings()
    {
        const string source = """{"\ud800":"\udc00"}""";

        var fromElement = EventNodes.FromElement(Element(source));
        var fromParsedValues = EventNodes.FromJson(JsonNode.Parse("""{"k":"\udc00","list":["\ud800",1]}"""));
        var fromParsedString = EventNodes.FromJson(JsonNode.Parse("\"\\ud800\""));
        var fromCode = EventNodes.FromJson(new JsonObject { ["\ud800"] = "\udc00" });

        Assert.Equal(source, Text(fromElement));
        Assert.Equal("\ud800", Assert.Single(fromElement!.AsObject()).Key);
        Assert.Equal("\udc00", (string?)fromElement["\ud800"]);
        Assert.Equal("""{"k":"\udc00","list":["\ud800",1]}""", Text(fromParsedValues));
        Assert.Equal("\"\\ud800\"", Text(fromParsedString));
        Assert.Equal(source, Text(fromCode));
    }

    [Fact]
    public void FromElement_builds_case_sensitive_objects_with_JSON_parse_duplicate_rules()
    {
        var node = EventNodes.FromElement(Element("""{"id":1,"ID":2,"dup":"first","x":0,"dup":"last"}"""))!.AsObject();

        AssertSeq(node.Select(p => p.Key).ToArray(), "id", "ID", "dup", "x");
        Assert.Equal("last", (string?)node["dup"]);
        Assert.False(node.Options?.PropertyNameCaseInsensitive ?? false);
        Assert.Equal("""{"id":1,"ID":2,"dup":"last","x":0}""", Text(node));
    }

    [Fact]
    public void Numbers_keep_their_source_text()
    {
        const string source = """{"a":1.50,"b":1e400,"c":-0,"d":12345678901234567890123,"e":[1E+2,0.000001]}""";

        Assert.Equal(source, Text(EventNodes.FromElement(Element(source))));
        Assert.Equal(source, Text(EventNodes.FromJson(JsonNode.Parse(source))));
    }

    [Fact]
    public void Scalars_built_in_code_are_copied_by_value()
    {
        var source = new JsonObject
        {
            ["d"] = 0.1 + 0.2,
            ["i"] = 42,
            ["m"] = 1.5m,
            ["t"] = true,
            ["f"] = false,
            ["n"] = null,
            ["s"] = "text",
            ["c"] = 'x',
        };

        var copy = EventNodes.FromJson(source);

        Assert.Equal(Text(source), Text(copy));
        Assert.Equal("""{"d":0.30000000000000004,"i":42,"m":1.5,"t":true,"f":false,"n":null,"s":"text","c":"x"}""", Text(copy));
    }

    [Fact]
    public void FromJson_returns_a_new_case_sensitive_tree_and_never_touches_the_source()
    {
        var web = JsonSerializer.SerializeToNode(
            new Dictionary<string, int> { ["id"] = 1, ["ID"] = 2 },
            WebOptions.GetTypeInfo(typeof(Dictionary<string, int>)))!;
        var nested = new JsonObject { ["inner"] = new JsonArray(1, "two", new JsonObject { ["k"] = "v" }) };
        var before = nested.ToJsonString();

        var fromWeb = EventNodes.FromJson(web)!.AsObject();
        var copy = EventNodes.FromJson(nested)!.AsObject();

        AssertSeq(fromWeb.Select(p => p.Key).ToArray(), "id", "ID");
        Assert.Equal(before, nested.ToJsonString());
        Assert.Equal(before, copy.ToJsonString());
        Assert.NotSame(nested["inner"], copy["inner"]);
        Assert.Null(copy.Parent);
        copy["inner"]!.AsArray().Add((JsonNode)3);
        Assert.Equal(before, nested.ToJsonString());
    }

    [Fact]
    public void FromJson_reads_parsed_duplicate_keys_as_JSON_parse_does()
    {
        var parsed = JsonNode.Parse("""{"a":1,"b":{"x":"first","x":"last"},"a":2}""");

        Assert.Equal("""{"a":2,"b":{"x":"last"}}""", Text(EventNodes.FromJson(parsed)));
    }

    [Fact]
    public void Values_holding_containers_are_expanded()
    {
        var typeInfo = (JsonTypeInfo<Dictionary<string, int[]>>)WebOptions.GetTypeInfo(typeof(Dictionary<string, int[]>));
        var payload = new Dictionary<string, int[]> { ["Key"] = new int[2] };
        payload["Key"][0] = 1;
        payload["Key"][1] = 2;
        var source = new JsonObject { ["wrapped"] = JsonValue.Create(payload, typeInfo) };

        var copy = EventNodes.FromJson(source)!;

        Assert.Equal(JsonValueKind.Object, source["wrapped"]!.GetValueKind());
        Assert.IsType<JsonObject>(copy["wrapped"]);
        Assert.Equal("""{"wrapped":{"Key":[1,2]}}""", Text(copy));
    }

    [Fact]
    public void Null_and_nesting_limits()
    {
        Assert.Null(EventNodes.FromJson(null));
        Assert.Null(EventNodes.FromElement(Element("null")));
        var deepest = Nested(JsJson.MaxDepth);
        var deepestElement = Element(NestedText(JsJson.MaxDepth));
        var deepestParsed = JsonNode.Parse(NestedText(JsJson.MaxDepth), new JsonNodeOptions(), new JsonDocumentOptions { MaxDepth = 2000 });
        var tooDeep = Nested(JsJson.MaxDepth + 1);
        var tooDeepElement = Element(NestedText(JsJson.MaxDepth + 1));

        var copies = OnSmallStack(() => new[]
        {
            EventNodes.FromJson(deepest),
            EventNodes.FromElement(deepestElement),
            EventNodes.FromJson(deepestParsed),
        });
        var deepNode = OnSmallStack(() => Record.Exception(() => EventNodes.FromJson(tooDeep)));
        var deepElement = OnSmallStack(() => Record.Exception(() => EventNodes.FromElement(tooDeepElement)));

        Assert.All(copies, copy => Assert.Equal(JsJson.MaxDepth, ArrayDepth(copy)));
        Assert.Equal(NestingMessage, Assert.IsType<InvalidOperationException>(deepNode).Message);
        Assert.Equal(NestingMessage, Assert.IsType<InvalidOperationException>(deepElement).Message);
    }

    [Fact]
    public void Deep_mixed_trees_keep_keys_and_values_on_a_small_stack()
    {
        var text = new StringBuilder();
        for (var i = 0; i < JsJson.MaxDepth / 2; i++)
        {
            text.Append("{\"k").Append(i).Append("\":[");
        }

        text.Append("1.50");
        for (var i = 0; i < JsJson.MaxDepth / 2; i++)
        {
            text.Append("]}");
        }

        var element = Element(text.ToString());
        var parsed = JsonNode.Parse(text.ToString(), new JsonNodeOptions(), new JsonDocumentOptions { MaxDepth = 2000 });

        var copies = OnSmallStack(() => new[] { EventNodes.FromElement(element), EventNodes.FromJson(parsed) });

        Assert.All(copies, copy =>
        {
            var node = copy;
            for (var i = 0; i < JsJson.MaxDepth / 2; i++)
            {
                var obj = Assert.IsType<JsonObject>(node);
                Assert.False(obj.Options?.PropertyNameCaseInsensitive ?? false);
                node = Assert.IsType<JsonArray>(Assert.Single(obj, p => p.Key == "k" + i).Value)[0];
            }

            Assert.Equal("1.50", node!.ToJsonString());
        });
    }

    [Fact]
    public void FromResult_writes_the_result_through_its_type_info()
    {
        var result = Mcp.Result("""{"content":[{"type":"text","text":"hi"}],"structuredContent":{"count":1.50,"Count":2},"isError":true,"_meta":{"m":"v"}}""");
        var before = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ModelContextProtocol.Protocol.CallToolResult)));

        var node = EventNodes.FromResult(result);

        Assert.Equal(
            """{"content":[{"type":"text","text":"hi"}],"structuredContent":{"count":1.50,"Count":2},"isError":true,"_meta":{"m":"v"}}""",
            Text(node));
        Assert.Equal(before, JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ModelContextProtocol.Protocol.CallToolResult))));
    }
}
