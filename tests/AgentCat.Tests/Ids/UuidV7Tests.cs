using System.Globalization;
using AgentCat.Internal.Ids;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Ids;

public class UuidV7Tests
{
    private const string UuidV7Shape = @"^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z";

    private readonly string _realSessionId = SessionIds.NewSessionId(DateTimeOffset.UtcNow);

    private static string NewSessionId() => SessionIds.NewSessionId(DateTimeOffset.UtcNow);

    private static string RawHex(string uuid) => uuid.Replace("-", string.Empty);

    private static long TimestampMs(string uuid) => Convert.ToInt64(RawHex(uuid).Substring(0, 12), 16);

    private static void AssertValidUuidV7(string uuid)
    {
        Assert.Matches(UuidV7Shape, uuid);
        Assert.Equal(7, Convert.ToInt32(uuid.Substring(14, 1), 16));
    }

    private static byte[] Filled(int length, byte value)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = value;
        }

        return bytes;
    }

    public static TheoryData<string, string, string> GoldenVectors() => new()
    {
        { "session", "ses_2yXlShd4ODDcHLYliGW9VgH3m1R", "01977375-f600-7179-87c6-b00767670c45" },
        { "event", "evt_2yXlTL6bElM5Whm0HjT0Iz8LL0M", "01977376-0988-78fb-aa6b-36e16f376637" },
        { "my-customer-task-id", "my-customer-task-id", "634902a7-d0c7-7220-91c6-0fa56fad8828" },
        { "''", "", "f4c8996f-b924-73b0-8442-98fc1c149afb" },
        { "ses_session45600000000000000000", "ses_session45600000000000000000", "6c350afa-fa75-7081-b0c6-8ad3272daa6c" },
        { "abc_def_ghi", "abc_def_ghi", "ae0466b7-1fe3-7984-a344-541280a247bb" },
        { "27 dashes", "x_" + new string('-', 27), "0145f680-b000-7d7a-8884-df06b0d17ac0" },
    };

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void FromPrefixedId_matches_golden_vectors(string label, string input, string expected)
    {
        var golden = Fixtures.Text("exporters/golden-output.txt");
        Assert.Contains("uuid(" + label + ") " + expected, golden, StringComparison.Ordinal);

        Assert.Equal(expected, UuidV7.FromPrefixedId(input));
    }

    private static readonly Dictionary<string, (string Input, string Expected)> EdgeVectors = new(StringComparer.Ordinal)
    {
        ["unprefixed KSUID"] = ("2yXlShd4ODDcHLYliGW9VgH3m1R", "01977375-f600-7c39-8967-a3721caf96e2"),
        ["upper-case prefix is kept"] = ("SES_2yXlShd4ODDcHLYliGW9VgH3m1R", "8bc15b2a-2661-7bac-a5b6-02eefdf24182"),
        ["digit in prefix is kept"] = ("ses1_2yXlShd4ODDcHLYliGW9VgH3m1R", "56850178-a427-7b2f-a6cc-207e5e56421e"),
        ["bare underscore is kept"] = ("_2yXlShd4ODDcHLYliGW9VgH3m1R", "bd6465b4-f89e-7ade-afa1-38dfc267d2ec"),
        ["only the first prefix is stripped"] = ("a_ses_2yXlShd4ODDcHLYliGW9VgH3m1R", "8cc403ec-afed-77fd-a8a1-bc3b916b72de"),
        ["non-ASCII letter prefix is kept"] = ("é_2yXlShd4ODDcHLYliGW9VgH3m1R", "f76a5eab-6cf3-73ca-a7d2-4af0944db97c"),
        ["leading newline is kept"] = ("\nses_2yXlShd4ODDcHLYliGW9VgH3m1R", "01926de0-6d95-7fa0-863c-0cd04c5ff442"),
        ["maximum KSUID"] = ("ses_aWgEPTl1tmebfsQzFP4bxwgy80V", "052df680-ac18-70fb-a3ad-b18287c0f14b"),
        ["minimum KSUID"] = ("ses_000000000000000000000000000", "0145f680-b000-72dd-9660-4a400f542404"),
        ["27 dashes with ses prefix"] = ("ses_" + new string('-', 27), "0145f680-b000-786d-98cc-d5d01e28dec2"),
        ["27 dashes without prefix"] = (new string('-', 27), "0145f680-b000-7e0f-a24f-0941faa11a30"),
        ["27 characters wider than 20 bytes"] = ("ses_" + new string('z', 27), "824c5521-0b85-7994-816a-e1cc13ecbe70"),
        ["lone surrogate"] = ("ses_a\uD800b", "079522d3-13fe-763d-a426-b15ae27dcfb2"),
        ["surrogate pair"] = ("ses_\U0001F600", "ba05d9c7-e99e-7a83-81c3-c8be4445bb30"),
    };

    public static TheoryData<string> EdgeVectorLabels() => new(EdgeVectors.Keys);

    [Theory]
    [MemberData(nameof(EdgeVectorLabels))]
    public void FromPrefixedId_matches_TS_for_edge_inputs(string label)
    {
        var (input, expected) = EdgeVectors[label];

        Assert.Equal(expected, UuidV7.FromPrefixedId(input));
    }

    [Fact]
    public void Should_produce_a_valid_UUIDv7()
    {
        var result = UuidV7.FromPrefixedId(_realSessionId);

        AssertValidUuidV7(result);
    }

    [Fact]
    public void Should_be_deterministic_same_input_always_produces_same_output()
    {
        var a = UuidV7.FromPrefixedId(_realSessionId);
        var b = UuidV7.FromPrefixedId(_realSessionId);
        var c = UuidV7.FromPrefixedId(_realSessionId);

        Assert.Equal(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void Should_produce_different_UUIDs_for_different_session_IDs()
    {
        var sessionA = NewSessionId();
        var sessionB = NewSessionId();

        Assert.NotEqual(UuidV7.FromPrefixedId(sessionA), UuidV7.FromPrefixedId(sessionB));
    }

    [Fact]
    public void Should_embed_the_KSUID_timestamp_in_the_UUIDv7()
    {
        var knownTime = DateTimeOffset.Parse("2025-06-15T12:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        var ksuid = Ksuid.FromParts(knownTime, Filled(16, 0xab));
        var sessionId = "ses_" + ksuid.ToString();

        var result = UuidV7.FromPrefixedId(sessionId);

        var extractedMs = TimestampMs(result);
        Assert.True(Math.Abs(extractedMs - knownTime) < 1000);
    }

    [Fact]
    public void Should_set_version_bits_to_7_0111()
    {
        var result = UuidV7.FromPrefixedId(_realSessionId);

        var versionNibble = Convert.ToInt32(RawHex(result).Substring(12, 1), 16);
        Assert.Equal(7, versionNibble);
    }

    [Fact]
    public void Should_set_variant_bits_to_10xx()
    {
        var result = UuidV7.FromPrefixedId(_realSessionId);

        var variantNibble = Convert.ToInt32(RawHex(result).Substring(16, 1), 16);
        Assert.InRange(variantNibble, 8, 0xb);
    }

    [Fact]
    public void Should_handle_evt_prefix()
    {
        var eventId = SessionIds.NewEventId(DateTimeOffset.UtcNow);

        var result = UuidV7.FromPrefixedId(eventId);

        AssertValidUuidV7(result);
    }

    [Fact]
    public void Should_handle_invalid_KSUID_gracefully_with_fallback_timestamp()
    {
        var result = UuidV7.FromPrefixedId("ses_invalid_ksuid_string");

        AssertValidUuidV7(result);
        Assert.Equal("8a9bdc3e-a187-7187-bbe0-cee6b92a5751", result);
    }

    [Fact]
    public async Task Should_be_deterministic_for_non_KSUID_input_fallback_path()
    {
        var first = UuidV7.FromPrefixedId("my-customer-task-id");
        await Task.Delay(5, TestContext.Current.CancellationToken);
        var second = UuidV7.FromPrefixedId("my-customer-task-id");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Should_produce_different_UUIDs_for_different_non_KSUID_inputs()
    {
        Assert.NotEqual(UuidV7.FromPrefixedId("task-a"), UuidV7.FromPrefixedId("task-b"));
        Assert.Equal("2084b1c4-8b52-77bb-bbed-5fd87cbc8c1b", UuidV7.FromPrefixedId("task-a"));
        Assert.Equal("8c37e6bc-5830-7977-9a3c-7aa32912c971", UuidV7.FromPrefixedId("task-b"));
    }

    [Fact]
    public async Task Should_be_deterministic_for_empty_string_input()
    {
        var first = UuidV7.FromPrefixedId(string.Empty);
        await Task.Delay(5, TestContext.Current.CancellationToken);

        Assert.Equal(first, UuidV7.FromPrefixedId(string.Empty));
        AssertValidUuidV7(first);
    }

    [Fact]
    public void Should_produce_timestamps_that_are_before_or_equal_to_current_time()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = UuidV7.FromPrefixedId(_realSessionId);

        var extractedMs = TimestampMs(result);
        Assert.True(extractedMs <= now);
    }
}
