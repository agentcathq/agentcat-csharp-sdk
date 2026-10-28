using System;
using System.Buffers.Binary;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Ids;

internal static class SessionIds
{
    private const long Epoch2024Milliseconds = 1_704_067_200_000;
    private const long OffsetModulus = 31_536_000_000;
    private const string SessionIdPrefix = WireNames.SessionPrefix + "_";
    private const string EventIdPrefix = WireNames.EventPrefix + "_";

    internal static string NewSessionId(DateTimeOffset now) => SessionIdPrefix + Ksuid.NewRandom(now).ToString();

    internal static string NewEventId(DateTimeOffset now) => EventIdPrefix + Ksuid.NewRandom(now).ToString();

    internal static string Derive(string id, string? projectId)
    {
        var hash = Crypto.Sha256Utf8(string.IsNullOrEmpty(projectId) ? id : id + ":" + projectId);
        var offset = BinaryPrimitives.ReadUInt32BigEndian(hash) % OffsetModulus;
        var ksuid = Ksuid.FromParts(Epoch2024Milliseconds + offset, hash.AsSpan(4, Ksuid.PayloadLength));
        return SessionIdPrefix + ksuid.ToString();
    }

    internal static bool IsValid(string? value)
    {
        if (value is null
            || value.Length != SessionIdPrefix.Length + Ksuid.StringLength
            || !value.StartsWith(SessionIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = SessionIdPrefix.Length; i < value.Length; i++)
        {
            if (!IsBase62(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBase62(char c) => c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
}
