using System;
using System.Buffers.Binary;

namespace AgentCat.Internal.Ids;

internal readonly struct Ksuid : IEquatable<Ksuid>, IComparable<Ksuid>
{
    public const long EpochMilliseconds = 1_400_000_000_000;
    public const int ByteLength = 20;
    public const int PayloadLength = 16;
    public const int StringLength = 27;
    public const string MinString = "000000000000000000000000000";
    public const string MaxString = "aWgEPTl1tmebfsQzFP4bxwgy80V";

    private const int TimestampLength = ByteLength - PayloadLength;
    private const long MaxMilliseconds = (1000L * uint.MaxValue) + EpochMilliseconds;

    private const string TimeMessage = "Valid KSUID timestamps must be in milliseconds since 1970-01-01T00:00:00Z, "
        + "no earlier than 2014-05-13T16:53:20Z and no later than 2150-06-19T23:21:35Z";

    private static readonly byte[] Zero = new byte[ByteLength];

    private readonly byte[]? _raw;

    private Ksuid(byte[] raw) => _raw = raw;

    public uint Timestamp => BinaryPrimitives.ReadUInt32BigEndian(Raw);

    public DateTimeOffset Date => DateTimeOffset.FromUnixTimeMilliseconds((1000L * Timestamp) + EpochMilliseconds);

    public ReadOnlySpan<byte> Payload => Raw.Slice(TimestampLength);

    private ReadOnlySpan<byte> Raw => _raw ?? Zero;

    public static Ksuid FromParts(long unixTimeMilliseconds, ReadOnlySpan<byte> payload)
    {
        if (unixTimeMilliseconds < EpochMilliseconds || unixTimeMilliseconds > MaxMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(unixTimeMilliseconds), unixTimeMilliseconds, TimeMessage);
        }

        if (payload.Length != PayloadLength)
        {
            throw new ArgumentException("Valid KSUID payloads are 16 bytes", nameof(payload));
        }

        var raw = new byte[ByteLength];
        BinaryPrimitives.WriteUInt32BigEndian(raw, (uint)((unixTimeMilliseconds - EpochMilliseconds) / 1000));
        payload.CopyTo(raw.AsSpan(TimestampLength));
        return new Ksuid(raw);
    }

    public static Ksuid FromBytes(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != ByteLength)
        {
            throw new ArgumentException("Valid KSUID buffers are 20 bytes", nameof(raw));
        }

        return new Ksuid(raw.ToArray());
    }

    public static Ksuid NewRandom(DateTimeOffset now)
        => FromParts(now.ToUnixTimeMilliseconds(), Crypto.RandomBytes(PayloadLength));

    public static Ksuid Parse(string encoded)
    {
        if (encoded.Length != StringLength)
        {
            throw new FormatException("Valid encoded KSUIDs are 27 characters");
        }

        return new Ksuid(Base62.Decode(encoded, ByteLength));
    }

    public static bool operator ==(Ksuid left, Ksuid right) => left.Equals(right);

    public static bool operator !=(Ksuid left, Ksuid right) => !left.Equals(right);

    public static bool operator <(Ksuid left, Ksuid right) => left.CompareTo(right) < 0;

    public static bool operator <=(Ksuid left, Ksuid right) => left.CompareTo(right) <= 0;

    public static bool operator >(Ksuid left, Ksuid right) => left.CompareTo(right) > 0;

    public static bool operator >=(Ksuid left, Ksuid right) => left.CompareTo(right) >= 0;

    public byte[] ToByteArray() => Raw.ToArray();

    public int CompareTo(Ksuid other) => Raw.SequenceCompareTo(other.Raw);

    public bool Equals(Ksuid other) => Raw.SequenceEqual(other.Raw);

    public override bool Equals(object? obj) => obj is Ksuid other && Equals(other);

    public override int GetHashCode()
    {
        var raw = Raw;
        var hash = 17;
        for (var i = 0; i < ByteLength; i += 4)
        {
            hash = unchecked((hash * 31) + BinaryPrimitives.ReadInt32BigEndian(raw.Slice(i)));
        }

        return hash;
    }

    public override string ToString() => Base62.Encode(Raw, StringLength);
}
