using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentCat.Internal.Json;

namespace AgentCat.Tests.Json;

public class JsonTreeCopyTests
{
    [Fact]
    public void Copies_every_kind_of_value_into_a_detached_equal_tree()
    {
        const string json = "{\"s\":\"text\",\"n\":1.50,\"big\":1E400,\"t\":true,\"f\":false,\"z\":null,\"a\":[1,\"two\",[],{}],\"o\":{\"k\":[null]}}";
        var source = new JsonObject { ["payload"] = JsonNode.Parse(json) };
        var payload = source["payload"]!;

        var copy = JsonTreeCopy.Copy(payload)!;

        Assert.Equal(json, copy.ToJsonString());
        Assert.Null(copy.Parent);
        Assert.NotSame(payload["a"], copy["a"]);
        Assert.NotSame(payload["o"], copy["o"]);
        Assert.Same(payload, source["payload"]);
        Assert.Equal(json, payload.ToJsonString());
    }

    [Fact]
    public void Copies_are_case_sensitive_whatever_the_source_options()
    {
        var source = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true })
        {
            ["x"] = 1,
            ["nested"] = new JsonObject(new JsonNodeOptions()) { ["a"] = "lower", ["A"] = "upper" },
        };

        var copy = (JsonObject)JsonTreeCopy.Copy(source)!;
        copy["X"] = 2;
        var host = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true }) { ["host"] = copy };
        copy["NESTED"] = 3;

        Assert.Same(host, copy.Parent);

        Assert.Equal("{\"x\":1,\"nested\":{\"a\":\"lower\",\"A\":\"upper\"},\"X\":2,\"NESTED\":3}", copy.ToJsonString());
        Assert.Equal("{\"x\":1,\"nested\":{\"a\":\"lower\",\"A\":\"upper\"}}", source.ToJsonString());
    }

    [Fact]
    public void Numbers_and_booleans_built_in_code_are_copied_by_value()
    {
        var source = new JsonArray(
            double.NaN,
            double.PositiveInfinity,
            double.NegativeInfinity,
            float.NaN,
            int.MinValue,
            long.MaxValue,
            1.5m,
            (short)-2,
            (byte)3,
            (sbyte)-4,
            (ushort)5,
            6u,
            ulong.MaxValue,
            true,
            false);

        var copy = (JsonArray)JsonTreeCopy.Copy(source)!;

        Assert.Equal(source.Count, copy.Count);
        Assert.True(double.IsNaN(copy[0]!.GetValue<double>()));
        Assert.Equal(double.PositiveInfinity, copy[1]!.GetValue<double>());
        Assert.Equal(double.NegativeInfinity, copy[2]!.GetValue<double>());
        Assert.True(float.IsNaN(copy[3]!.GetValue<float>()));
        Assert.Equal(int.MinValue, copy[4]!.GetValue<int>());
        Assert.Equal(long.MaxValue, copy[5]!.GetValue<long>());
        Assert.Equal(1.5m, copy[6]!.GetValue<decimal>());
        Assert.Equal((short)-2, copy[7]!.GetValue<short>());
        Assert.Equal((byte)3, copy[8]!.GetValue<byte>());
        Assert.Equal((sbyte)-4, copy[9]!.GetValue<sbyte>());
        Assert.Equal((ushort)5, copy[10]!.GetValue<ushort>());
        Assert.Equal(6u, copy[11]!.GetValue<uint>());
        Assert.Equal(ulong.MaxValue, copy[12]!.GetValue<ulong>());
        Assert.True(copy[13]!.GetValue<bool>());
        Assert.False(copy[14]!.GetValue<bool>());
        for (var i = 0; i < source.Count; i++)
        {
            Assert.NotSame(source[i], copy[i]);
        }
    }

    [Fact]
    public void String_leaves_are_mapped_in_document_order_and_null_becomes_json_null()
    {
        var source = JsonNode.Parse("{\"a\":\"1\",\"b\":[\"2\",{\"c\":\"3\"},4],\"d\":\"drop\",\"e\":\"5\"}");
        var seen = new List<string>();

        var copy = JsonTreeCopy.Copy(source, text =>
        {
            seen.Add(text);
            return text == "drop" ? null! : "[" + text + "]";
        });

        Assert.Equal("1,2,3,drop,5", string.Join(",", seen));
        Assert.Equal("{\"a\":\"[1]\",\"b\":[\"[2]\",{\"c\":\"[3]\"},4],\"d\":null,\"e\":\"[5]\"}", copy!.ToJsonString());
    }

    [Fact]
    public void Stepping_pauses_at_each_string_and_keeps_it_unless_replaced()
    {
        var copy = new JsonTreeCopy(JsonNode.Parse("[\"keep\",\"swap\",7]"));
        var seen = new List<string>();
        while (copy.MoveNext())
        {
            seen.Add(copy.Current);
            if (copy.Current == "swap")
            {
                copy.Replace("swapped");
            }
        }

        Assert.Equal("keep,swap", string.Join(",", seen));
        Assert.Equal("[\"keep\",\"swapped\",7]", copy.Result!.ToJsonString());
        Assert.False(copy.MoveNext());
    }

    [Fact]
    public void Scalar_and_null_roots_are_copied()
    {
        Assert.Null(JsonTreeCopy.Copy(null));
        Assert.Equal("\"[secret]\"", JsonTreeCopy.Copy(JsonValue.Create("secret"), t => "[" + t + "]")!.ToJsonString());
        Assert.Equal("\"x\"", JsonTreeCopy.Copy(JsonValue.Create('x'))!.ToJsonString());
        Assert.Equal("42", JsonTreeCopy.Copy(JsonValue.Create(42), t => "[" + t + "]")!.ToJsonString());
        Assert.True(double.IsNaN(JsonTreeCopy.Copy(JsonValue.Create(double.NaN))!.GetValue<double>()));
    }

    [Fact]
    public void Json_values_wrapping_containers_expand_up_to_the_JS_depth_limit()
    {
        var chain = new CopyChain { Leaf = "deep" };
        for (var i = 1; i < JsJson.MaxDepth; i++)
        {
            chain = new CopyChain { Next = chain };
        }

        var wrapped = JsonValue.Create(chain, JsonTreeCopyTestJsonContext.Default.CopyChain)!;

        var copy = JsonTreeCopy.Copy(wrapped, t => t.ToUpperInvariant());

        var node = copy;
        for (var i = 1; i < JsJson.MaxDepth; i++)
        {
            Assert.IsType<JsonObject>(node);
            node = node!["Next"];
        }

        Assert.Equal("DEEP", node!["Leaf"]!.GetValue<string>());
    }

    [Fact]
    public void Code_built_deep_trees_copy_on_a_small_stack()
    {
        const int depth = 100_000;
        JsonNode node = new JsonArray("leaf", 1);
        for (var i = 0; i < depth; i++)
        {
            node = i % 2 == 0 ? new JsonObject { ["n"] = node } : new JsonArray(node);
        }

        JsonNode? copy = null;
        var thread = new Thread(() => copy = JsonTreeCopy.Copy(node, t => t + "!"), 1024 * 1024);
        thread.Start();
        thread.Join();

        var current = copy;
        for (var i = 0; i < depth; i++)
        {
            current = current is JsonObject wrapper ? wrapper["n"] : ((JsonArray)current!)[0];
        }

        Assert.Equal("[\"leaf!\",1]", current!.ToJsonString());
    }

    [Fact]
    public void Copy_falls_back_for_case_insensitive_objects()
    {
        var source = JsonObject.Create(JsonDocument.Parse("{\"id\":1,\"ID\":2}").RootElement, new JsonNodeOptions { PropertyNameCaseInsensitive = true });

        var copy = (JsonObject)JsonTreeCopy.Copy(source)!;

        Assert.Equal("{\"id\":1,\"ID\":2}", copy.ToJsonString());
    }

    [Fact]
    public void Copy_keeps_lone_surrogates_in_parsed_strings()
    {
        var copy = JsonTreeCopy.Copy(JsonNode.Parse("{\"s\":\"\\ud800\"}"));

        Assert.Equal("\ud800", copy!["s"]!.GetValue<string>());
    }
}

internal sealed class CopyChain
{
    public string? Leaf { get; set; }

    public CopyChain? Next { get; set; }
}

[JsonSourceGenerationOptions(MaxDepth = JsJson.MaxDepth + 1)]
[JsonSerializable(typeof(CopyChain))]
internal sealed partial class JsonTreeCopyTestJsonContext : JsonSerializerContext;
