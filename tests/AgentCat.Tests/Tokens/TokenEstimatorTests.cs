using System.Collections;
using System.Text;
using System.Text.Json;
using AgentCat.Internal.Tokens;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol.Protocol;

namespace AgentCat.Tests.Tokens;

public class TokenEstimatorTests
{
    private static Dictionary<string, JsonElement> Args(string json)
    {
        var root = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 10_000 }).RootElement;
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            args[property.Name] = property.Value;
        }

        return args;
    }

    private static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement;

    private static string Nested(int depth) => "{\"d\":" + new string('[', depth) + "1" + new string(']', depth) + "}";

    private static string Text(string text) => "{\"type\":\"text\",\"text\":\"" + text + "\"}";

    [Fact]
    public void Pins_the_divisor()
    {
        Assert.Equal(3.5, TokenEstimator.BytesPerToken);
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(1L, 1)]
    [InlineData(7L, 2)]
    [InlineData(13L, 4)]
    [InlineData(4096L, 1171)]
    [InlineData(7516192765L, 2147483647)]
    [InlineData(7516192761L, 2147483646)]
    [InlineData(7516192762L, 2147483647)]
    [InlineData(-1L, 0)]
    [InlineData(long.MaxValue, 2147483647)]
    public void Bytes_to_tokens(long bytes, int tokens)
    {
        Assert.Equal(tokens, TokenEstimator.EstimateTokens(bytes));
    }

    [Fact]
    public void Utf8_byte_length_matches_TextEncoder()
    {
        var vectors = new (string Text, long Bytes)[]
        {
            ("", 0),
            ("hello", 5),
            ("café", 5),
            ("你好", 6),
            ("\U0001F600", 4),
            ("\ud83d", 3),
            ("\ude00", 3),
            ("\ude00\ud83d", 6),
            ("a\ud83db", 5),
        };

        foreach (var (text, bytes) in vectors)
        {
            Assert.Equal(bytes, TokenEstimator.Utf8ByteLength(text));
            Assert.Equal(bytes, Encoding.UTF8.GetByteCount(text));
        }
    }

    [Theory]
    [InlineData("{\"q\":\"hello\"}", 4)]
    [InlineData("{}", 1)]
    [InlineData("{\"name\":\"café\"}", 5)]
    [InlineData("{\"html\":\"<a>&</a>\"}", 6)]
    [InlineData("{\"t\":\"你好\"}", 4)]
    [InlineData("{\"ids\":[1,2,3],\"opts\":{\"deep\":true,\"n\":null}}", 13)]
    [InlineData("{\"nested\":{\"a\":[{\"b\":[]},{}],\"c\":\"\"}}", 11)]
    [InlineData("{ \"spaced\" : [ 1 , 2 ] }", 5)]
    [InlineData("{\"text\":\"hi there\"}", 6)]
    [InlineData("{\"text\":\"secret-value-123456789\"}", 10)]
    public void Raw_arguments(string json, int tokens)
    {
        Assert.Equal(tokens, TokenEstimator.EstimateInputTokens(Args(json)));
    }

    [Fact]
    public void Absent_arguments_omit_the_field()
    {
        Assert.Null(TokenEstimator.EstimateInputTokens(null));
    }

    [Fact]
    public void Emoji_counts_four_bytes()
    {
        Assert.Equal(4, TokenEstimator.EstimateInputTokens(Args("{\"e\":\"\U0001F600\"}")));
        Assert.Equal(4, TokenEstimator.EstimateInputTokens(Args("{\"e\":\"\\ud83d\\ude00\"}")));
    }

    [Theory]
    [InlineData("{\"c\":\"a\\\"b\\\\c\\/d\\b\\f\\n\\r\\t\\u0000\\u001f\\u0001 \\u007f\"}", 14)]
    [InlineData("{\"ctl\":\"\\u0008\\u000c\\u000a\\u000d\\u0009\\u000b\\u001b\"}", 10)]
    [InlineData("{\"k\\n\\\"ey\":1}", 4)]
    [InlineData("{\"o\":{\"k\\n\\\"ey\":1}}", 6)]
    public void Control_and_quote_characters_count_as_escaped(string json, int tokens)
    {
        Assert.Equal(tokens, TokenEstimator.EstimateInputTokens(Args(json)));
    }

    [Fact]
    public void Lone_surrogates_count_as_escaped()
    {
        Assert.Equal(16, TokenEstimator.EstimateInputTokens(Args("{\"lone\":\"\\ud83d\", \"low\":\"\\ude00x\", \"rev\":\"\\ude00\\ud83d\"}")));
        Assert.Equal(4, TokenEstimator.EstimateInputTokens(new Dictionary<string, JsonElement> { ["\ud83d"] = Value("1") }));
    }

    [Fact]
    public void Line_separators_slashes_and_html_stay_unescaped()
    {
        Assert.Equal(6, TokenEstimator.EstimateInputTokens(Args("{\"s\":\"\\u2028\\u2029/<>&'\"}")));
    }

    [Fact]
    public void Unicode_escapes_count_as_the_characters_they_encode()
    {
        Assert.Equal(5, TokenEstimator.EstimateInputTokens(Args("{\"esc\":\"\\u0041\\u00e9\\u4f60\"}")));
    }

    [Fact]
    public void Canonical_numbers_match_JSON_stringify()
    {
        Assert.Equal(16, TokenEstimator.EstimateInputTokens(Args("{\"n\":[0,-1,1.5,100,1e-7,1.2345678901234569e+23,-2.5e-8]}")));
    }

    [Fact]
    public void Numbers_count_their_raw_json_text()
    {
        Assert.Equal(3, TokenEstimator.EstimateInputTokens(Args("{\"n\":1.0}")));
    }

    [Fact]
    public void Undefined_values_are_omitted_like_JS_undefined()
    {
        var args = new Dictionary<string, JsonElement> { ["a"] = default, ["b"] = Value("1") };
        Assert.Equal(2, TokenEstimator.EstimateInputTokens(args));
    }

    [Fact]
    public void Unreadable_arguments_omit_the_field()
    {
        var document = JsonDocument.Parse("{\"q\":\"hello\"}");
        var args = new Dictionary<string, JsonElement> { ["q"] = document.RootElement.GetProperty("q") };
        document.Dispose();

        Assert.Null(TokenEstimator.EstimateInputTokens(args));
    }

    [Fact]
    public void Deeply_nested_arguments_do_not_throw()
    {
        Assert.Null(TokenEstimator.EstimateInputTokens(Args(Nested(5_000))));
    }

    [Fact]
    public void Nesting_is_measured_up_to_1000_levels()
    {
        Assert.Equal(573, TokenEstimator.EstimateInputTokens(Args(Nested(999))));
        Assert.Null(TokenEstimator.EstimateInputTokens(Args(Nested(1_000))));
    }

