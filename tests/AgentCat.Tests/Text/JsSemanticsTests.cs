using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Text;

namespace AgentCat.Tests.Text;

public class JsSemanticsTests
{
    private static readonly char[] JsWhiteSpace =
    {
        '\u0009', '\u000A', '\u000B', '\u000C', '\u000D', '\u0020', '\u00A0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200A',
        '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF',
    };

    public static TheoryData<int> WhiteSpaceUnits()
    {
        var data = new TheoryData<int>();
        foreach (var c in JsWhiteSpace)
        {
            data.Add(c);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WhiteSpaceUnits))]
    public void Trim_removes_each_JS_whitespace_unit_from_both_ends(int unit)
    {
        var c = (char)unit;

        Assert.Equal("a" + c + "b", JsSemantics.Trim(c + "a" + c + "b" + c + c));
    }

    [Theory]
    [InlineData(0x0085)]
    [InlineData(0x180E)]
    [InlineData(0x200B)]
    [InlineData(0x200C)]
    [InlineData(0x200D)]
    [InlineData(0x2060)]
    [InlineData(0x001C)]
    [InlineData(0x001F)]
    [InlineData(0x0000)]
    [InlineData(0xFFFE)]
    public void Trim_keeps_units_JS_does_not_treat_as_whitespace(int unit)
    {
        var text = (char)unit + "x" + (char)unit;

        Assert.Equal(text, JsSemantics.Trim(text));
    }

    [Fact]
    public void Trim_strips_exactly_the_JS_whitespace_set_across_all_UTF16_units()
    {
        var expected = new HashSet<char>(JsWhiteSpace);
        var trimmed = new List<int>();
        for (var unit = 0; unit <= 0xFFFF; unit++)
        {
            var c = (char)unit;
            if (JsSemantics.Trim(c + "x" + c) == "x")
            {
                trimmed.Add(unit);
            }
        }

        Assert.Equal(expected.Select(c => (int)c).OrderBy(u => u).ToArray(), trimmed.ToArray());
    }

#if NET
    [Fact]
    public void JS_whitespace_set_is_Zs_plus_the_ECMAScript_extras()
    {
        var expected = new List<int> { 0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x2028, 0x2029, 0xFEFF };
        for (var unit = 0; unit <= 0xFFFF; unit++)
        {
            if (char.GetUnicodeCategory((char)unit) == System.Globalization.UnicodeCategory.SpaceSeparator)
            {
                expected.Add(unit);
            }
        }

        Assert.Equal(expected.OrderBy(u => u).ToArray(), JsWhiteSpace.Select(c => (int)c).OrderBy(u => u).ToArray());
    }
