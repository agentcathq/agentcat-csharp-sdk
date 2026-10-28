using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Internal.Json;
using AgentCat.Internal.Telemetry;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Telemetry;

public class PayloadWriterTests
{
    private static readonly string LineSeparator = ((char)0x2028).ToString();
    private static readonly string LoneSurrogate = ((char)0xD800).ToString();

    private static string Text(PayloadWriter writer) => Encoding.UTF8.GetString(writer.ToArray());

    [Fact]
    public void Every_value_kind_matches_JsJson()
    {
        const string text = "a \"quoted\" \\ line\n\ttab";
        var date = new DateTimeOffset(2025, 6, 15, 14, 0, 5, 123, TimeSpan.FromHours(2));
        var tree = new JsonObject
        {
            ["text"] = text,
            ["long"] = JsonValue.Create(-9007199254740993L),
            ["double"] = JsonValue.Create(0.1 + 0.2),
            ["whole double"] = JsonValue.Create(5.0),
            ["true"] = true,
            ["false"] = false,
            ["null"] = null,
            ["date"] = JsonValue.Create(date),
            ["object"] = new JsonObject { ["nested"] = new JsonArray(1, "two", null, new JsonArray()) },
            ["empty"] = new JsonObject(),
            ["control " + (char)1] = "x",
        };

        var writer = new PayloadWriter();
        writer.StartObject();
        writer.Property("text");
        writer.String(text);
        writer.Property("long");
        writer.Number(-9007199254740993L);
        writer.Property("double");
        writer.Number(0.1 + 0.2);
        writer.Property("whole double");
        writer.Number(5.0);
        writer.Property("true");
        writer.Bool(true);
        writer.Property("false");
        writer.Bool(false);
        writer.Property("null");
        writer.Node(null);
        writer.Property("date");
        writer.Date(date);
        writer.Property("object");
        writer.StartObject();
        writer.Property("nested");
        writer.StartArray();
        writer.Number(1);
        writer.String("two");
        writer.String(null);
        writer.StartArray();
        writer.EndArray();
        writer.EndArray();
        writer.EndObject();
        writer.Property("empty");
        writer.StartObject();
        writer.EndObject();
        writer.Property("control " + (char)1);
        writer.String("x");
        writer.EndObject();

        Assert.Equal(JsJson.ToUtf8(tree), writer.ToArray());
        Assert.Equal(
            "{\"text\":\"a \\\"quoted\\\" \\\\ line\\n\\ttab\",\"long\":-9007199254740993,\"double\":0.30000000000000004,"
            + "\"whole double\":5,\"true\":true,\"false\":false,\"null\":null,\"date\":\"2025-06-15T12:00:05.123Z\","
            + "\"object\":{\"nested\":[1,\"two\",null,[]]},\"empty\":{},\"control \\" + "u0001\":\"x\"}",
            Text(writer));
    }

    [Fact]
    public void Non_ASCII_and_html_characters_are_written_as_they_are()
    {
        var text = "Zürich <b>&'" + LineSeparator + " \U0001F600 ✓";
        var lone = "lone " + LoneSurrogate;

        var writer = new PayloadWriter();
        writer.StartArray();
        writer.String(text);
        writer.String(lone);
        writer.EndArray();

        Assert.Equal(JsJson.ToUtf8(new JsonArray(text, lone)), writer.ToArray());
        Assert.Equal("[\"" + text + "\",\"lone \\" + "ud800\"]", Text(writer));
    }

    [Theory]
    [InlineData(0.25, "0.25")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(123456789012345680000.0, "123456789012345680000")]
    [InlineData(-0.0, "0")]
    [InlineData(double.NaN, "null")]
    [InlineData(double.PositiveInfinity, "null")]
    [InlineData(1749988805.123, "1749988805.123")]
    [InlineData(1749988804.9729998, "1749988804.9729998")]
    public void Doubles_use_ECMAScript_number_text(double value, string expected)
    {
        var writer = new PayloadWriter();
        writer.Number(value);

        Assert.Equal(expected, Text(writer));
        Assert.Equal(JsJson.ToUtf8(JsonValue.Create(value)), writer.ToArray());
    }

    // Sentry's start_timestamp in golden-output.txt: the end in seconds minus the duration in seconds.
    [Fact]
    public void A_double_computed_as_TS_does_matches_the_fixture()
    {
        var writer = new PayloadWriter();
        writer.Number((1749988805123 / 1000.0) - (150 / 1000.0));

        Assert.Equal("1749988804.9729998", Text(writer));
        Assert.Contains("\"start_timestamp\":1749988804.9729998,", Fixtures.Text("exporters/golden-output.txt"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(1749988805123L, "1749988805123")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    [InlineData(long.MaxValue, "9223372036854775807")]
    public void Longs_are_written_in_full(long value, string expected)
    {
        var writer = new PayloadWriter();
        writer.Number(value);

        Assert.Equal(expected, Text(writer));
    }

    [Fact]
    public void Dates_are_written_as_toISOString()
    {
        var writer = new PayloadWriter();
        writer.StartArray();
        writer.Date(new DateTimeOffset(2026, 10, 16, 15, 6, 0, 515, TimeSpan.Zero));
        writer.Date(new DateTimeOffset(2025, 6, 15, 7, 0, 5, 123, TimeSpan.FromHours(-5)).AddTicks(9999));
        writer.EndArray();

        Assert.Equal("[\"2026-10-16T15:06:00.515Z\",\"2025-06-15T12:00:05.123Z\"]", Text(writer));
    }

    [Fact]
    public void Node_writes_an_event_tree_without_attaching_it()
    {
        const string json = "{\"city\":\"Zürich\",\"n\":[1,2.5,{\"deep\":true}]}";
        var tree = JsonNode.Parse(json)!;
        var child = tree["n"]!;
        var before = JsJson.ToUtf8(tree);

        var first = new PayloadWriter();
        first.StartObject();
        first.Property("parameters");
        first.Node(tree);
        first.Property("again");
        first.Node(tree);
        first.EndObject();
        var second = new PayloadWriter();
        second.StartArray();
        second.Node(tree);
        second.Node(child);
        second.EndArray();

        Assert.Null(tree.Parent);
        Assert.Same(tree, child.Parent);
        Assert.Equal(before, JsJson.ToUtf8(tree));
        Assert.Equal("{\"parameters\":" + json + ",\"again\":" + json + "}", Text(first));
        Assert.Equal("[" + json + ",[1,2.5,{\"deep\":true}]]", Text(second));
    }

    [Fact]
    public void Node_writes_code_built_values_as_JsJson_does()
    {
        var tree = new JsonObject
        {
            ["when"] = JsonValue.Create(new DateTimeOffset(2025, 6, 15, 12, 0, 5, 123, TimeSpan.Zero)),
            ["ratio"] = JsonValue.Create(1e21),
            ["text"] = "Zürich " + LoneSurrogate,
        };

        var writer = new PayloadWriter();
        writer.StartArray();
        writer.Node(tree);
        writer.EndArray();

        Assert.Equal("[" + Encoding.UTF8.GetString(JsJson.ToUtf8(tree)) + "]", Text(writer));
        Assert.Null(tree.Parent);
    }
}
