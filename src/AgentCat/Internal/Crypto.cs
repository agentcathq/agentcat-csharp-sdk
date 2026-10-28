using System;
using System.Security.Cryptography;
using System.Text;

namespace AgentCat.Internal;

internal static class Crypto
{
    internal static byte[] Sha256(ReadOnlySpan<byte> data)
    {
#if NET
        return SHA256.HashData(data);
#else
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(data.ToArray());
#endif
    }

    internal static byte[] Sha256Utf8(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    internal static byte[] RandomBytes(int count)
    {
#if NET
        return RandomNumberGenerator.GetBytes(count);
#else
        var bytes = new byte[count];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return bytes;
#endif
    }
}