#endif

    [Theory]
    [InlineData("", "")]
    [InlineData(" \t\n\r ", "")]
    [InlineData("start", "start")]
    [InlineData("  start  ", "start")]
    [InlineData(" START\t", "START")]
    [InlineData("a b\u00A0c", "a b\u00A0c")]
    [InlineData("\u3000\uFEFFses_x\u2029", "ses_x")]
    [InlineData("\u0085start", "\u0085start")]
    public void Trim_matches_String_prototype_trim(string input, string expected)
    {
        Assert.Equal(expected, JsSemantics.Trim(input));
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("-0", false)]
    [InlineData("0.0", false)]
    [InlineData("0e10", false)]
    [InlineData("-0.000E-5", false)]
    [InlineData("1e-400", false)]
    [InlineData("1", true)]
    [InlineData("-1", true)]
    [InlineData("0.1", true)]
    [InlineData("1e400", true)]
    [InlineData("-1e400", true)]
    [InlineData("\"\"", false)]
    [InlineData("\" \"", true)]
    [InlineData("\"0\"", true)]
    [InlineData("\"false\"", true)]
    [InlineData("\"\\u0000\"", true)]
    [InlineData("\"\\ud800\"", true)]
    [InlineData("{}", true)]
    [InlineData("[]", true)]
    [InlineData("[0]", true)]
    [InlineData("{\"a\":null}", true)]
    public void IsTruthy_matches_JS_truthiness_of_parsed_JSON(string json, bool expected)
    {
        Assert.Equal(expected, JsSemantics.IsTruthy(JsonNode.Parse(json)));
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("0", false)]
    [InlineData("\"\"", false)]
    [InlineData("\"x\"", true)]
    [InlineData("{}", true)]
    public void IsTruthy_holds_for_nested_values(string json, bool expected)
    {
        var parent = JsonNode.Parse("{\"v\":" + json + "}")!.AsObject();

        Assert.Equal(expected, JsSemantics.IsTruthy(parent["v"]));
    }

    [Fact]
    public void IsTruthy_is_false_for_a_missing_node()
    {
        Assert.False(JsSemantics.IsTruthy(null));
        Assert.False(JsSemantics.IsTruthy(new JsonObject()["missing"]));
    }

    [Fact]
    public void IsTruthy_handles_values_created_in_code()
    {
        Assert.False(JsSemantics.IsTruthy(JsonValue.Create(0)));
        Assert.False(JsSemantics.IsTruthy(JsonValue.Create(0L)));
        Assert.False(JsSemantics.IsTruthy(JsonValue.Create(0.0)));
        Assert.False(JsSemantics.IsTruthy(JsonValue.Create(-0.0)));
        Assert.False(JsSemantics.IsTruthy(JsonValue.Create(0m)));
        Assert.False(JsSemantics.IsTruthy(JsonValue.Create(0f)));
        Assert.False(JsSemantics.IsTruthy(JsonValue.Create(string.Empty)));
        Assert.False(JsSemantics.IsTruthy(JsonValue.Create(false)));
        Assert.True(JsSemantics.IsTruthy(JsonValue.Create(2)));
        Assert.True(JsSemantics.IsTruthy(JsonValue.Create(0.5)));
        Assert.True(JsSemantics.IsTruthy(JsonValue.Create(double.Epsilon)));
        Assert.True(JsSemantics.IsTruthy(JsonValue.Create("x")));
        Assert.True(JsSemantics.IsTruthy(JsonValue.Create(true)));
        Assert.True(JsSemantics.IsTruthy(JsonValue.Create('\0')));
        Assert.True(JsSemantics.IsTruthy(new JsonObject()));
        Assert.True(JsSemantics.IsTruthy(new JsonArray()));
    }

    [Fact]
    public void IsTruthy_reads_element_backed_values()
    {
        using var document = JsonDocument.Parse("[0, \"\", 7, \"y\"]");
        var items = document.RootElement.EnumerateArray().Select(e => JsonValue.Create(e)).ToArray();

        Assert.False(JsSemantics.IsTruthy(items[0]));
        Assert.False(JsSemantics.IsTruthy(items[1]));
        Assert.True(JsSemantics.IsTruthy(items[2]));
        Assert.True(JsSemantics.IsTruthy(items[3]));
    }

    [Fact]
    public void IsTruthy_does_not_decode_strings()
    {
        var json = System.Text.Encoding.UTF8.GetBytes("{\"v\":\"?\",\"w\":\"\\ud800\"}");
        json[Array.IndexOf(json, (byte)'?')] = 0xFF;
        var node = JsonNode.Parse(json)!.AsObject();

        Assert.True(JsSemantics.IsTruthy(node["v"]));
        Assert.True(JsSemantics.IsTruthy(node["w"]));
    }

    [Fact]
    public void ToJsString_renders_values_as_JS_String_does()
    {
        var array = new JsonArray(
            1,
            new JsonArray(2, null),
            new JsonArray(),
            new JsonArray(new JsonArray(3)),
            new JsonObject { ["a"] = 1 },
            true,
            "x",
            null,
            double.NaN,
            double.NegativeInfinity);

        Assert.Equal("1,2,,,3,[object Object],true,x,,NaN,-Infinity", JsSemantics.ToJsString(array));
        Assert.Equal("null", JsSemantics.ToJsString(null));
        Assert.Equal("[object Object]", JsSemantics.ToJsString(new JsonObject()));
        Assert.Equal(string.Empty, JsSemantics.ToJsString(new JsonArray(new JsonArray())));
        Assert.Equal(",", JsSemantics.ToJsString(new JsonArray(new JsonArray(), null)));
        Assert.Equal("Infinity", JsSemantics.ToJsString(JsonValue.Create(float.PositiveInfinity)));
        Assert.Equal("false", JsSemantics.ToJsString(JsonValue.Create(false)));
        Assert.Equal("1.5", JsSemantics.ToJsString(JsonNode.Parse("1.5")));
        Assert.Equal("video", JsSemantics.ToJsString(JsonNode.Parse("\"video\"")));
    }

    [Fact]
    public void ToJsJoinElement_renders_null_as_empty()
    {
        Assert.Equal(string.Empty, JsSemantics.ToJsJoinElement(null));
        Assert.Equal("a", JsSemantics.ToJsJoinElement(JsonValue.Create("a")));
        Assert.Equal("1,", JsSemantics.ToJsJoinElement(new JsonArray(1, null)));
    }

    [Fact]
    public void ToJsString_flattens_code_built_deep_arrays_on_a_small_stack()
    {
        JsonNode node = new JsonArray("leaf", 1);
        for (var i = 0; i < 100_000; i++)
        {
            node = new JsonArray(node, i % 2 == 0 ? null : "x");
        }

        var text = string.Empty;
        var thread = new Thread(() => text = JsSemantics.ToJsString(node), 1024 * 1024);
        thread.Start();
        thread.Join();

        Assert.StartsWith("leaf,1,,x,,x", text, StringComparison.Ordinal);
        Assert.Equal(6 + (50_000 * 2) + (50_000 * 1), text.Length);
    }
}