#if NET
    [Fact]
    public void Large_arguments_are_measured_without_copying()
    {
        var args = Args("{\"s\":\"" + new string('x', 1_000_000) + "\"}");
        TokenEstimator.EstimateInputTokens(args);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var tokens = TokenEstimator.EstimateInputTokens(args);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(285717, tokens);
        Assert.True(allocated < 100_000, "allocated " + allocated + " bytes");
    }
#endif

    public static TheoryData<string, int> OutputVectors => new()
    {
        { "{\"content\":[" + Text("hello world") + "]}", 4 },
        { "{\"content\":[" + Text("abcd") + "," + Text("e") + "]}", 2 },
        { "{\"content\":[" + Text("") + "]}", 0 },
        { "{\"content\":[{\"type\":\"image\",\"data\":\"QUJD\",\"mimeType\":\"image/png\"}]}", 0 },
        { "{\"content\":[{\"type\":\"resource\",\"resource\":{\"uri\":\"file:///a\",\"text\":\"resource body\"}}]}", 4 },
        { "{\"content\":[{\"type\":\"resource\",\"resource\":{\"uri\":\"file:///a\",\"blob\":\"QUJD\"}}]}", 0 },
        { "{\"content\":[" + Text("hi") + "],\"structuredContent\":{\"big\":\"" + new string('y', 1000) + "\"}}", 1 },
        { "{\"content\":[" + Text(new string('x', 4096)) + "]}", 1171 },
        { "{\"content\":[" + Text("hi") + "],\"isError\":true}", 1 },
        { "{\"content\":[],\"structuredContent\":{\"result\":\"ok\"}}", 5 },
        { "{\"content\":[]}", 0 },
        { "{\"content\":[],\"structuredContent\":null}", 0 },
        { "{\"content\":[{\"type\":\"image\",\"data\":\"QUJD\",\"mimeType\":\"image/png\"}],\"structuredContent\":{\"result\":\"ok\"}}", 0 },
        { "{\"content\":[" + Text("a") + ",{\"type\":\"resource\",\"resource\":{\"uri\":\"file:///a\",\"text\":\"你好\"}},{\"type\":\"image\",\"data\":\"QUJD\",\"mimeType\":\"image/png\"}]}", 2 },
        { "{\"content\":[],\"structuredContent\":{\"msg\":\"<b>café</b> \U0001F600\\n\"}}", 9 },
        { "{\"content\":[],\"structuredContent\":[1,2]}", 2 },
        { "{\"content\":[],\"structuredContent\":\"str\"}", 2 },
        { "{\"content\":[],\"structuredContent\":0}", 1 },
        { "{\"content\":[],\"structuredContent\":false}", 2 },
        { "{\"content\":[null," + Text("abcd") + "]}", 2 },
        { "{\"isError\":true,\"content\":[" + Text("boom!") + "]}", 2 },
        { "{\"isError\":true,\"content\":[" + Text("no") + "]}", 1 },
        { "{\"content\":[" + Text("secret-value-123456789") + "]}", 7 },
        { "{\"content\":[" + Text(new string('x', 40_000)) + "]}", 11429 },
    };

    [Theory]
    [MemberData(nameof(OutputVectors))]
    public void Content_blocks(string json, int tokens)
    {
        Assert.Equal(tokens, TokenEstimator.EstimateOutputTokens(Mcp.Result(json)));
    }

    [Fact]
    public void Text_blocks_count_utf8_bytes_not_json_escapes()
    {
        var result = Mcp.Result("{\"content\":[" + Text("x") + "]}");
        ((TextContentBlock)result.Content[0]).Text = "\U0001F600\ud83d\n";

        Assert.Equal(3, TokenEstimator.EstimateOutputTokens(result));
    }

    [Theory]
    [InlineData("{\"content\":null,\"structuredContent\":{\"result\":\"ok\"}}", 11)]
    [InlineData("{\"content\":null,\"structuredContent\":{\"result\":\"ok\"},\"isError\":true}", 15)]
    [InlineData("{\"content\":null,\"structuredContent\":{\"msg\":\"<b>café</b> \U0001F600\\n\"},\"_meta\":{\"k\":\"v\"}}", 20)]
    public void Falls_back_to_the_whole_result_when_there_is_no_content_list(string json, int tokens)
    {
        Assert.Equal(tokens, TokenEstimator.EstimateOutputTokens(Mcp.Result(json)));
    }

    [Fact]
    public void Absent_result_omits_the_field()
    {
        Assert.Null(TokenEstimator.EstimateOutputTokens(null));
    }

    [Fact]
    public void Unreadable_content_omits_the_field()
    {
        var result = Mcp.Result("{\"content\":[]}");
        result.Content = new ThrowingList();

        Assert.Null(TokenEstimator.EstimateOutputTokens(result));
    }

    private sealed class ThrowingList : IList<ContentBlock>
    {
        public ContentBlock this[int index]
        {
            get => throw new InvalidOperationException("boom");
            set => throw new InvalidOperationException("boom");
        }

        public int Count => throw new InvalidOperationException("boom");

        public bool IsReadOnly => throw new InvalidOperationException("boom");

        public void Add(ContentBlock item) => throw new InvalidOperationException("boom");

        public void Clear() => throw new InvalidOperationException("boom");

        public bool Contains(ContentBlock item) => throw new InvalidOperationException("boom");

        public void CopyTo(ContentBlock[] array, int arrayIndex) => throw new InvalidOperationException("boom");

        public IEnumerator<ContentBlock> GetEnumerator() => throw new InvalidOperationException("boom");

        public int IndexOf(ContentBlock item) => throw new InvalidOperationException("boom");

        public void Insert(int index, ContentBlock item) => throw new InvalidOperationException("boom");

        public bool Remove(ContentBlock item) => throw new InvalidOperationException("boom");

        public void RemoveAt(int index) => throw new InvalidOperationException("boom");

        IEnumerator IEnumerable.GetEnumerator() => throw new InvalidOperationException("boom");
    }
}
