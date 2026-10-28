using System;
using System.Buffers.Binary;
using System.Globalization;
using AgentCat.Internal.Telemetry;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Ids;

internal static class TraceIds
{
    private const int TraceIdLength = 16;
    private const int SpanIdLength = 8;

    internal static string GetTraceId(string? sessionId) => GetTraceId(sessionId, RandomSource.Default);

    internal static string GetTraceId(string? sessionId, IRandomSource random) => Hex.Lower(IdBytes(sessionId, TraceIdLength, random));

    internal static string GetSpanId(string? eventId) => GetSpanId(eventId, RandomSource.Default);

    internal static string GetSpanId(string? eventId, IRandomSource random) => Hex.Lower(IdBytes(eventId, SpanIdLength, random));

    internal static string GetDatadogTraceId(string? sessionId) => GetDatadogTraceId(sessionId, RandomSource.Default);

    internal static string GetDatadogTraceId(string? sessionId, IRandomSource random)
        => ToDecimal(IdBytes(sessionId, TraceIdLength, random).AsSpan(TraceIdLength - 8));

    internal static string GetDatadogSpanId(string? eventId) => GetDatadogSpanId(eventId, RandomSource.Default);

    internal static string GetDatadogSpanId(string? eventId, IRandomSource random)
        => ToDecimal(IdBytes(eventId, SpanIdLength, random));

    private static byte[] IdBytes(string? id, int length, IRandomSource random)
    {
        if (string.IsNullOrEmpty(id))
        {
            var bytes = new byte[length];
            random.Fill(bytes);
            return bytes;
        }

        var hash = Crypto.Sha256Utf8(id!);
        Array.Resize(ref hash, length);
        return hash;
    }

    private static string ToDecimal(ReadOnlySpan<byte> bytes)
        => BinaryPrimitives.ReadUInt64BigEndian(bytes).ToString(CultureInfo.InvariantCulture);
}
