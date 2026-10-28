using System.Globalization;
using AgentCat.Internal.Ids;

namespace AgentCat.Tests.Ids;

public class KsuidTests
{
    private const long MaxMilliseconds = (1000L * uint.MaxValue) + Ksuid.EpochMilliseconds;

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static DateTimeOffset At(long unixTimeMilliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(unixTimeMilliseconds);

    private static DateTimeOffset FlooredToSeconds(long unixTimeMilliseconds) => At(unixTimeMilliseconds / 1000 * 1000);

    private static byte[] Filled(int length, byte value)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = value;
        }

        return bytes;
    }

    private static byte[] Hex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    [Fact]
    public void Should_create_a_KSUID_from_randomSync()
    {
        var ksuid = Ksuid.NewRandom(DateTimeOffset.UtcNow);

        Assert.Equal(27, ksuid.ToString().Length);
        Assert.Equal(20, ksuid.ToByteArray().Length);
    }

    [Fact]
    public void Should_create_KSUIDs_with_different_timestamps()
    {
        var time1 = NowMs();
        var time2 = time1 + 1000;

        var ksuid1 = Ksuid.NewRandom(At(time1));
        var ksuid2 = Ksuid.NewRandom(At(time2));

        Assert.Equal(FlooredToSeconds(time1), ksuid1.Date);
        Assert.Equal(FlooredToSeconds(time2), ksuid2.Date);
        Assert.True(ksuid1.CompareTo(ksuid2) < 0);
    }

    [Fact]
    public void Should_create_KSUID_from_timestamp_and_payload()
    {
        var timeInMs = NowMs();
        var payload = Filled(16, 42);

        var ksuid = Ksuid.FromParts(timeInMs, payload);

        Assert.Equal(FlooredToSeconds(timeInMs), ksuid.Date);
        Assert.Equal(payload, ksuid.Payload.ToArray());
    }

    [Fact]
    public void Should_throw_error_for_invalid_timestamp()
    {
        var payload = new byte[16];

        Assert.Throws<ArgumentOutOfRangeException>(() => Ksuid.FromParts(100, payload));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ksuid.FromParts(NowMs() + 1_000_000_000_000_000, payload));
    }

    [Fact]
    public void Should_throw_error_for_invalid_payload()
    {
        var timeInMs = NowMs();

        Assert.Throws<ArgumentException>(() => Ksuid.FromParts(timeInMs, new byte[15]));
        Assert.Throws<ArgumentException>(() => Ksuid.FromParts(timeInMs, new byte[17]));
    }

    [Fact]
    public void Should_encode_and_parse_KSUID_strings_correctly()
    {
        var original = Ksuid.NewRandom(DateTimeOffset.UtcNow);
        var encoded = original.ToString();

        var parsed = Ksuid.Parse(encoded);

        Assert.True(parsed.Equals(original));
        Assert.Equal(encoded, parsed.ToString());
    }

    [Fact]
    public void Should_handle_MIN_and_MAX_string_values()
    {
        var min = Ksuid.Parse(Ksuid.MinString);
        var max = Ksuid.Parse(Ksuid.MaxString);

        Assert.Equal("000000000000000000000000000", min.ToString());
        Assert.Equal("aWgEPTl1tmebfsQzFP4bxwgy80V", max.ToString());
        Assert.True(min.CompareTo(max) < 0);
        Assert.Equal(new byte[20], min.ToByteArray());
        Assert.Equal(Filled(20, 0xFF), max.ToByteArray());
        Assert.Equal(uint.MaxValue, max.Timestamp);
        Assert.Equal(At(MaxMilliseconds), max.Date);
    }

    [Fact]
    public void Should_throw_error_for_invalid_string_length()
    {
        var tooShort = Assert.Throws<FormatException>(() => Ksuid.Parse("too_short"));
        var tooLong = Assert.Throws<FormatException>(() => Ksuid.Parse("way_too_long_string_for_ksuid"));

        Assert.Equal("Valid encoded KSUIDs are 27 characters", tooShort.Message);
        Assert.Equal("Valid encoded KSUIDs are 27 characters", tooLong.Message);
    }

    [Fact]
    public void Should_compare_KSUIDs_correctly()
    {
        var ksuid1 = Ksuid.NewRandom(DateTimeOffset.UtcNow);
        var ksuid2 = Ksuid.NewRandom(DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.True(ksuid1.CompareTo(ksuid2) < 0);
        Assert.True(ksuid2.CompareTo(ksuid1) > 0);
        Assert.Equal(0, ksuid1.CompareTo(ksuid1));
        Assert.True(ksuid1 < ksuid2);
        Assert.True(ksuid2 > ksuid1);
        var copy = Ksuid.Parse(ksuid1.ToString());
        Assert.True(ksuid1 <= copy);
        Assert.True(ksuid1 >= copy);
        Assert.False(ksuid1 > copy);
        Assert.False(ksuid2 <= ksuid1);
    }

    [Fact]
    public void Should_check_equality_correctly()
    {
        var ksuid1 = Ksuid.NewRandom(DateTimeOffset.UtcNow);
        var ksuid2 = Ksuid.Parse(ksuid1.ToString());
        var ksuid3 = Ksuid.NewRandom(DateTimeOffset.UtcNow);

        Assert.True(ksuid1.Equals(ksuid1));
        Assert.True(ksuid1.Equals(ksuid2));
        Assert.False(ksuid1.Equals(ksuid3));
        Assert.True(ksuid1 == ksuid2);
        Assert.True(ksuid1 != ksuid3);
        Assert.True(ksuid1.Equals((object)ksuid2));
        Assert.False(ksuid1.Equals(ksuid1.ToString()));
        Assert.Equal(ksuid1.GetHashCode(), ksuid2.GetHashCode());
    }

    [Fact]
    public void Should_throw_error_for_invalid_buffer_in_constructor()
    {
        Assert.Throws<ArgumentException>(() => Ksuid.FromBytes(new byte[19]));
        Assert.Throws<ArgumentException>(() => Ksuid.FromBytes(new byte[21]));
    }

    [Fact]
    public void Should_have_correct_timestamp_property()
    {
        var timeInMs = NowMs();

        var ksuid = Ksuid.NewRandom(At(timeInMs));

        Assert.Equal((uint)((timeInMs - 1_400_000_000_000) / 1000), ksuid.Timestamp);
    }

    [Fact]
    public void Should_have_correct_date_property()
    {
        var timeInMs = NowMs();

        var ksuid = Ksuid.NewRandom(At(timeInMs));

        Assert.Equal(FlooredToSeconds(timeInMs), ksuid.Date);
    }

    [Fact]
    public void Should_have_16_byte_payload()
    {
        var ksuid = Ksuid.NewRandom(DateTimeOffset.UtcNow);

        Assert.Equal(16, ksuid.Payload.Length);
    }

    [Fact]
    public void Should_handle_KSUID_epoch_boundary()
    {
        var epochTime = At(1_400_000_000_000);

        var ksuid = Ksuid.NewRandom(epochTime);

        Assert.Equal(0u, ksuid.Timestamp);
        Assert.Equal(epochTime, ksuid.Date);
    }

    [Fact]
    public void Should_create_unique_KSUIDs()
    {
        var strings = new List<string>();
        for (var i = 0; i < 100; i++)
        {
            strings.Add(Ksuid.NewRandom(DateTimeOffset.UtcNow).ToString());
        }

        Assert.Equal(strings.Count, new HashSet<string>(strings, StringComparer.Ordinal).Count);
    }

    [Fact]
    public void Constants_match_TS()
    {
        Assert.Equal(1_400_000_000_000, Ksuid.EpochMilliseconds);
        Assert.Equal(20, Ksuid.ByteLength);
        Assert.Equal(16, Ksuid.PayloadLength);
        Assert.Equal(27, Ksuid.StringLength);
        Assert.Equal("000000000000000000000000000", Ksuid.MinString);
        Assert.Equal("aWgEPTl1tmebfsQzFP4bxwgy80V", Ksuid.MaxString);
    }

    [Fact]
    public void FromParts_accepts_exactly_the_representable_range()
    {
        Assert.Equal(Ksuid.MinString, Ksuid.FromParts(Ksuid.EpochMilliseconds, new byte[16]).ToString());
        Assert.Equal(Ksuid.MaxString, Ksuid.FromParts(MaxMilliseconds, Filled(16, 0xFF)).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => Ksuid.FromParts(Ksuid.EpochMilliseconds - 1, new byte[16]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ksuid.FromParts(MaxMilliseconds + 1, new byte[16]));
    }

    [Fact]
    public void ToString_matches_TS_encoding()
    {
        var sequence = new byte[16];
        for (var i = 0; i < sequence.Length; i++)
        {
            sequence[i] = (byte)i;
        }

        Assert.Equal("00eMKhJX6TVpWeUosIRtqpuh1Nn", Ksuid.FromParts(1_400_000_000_000 + 1_234_567_999, sequence).ToString());
        Assert.Equal("33p35AICTnexASGr07KiVSgeE9a", Ksuid.FromParts(1_760_000_000_123, Filled(16, 42)).ToString());
        Assert.Equal("aWgEPLxxrZOFaOlDVFHTB3ZiQOO", Ksuid.FromParts(MaxMilliseconds, new byte[16]).ToString());
    }

    [Fact]
    public void FromBytes_and_ToByteArray_copy()
    {
        var raw = Filled(20, 7);

        var ksuid = Ksuid.FromBytes(raw);
        raw[19] = 8;
        ksuid.ToByteArray()[18] = 9;

        Assert.Equal(Filled(20, 7), ksuid.ToByteArray());
        Assert.Equal(ksuid, Ksuid.Parse(ksuid.ToString()));
    }

    [Fact]
    public void Default_value_is_the_minimum_KSUID()
    {
        var empty = default(Ksuid);

        Assert.Equal(Ksuid.MinString, empty.ToString());
        Assert.Equal(Ksuid.Parse(Ksuid.MinString), empty);
        Assert.Equal(0u, empty.Timestamp);
        Assert.Equal(new byte[16], empty.Payload.ToArray());
    }

    [Fact]
    public void Short_values_decode_left_padded_with_zero_bytes()
    {
        var ksuid = Ksuid.Parse("0000000000000000000000000" + "10");

        var expected = new byte[20];
        expected[19] = 62;
        Assert.Equal(expected, ksuid.ToByteArray());
    }

    private static readonly Dictionary<string, (string Input, string RawHex, string Reencoded)> NonBase62 = new(StringComparer.Ordinal)
    {
        ["27 dashes"] = (new string('-', 27), "000000000000000000000000000000000000003f", "000000000000000000000000011"),
        ["27 spaces"] = (new string(' ', 27), "0000000000000000000000000000000000000050", "00000000000000000000000001I"),
        ["27 NULs"] = (new string('\0', 27), "00000000000000000000000000000000000000f0", "00000000000000000000000003s"),
        ["surrogate pair is one digit"] = (new string('0', 25) + "\U0001F600", "000000000000000000000000000000000000d800", "000000000000000000000000ENs"),
        ["lone low surrogate"] = (new string('0', 26) + "\uDE00", "000000000000000000000000000000000000ddc3", "000000000000000000000000Elf"),
        ["negative digit after a zero remainder"] = (new string('0', 24) + "48-", "0000000000000000000000000000000000003dfd", "00000000000000000000000047x"),
        ["repeated negative quotients"] = (new string('0', 16) + "48-48-48-48", "0000000000000000000000002f4bcc68ef13e354", "000000000000000043krvkrvkw8"),
    };

    public static TheoryData<string> NonBase62Labels() => new(NonBase62.Keys);

    [Theory]
    [MemberData(nameof(NonBase62Labels))]
    public void Parse_decodes_non_base62_characters_like_TS(string label)
    {
        var (input, rawHex, reencoded) = NonBase62[label];

        var ksuid = Ksuid.Parse(input);

        Assert.Equal(Hex(rawHex), ksuid.ToByteArray());
        Assert.Equal(reencoded, ksuid.ToString());
        Assert.Equal(At(Ksuid.EpochMilliseconds), ksuid.Date);
    }

    private static readonly Dictionary<string, (string Input, int ExpectedLength)> TooLarge = new(StringComparer.Ordinal)
    {
        ["27 z"] = (new string('z', 27), 21),
        ["max plus one"] = ("aWgEPTl1tmebfsQzFP4bxwgy80W", 21),
        ["customer value"] = ("my-customer-task-id00000000", 21),
        ["leading surrogate pair"] = ("\U0001F600" + new string('0', 25), 20),
        ["leading lone high surrogate"] = ("\uD83D" + new string('0', 26), 21),
    };

    public static TheoryData<string> TooLargeLabels() => new(TooLarge.Keys);

    [Theory]
    [MemberData(nameof(TooLargeLabels))]
    public void Parse_rejects_values_wider_than_20_bytes_like_TS(string label)
    {
        var (input, expectedLength) = TooLarge[label];

        var error = Assert.Throws<ArgumentException>(() => Ksuid.Parse(input));

        Assert.Equal("Fixed length of 20 is too small, expected at least " + expectedLength.ToString(CultureInfo.InvariantCulture), error.Message);
    }
}
