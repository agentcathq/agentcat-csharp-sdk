using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentCat.Internal.Json;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Json;

public class JsJsonTests
{
    private const string NestingMessage = "JSON nesting exceeds 1000 levels.";

    public static TheoryData<int> StringVectors => Indexes("strings");

    public static TheoryData<int> NumberVectors => Indexes("numbers");

    public static TheoryData<int> StructureVectors => Indexes("structures");

    private static JsonArray Vectors(string group) => Fixtures.Json("json/stringify-vectors.json")[group]!.AsArray();

    private static TheoryData<int> Indexes(string group)
    {
        var indexes = new TheoryData<int>();
        for (var i = 0; i < Vectors(group).Count; i++)
        {
            indexes.Add(i);
        }

        return indexes;
    }

    private static JsonElement Parse(byte[] utf8)
        => JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 10_000 }).RootElement;

    private static JsonElement Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

    private static byte[] Hex(string hex)
    {
        var parts = hex.Split(' ');
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            bytes[i] = Convert.ToByte(parts[i], 16);
        }

        return bytes;
    }

    private static string Nested(int depth) => new string('[', depth) + new string(']', depth);

    private static JsonNode NestedArrays(int depth)
    {
        JsonNode node = new JsonArray();
        for (var i = 1; i < depth; i++)
        {
            node = new JsonArray(node);
        }

        return node;
    }

    // The tree JSON.parse builds, with strings and keys read leniently: JsonNode.Parse throws on a lone-surrogate key.
    private static JsonNode? CodeBuilt(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    var name = Parse("\"" + Encoding.UTF8.GetString(JsonMarshal.GetRawUtf8PropertyName(property).ToArray()) + "\"");
                    obj[JsJson.ReadString(name)] = CodeBuilt(property.Value);
                }

                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(CodeBuilt(item));
                }

                return array;
            case JsonValueKind.String:
                return JsonValue.Create(JsJson.ReadString(element));
            case JsonValueKind.Number:
                return JsonValue.Create(element.GetDouble());
            case JsonValueKind.True:
            case JsonValueKind.False:
                return JsonValue.Create(element.GetBoolean());
            default:
                return null;
        }
    }

    private static string Written(JsonNode? node) => Encoding.UTF8.GetString(JsJson.ToUtf8(node));

    private static void AssertWrites(byte[] expected, JsonNode? node)
    {
        Assert.Equal(expected, JsJson.ToUtf8(node));
        Assert.Equal(expected.Length, JsJson.Length(node));
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
            throw failure;
        }

        return result!;
    }

    [Theory]
    [MemberData(nameof(StringVectors))]
    public void Strings_match_JSON_stringify(int index)
    {
        var vector = Vectors("strings")[index]!;
        var json = (string)vector["json"]!;
        var expected = Encoding.UTF8.GetBytes((string)vector["expected"]!);

        AssertWrites(expected, JsonValue.Create(JsJson.ReadString(Parse(json))));
        AssertWrites(expected, JsonNode.Parse(json));
    }

    [Theory]
    [MemberData(nameof(NumberVectors))]
    public void Numbers_match_JSON_stringify(int index)
    {
        var vector = Vectors("numbers")[index]!;
        var bits = long.Parse((string)vector["bits"]!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var expected = Encoding.UTF8.GetBytes((string)vector["expected"]!);

        AssertWrites(expected, JsonValue.Create(BitConverter.Int64BitsToDouble(bits)));
    }

    [Theory]
    [MemberData(nameof(StructureVectors))]
    public void Structures_match_JSON_stringify(int index)
    {
        var vector = Vectors("structures")[index]!;
        var element = Parse((string)vector["json"]!);
        var expected = Encoding.UTF8.GetBytes((string)vector["expected"]!);

        AssertWrites(expected, CodeBuilt(element));
        Assert.Equal(expected.Length, JsJson.Length(element));
    }

    [Fact]
    public void Parsed_and_code_built_trees_are_compact_in_node_order()
    {
        const string json = "{ \"a\" : [ 1 , null , { \"b\" : \"c\" } ] , \"e\" : { } , \"f\" : [ ] , \"z\" : null }";
        var codeBuilt = new JsonObject
        {
            ["a"] = new JsonArray(1, null, new JsonObject { ["b"] = "c" }),
            ["e"] = new JsonObject(),
            ["f"] = new JsonArray(),
            ["z"] = null,
        };
        var expected = Encoding.UTF8.GetBytes("{\"a\":[1,null,{\"b\":\"c\"}],\"e\":{},\"f\":[],\"z\":null}");

        AssertWrites(expected, JsonNode.Parse(json));
        AssertWrites(expected, codeBuilt);
        AssertWrites(Encoding.UTF8.GetBytes("null"), null);
    }

    [Fact]
    public void Case_insensitive_objects_keep_keys_that_differ_only_in_case()
    {
        const string json = "{\"id\":1,\"ID\":{\"k\":\"\\u00e9\",\"K\":[]}}";
        var obj = JsonObject.Create(Parse(json), new JsonNodeOptions { PropertyNameCaseInsensitive = true })!;

        AssertWrites(Encoding.UTF8.GetBytes("[{\"id\":1,\"ID\":{\"k\":\"é\",\"K\":[]}}]"), new JsonArray(obj));
    }

    [Fact]
    public void Element_numbers_keep_source_text()
    {
        Assert.Equal("[1.0,1e2,-0]", Written(JsonNode.Parse("[1.0,1e2,-0]")));
    }

    [Fact]
    public void Code_built_values_follow_JS()
    {
        var utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        Assert.Equal("1.100000023841858", Written(JsonValue.Create(1.1f)));
        Assert.Equal("9223372036854775807", Written(JsonValue.Create(long.MaxValue)));
        Assert.Equal("18446744073709551615", Written(JsonValue.Create(ulong.MaxValue)));
        Assert.Equal("-32768", Written(JsonValue.Create(short.MinValue)));
        Assert.Equal("1.50", Written(JsonValue.Create(1.50m)));
        Assert.Equal("\"2026-01-02T03:04:05.000Z\"", Written(JsonValue.Create(utc)));
        Assert.Equal("\"2026-01-02T03:04:05.000Z\"", Written(JsonValue.Create(utc.ToLocalTime())));
        Assert.Equal("\"2026-01-02T03:04:05.123Z\"", Written(JsonValue.Create(new DateTime(2026, 1, 2, 3, 4, 5, 123))));
        Assert.Equal(
            "\"2026-01-02T03:04:05.000Z\"",
            Written(JsonValue.Create(new DateTimeOffset(2026, 1, 2, 5, 4, 5, TimeSpan.FromHours(2)))));
        Assert.Equal(
            "\"0f8fad5b-d9cb-469f-a165-70867728950e\"",
            Written(JsonValue.Create(new Guid("0F8FAD5B-D9CB-469F-A165-70867728950E"))));
        Assert.Equal("\"x\"", Written(JsonValue.Create('x')));
        Assert.Equal("\"\\\"\"", Written(JsonValue.Create('"')));
        Assert.Equal("\"\\ud800\"", Written(JsonValue.Create('\ud800')));
        Assert.Equal("true", Written(JsonValue.Create(true)));
        Assert.Equal("false", Written(JsonValue.Create(false)));
        Assert.Equal("[null,null,null]", Written(new JsonArray(float.NaN, float.PositiveInfinity, double.NegativeInfinity)));
    }

    [Fact]
    public void Other_values_are_written_by_their_type_info_then_the_element_rule()
    {
        var value = JsonValue.Create(new Labeled { Label = "é<\ud83d\ude00", Score = 1.0 }, JsJsonTestJsonContext.Default.Labeled);

        Assert.Equal("{\"Label\":\"é<\ud83d\ude00\",\"Score\":1}", Written(value));
    }

    [Fact]
    public void Nesting_past_1000_levels_throws_without_overflow()
    {
        Assert.Equal(2000, JsJson.Length(NestedArrays(1_000)));
        Assert.Equal(2000, JsJson.ToUtf8(NestedArrays(1_000)).Length);

        foreach (var depth in new[] { 1_001, 100_000 })
        {
            var node = NestedArrays(depth);

            Assert.Equal(NestingMessage, OnSmallStack(() => Assert.Throws<InvalidOperationException>(() => JsJson.ToUtf8(node))).Message);
            Assert.Equal(NestingMessage, OnSmallStack(() => Assert.Throws<InvalidOperationException>(() => JsJson.Length(node))).Message);
        }

        var element = Parse(Nested(1_001));
        Assert.Equal(NestingMessage, OnSmallStack(() => Assert.Throws<InvalidOperationException>(() => JsJson.Length(element))).Message);
    }

    [Fact]
    public void ReadString_keeps_a_lone_surrogate_like_JSON_parse()
    {
        Assert.Equal(" a\uD800b ", JsJson.ReadString(Parse("\" a\\ud800b \"")));
        Assert.Equal("\uDC00", JsJson.ReadString(Parse("\"\\udc00\"")));
        Assert.Equal(
            "x\n\"\\/\b\f\r\t\uD83D\uDE00\u00E9y\uD800",
            JsJson.ReadString(Parse("\"x\\n\\\"\\\\\\/\\b\\f\\r\\t\\ud83d\\ude00\u00E9y\\ud800\"")));
        Assert.Equal(" ab\n\uD800", JsJson.ReadString(Parse("\"\\u0020a\\u0062\\n\\ud800\"")));
        Assert.Equal("\t a b\uDBFF\u3000", JsJson.ReadString(Parse("\"\\t a\\u0020b\\udbff\\u3000\"")));
    }

    [Fact]
    public void ReadString_replaces_invalid_UTF8_like_a_JS_decoder()
    {
        var json = Encoding.UTF8.GetBytes("\"a?b\\u0041\"");
        json[Array.IndexOf(json, (byte)'?')] = 0xFF;

        Assert.Equal("a\uFFFDbA", JsJson.ReadString(Parse(json)));
        Assert.Equal("\uFFFD\uFFFD\uFFFDx", JsJson.ReadString(Parse(Hex("22 ED A0 BD 78 22"))));
        Assert.Equal("\uFFFD", JsJson.ReadString(Parse(Hex("22 F0 9F 98 22"))));
    }

    [Theory]
    [InlineData("\"hello\"", 7)]
    [InlineData("\"café\"", 7)]
    [InlineData("\"<a>&</a>\"", 10)]
    [InlineData("\"你好\"", 8)]
    [InlineData("\"\U0001F600\"", 6)]
    [InlineData("\"\\ud83d\\ude00\"", 6)]
    [InlineData("\"\\ud83d\"", 8)]
    [InlineData("\"\\ude00x\"", 9)]
    [InlineData("\"\\ude00\\ud83d\"", 14)]
    [InlineData("\"\\ud83dx\"", 9)]
    [InlineData("\"\\ud83d\\u0041\"", 9)]
    [InlineData("\"\\ud83d\\ud83d\\ude00\"", 12)]
    [InlineData("\"a\\\"b\\\\c\\/d\"", 11)]
    [InlineData("\"\\b\\f\\n\\r\\t\"", 12)]
    [InlineData("\"\\u0000\\u001f\\u000b\\u001b\"", 26)]
    [InlineData("\"\\u007f\"", 3)]
    [InlineData("\"\\u2028\\u2029\"", 8)]
    [InlineData("\"\\u0041\\u00e9\\u4f60\"", 8)]
    [InlineData("\"\\\"\"", 4)]
    [InlineData("\"\"", 2)]
    [InlineData("\"\\u005c\\u0022\\u002F\"", 7)]
    [InlineData("0", 1)]
    [InlineData("-1", 2)]
    [InlineData("1.5", 3)]
    [InlineData("1e-7", 4)]
    [InlineData("1.2345678901234569e+23", 22)]
    [InlineData("true", 4)]
    [InlineData("false", 5)]
    [InlineData("null", 4)]
    [InlineData("[]", 2)]
    [InlineData("{}", 2)]
    [InlineData("[1,2,3]", 7)]
    [InlineData("{ \"a\" : [ 1 , 2 ] }", 11)]
    [InlineData("{\"nested\":{\"a\":[{\"b\":[]},{}],\"c\":\"\"}}", 37)]
    [InlineData("{\"k\\n\\\"ey\":1}", 13)]
    [InlineData("{\"\\u0041\":1}", 7)]
    [InlineData("{\"\\ud83d\":1}", 12)]
    [InlineData("{\"é\":1}", 8)]
    [InlineData("[[[]],[{}],null]", 16)]
    public void Matches_JSON_stringify_byte_length(string json, long bytes)
    {
        Assert.Equal(bytes, JsJson.Length(Parse(json)));
    }

    [Theory]
    [InlineData("22 FF C3 22", 8)]
    [InlineData("22 ED A0 BD 78 22", 12)]
    [InlineData("22 F0 9F 98 22", 5)]
    [InlineData("22 C0 80 22", 8)]
    [InlineData("22 E0 80 80 22", 11)]
    [InlineData("22 F4 90 80 80 22", 14)]
    [InlineData("22 C3 A9 E4 BD A0 F0 9F 98 80 ED 9F BF 22", 14)]
    [InlineData("22 80 BF 22", 8)]
    [InlineData("22 F0 9F 78 22", 6)]
    [InlineData("22 E0 A0 22", 5)]
    [InlineData("22 F5 22", 5)]
    [InlineData("22 C2 22", 5)]
    [InlineData("7B 22 FF 22 3A 31 7D", 9)]
    public void Invalid_utf8_counts_like_decoded_replacement_characters(string hex, long bytes)
    {
        Assert.Equal(bytes, JsJson.Length(Parse(Hex(hex))));
    }

    [Fact]
    public void Dictionary_keys_are_escaped_like_JSON_stringify()
    {
        var properties = new Dictionary<string, JsonElement>
        {
            ["k\n\"ey"] = Parse("1"),
            ["\ud83d"] = Parse("\"\U0001F600\""),
            ["é"] = Parse("[]"),
        };

        Assert.Equal(37, JsJson.LengthOfObject(properties));
    }

    [Fact]
    public void Nesting_deeper_than_1000_levels_throws()
    {
        Assert.Equal(2000, JsJson.Length(Parse(Nested(1_000))));
        Assert.Throws<InvalidOperationException>(() => JsJson.Length(Parse(Nested(1_001))));
    }

    [Fact]
    public void Undefined_element_throws()
    {
        Assert.Throws<ArgumentException>(() => JsJson.Length(default(JsonElement)));
    }
}

internal sealed class Labeled
{
    public string? Label { get; set; }

    public double Score { get; set; }
}

[JsonSerializable(typeof(Labeled))]
internal sealed partial class JsJsonTestJsonContext : JsonSerializerContext;
