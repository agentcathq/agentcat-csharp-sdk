using System;

namespace AgentCat.Internal.Text;

internal static class Hex
{
    private const string Digits = "0123456789abcdef";

    internal static string Lower(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[2 * i] = Digits[bytes[i] >> 4];
            chars[(2 * i) + 1] = Digits[bytes[i] & 0x0f];
        }

        return new string(chars);
    }
}
