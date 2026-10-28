using System.Globalization;
using AgentCat.Internal.Ids;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Ids;

public class TraceIdsTests
{
    private const string SessionId = "ses_2yXlShd4ODDcHLYliGW9VgH3m1R";
    private const string EventId = "evt_2yXlTL6bElM5Whm0HjT0Iz8LL0M";
    private const string TraceIdShape = @"^[0-9a-f]{32}\z";
    private const string SpanIdShape = @"^[0-9a-f]{16}\z";
    private const string UnsignedDecimalShape = @"^(0|[1-9][0-9]*)\z";

    [Fact]
    public void Ids_match_golden_header()
    {
        var golden = Fixtures.Text("exporters/golden-output.txt");
        Assert.Contains("sessionId " + SessionId + " eventId " + EventId, golden, StringComparison.Ordinal);
        Assert.Contains("traceId b17987c6b00767670c45519276a327c3 spanId 38fbea6b36e16f37", golden, StringComparison.Ordinal);
        Assert.Contains("ddTrace 884202591345649603 ddSpan 4106133231481483063", golden, StringComparison.Ordinal);

        Assert.Equal("b17987c6b00767670c45519276a327c3", TraceIds.GetTraceId(SessionId));
        Assert.Equal("38fbea6b36e16f37", TraceIds.GetSpanId(EventId));
        Assert.Equal("884202591345649603", TraceIds.GetDatadogTraceId(SessionId));
        Assert.Equal("4106133231481483063", TraceIds.GetDatadogSpanId(EventId));
    }

    private static readonly Dictionary<string, (string Input, string TraceId, string SpanId, string DatadogTraceId, string DatadogSpanId)> Vectors = new(StringComparer.Ordinal)
    {
        ["session"] = (SessionId, "b17987c6b00767670c45519276a327c3", "b17987c6b0076767", "884202591345649603", "12788401904321783655"),
        ["event"] = (EventId, "38fbea6b36e16f376637b11922343062", "38fbea6b36e16f37", "7365550437093879906", "4106133231481483063"),
        ["Datadog trace above long.MaxValue"] = ("ses_1", "46bf2fa87b4820eabd28fa94f4fa05e6", "46bf2fa87b4820ea", "13630419789908936166", "5097845703876026602"),
        ["Datadog span above long.MaxValue"] = ("evt_2", "e7a53ae46b784f8fc1105e6029d8fccf", "e7a53ae46b784f8f", "13911723016059419855", "16691812346648612751"),
        ["single character"] = ("x", "2d711642b726b04401627ca9fbac32f5", "2d711642b726b044", "99779211269255925", "3274422879871479876"),
        ["lone surrogate"] = ("a\uD800b", "05087813392efc16fe8ff448920c6328", "05087813392efc16", "18343148399828427560", "362671793962417174"),
        ["surrogate pair"] = ("\U0001F600", "f0443a342c5ef54783a111b51ba56c93", "f0443a342c5ef547", "9484881759769554067", "17313026863276160327"),
    };

    public static TheoryData<string> VectorLabels() => new(Vectors.Keys);

    [Theory]
    [MemberData(nameof(VectorLabels))]
    public void Ids_match_TS(string label)
    {
        var (input, traceId, spanId, datadogTraceId, datadogSpanId) = Vectors[label];

        Assert.Equal(traceId, TraceIds.GetTraceId(input));
        Assert.Equal(spanId, TraceIds.GetSpanId(input));
        Assert.Equal(datadogTraceId, TraceIds.GetDatadogTraceId(input));
        Assert.Equal(datadogSpanId, TraceIds.GetDatadogSpanId(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_session_gives_a_random_trace_id(string? sessionId)
    {
        var first = TraceIds.GetTraceId(sessionId);
        var second = TraceIds.GetTraceId(sessionId);

        Assert.Matches(TraceIdShape, first);
        Assert.Matches(TraceIdShape, second);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_event_gives_a_random_span_id(string? eventId)
    {
        var first = TraceIds.GetSpanId(eventId);
        var second = TraceIds.GetSpanId(eventId);

        Assert.Matches(SpanIdShape, first);
        Assert.Matches(SpanIdShape, second);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_inputs_give_random_Datadog_ids(string? id)
    {
        var traces = new[] { TraceIds.GetDatadogTraceId(id), TraceIds.GetDatadogTraceId(id) };
        var spans = new[] { TraceIds.GetDatadogSpanId(id), TraceIds.GetDatadogSpanId(id) };

        Assert.All(traces, value => Assert.Matches(UnsignedDecimalShape, value));
        Assert.All(spans, value => Assert.Matches(UnsignedDecimalShape, value));
        Assert.All(traces, value => Assert.True(ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)));
        Assert.All(spans, value => Assert.True(ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)));
        Assert.NotEqual(traces[0], traces[1]);
        Assert.NotEqual(spans[0], spans[1]);
    }

    // The sessionless OTLP trace ID and Datadog trace ID of golden2-output.txt, from the bytes TS drew.
    [Fact]
    public void Sessionless_ids_take_their_bytes_from_the_random_source()
    {
        var golden = Fixtures.Text("exporters/golden2-output.txt");
        Assert.Contains("\"traceId\":\"5fbf74784385eeb15609b0fed72cbb30\"", golden, StringComparison.Ordinal);
        Assert.Contains("\"trace_id\":\"16140858155396737714\"", golden, StringComparison.Ordinal);

        var random = new FixedRandomSource(
            "5fbf74784385eeb15609b0fed72cbb30",
            "0102030405060708",
            "0000000000000000dfffd8f97263aab2",
            "8000000000000001");

        Assert.Equal("5fbf74784385eeb15609b0fed72cbb30", TraceIds.GetTraceId("", random));
        Assert.Equal("0102030405060708", TraceIds.GetSpanId(null, random));
        Assert.Equal("16140858155396737714", TraceIds.GetDatadogTraceId(null, random));
        Assert.Equal("9223372036854775809", TraceIds.GetDatadogSpanId("", random));
        Assert.Equal(4, random.Draws);
    }

    [Fact]
    public void Ids_of_a_session_or_event_never_draw_random_bytes()
    {
        var random = new FixedRandomSource();

        Assert.Equal("b17987c6b00767670c45519276a327c3", TraceIds.GetTraceId(SessionId, random));
        Assert.Equal("38fbea6b36e16f37", TraceIds.GetSpanId(EventId, random));
        Assert.Equal("884202591345649603", TraceIds.GetDatadogTraceId(SessionId, random));
        Assert.Equal("4106133231481483063", TraceIds.GetDatadogSpanId(EventId, random));
        Assert.Equal(0, random.Draws);
    }
}
