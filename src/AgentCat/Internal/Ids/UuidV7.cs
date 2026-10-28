using System;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Ids;

internal static class UuidV7
{
    internal static string FromPrefixedId(string prefixedId)
    {
        var hash = Crypto.Sha256Utf8(prefixedId);
        var timestamp = TimestampMilliseconds(prefixedId, hash);

        var bytes = new byte[16];
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = (byte)(timestamp >> (8 * (5 - i)));
        }

        bytes[6] = (byte)(0x70 | (hash[0] & 0x0f));
        bytes[7] = hash[1];
        bytes[8] = (byte)(0x80 | (hash[2] & 0x3f));
        Array.Copy(hash, 3, bytes, 9, 7);
        return Format(bytes);
    }

    private static long TimestampMilliseconds(string prefixedId, byte[] hash)
    {
        try
        {
            return Ksuid.Parse(StripPrefix(prefixedId)).Date.ToUnixTimeMilliseconds();
        }
        catch (Exception)
        {
            long timestamp = 0;
            for (var i = 10; i < 16; i++)
            {
                timestamp = (timestamp << 8) | hash[i];
            }

            return timestamp;
        }
    }

    private static string StripPrefix(string prefixedId)
    {
        var letters = 0;
        while (letters < prefixedId.Length && prefixedId[letters] is >= 'a' and <= 'z')
        {
            letters++;
        }

        return letters > 0 && letters < prefixedId.Length && prefixedId[letters] == '_'
            ? prefixedId.Substring(letters + 1)
            : prefixedId;
    }

    private static string Format(byte[] bytes)
    {
        var hex = Hex.Lower(bytes);
        return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-"
            + hex.Substring(16, 4) + "-" + hex.Substring(20);
    }
}
