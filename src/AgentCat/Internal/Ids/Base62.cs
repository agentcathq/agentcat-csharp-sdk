using System;
using System.Globalization;

namespace AgentCat.Internal.Ids;

internal static class Base62
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    internal static string Encode(ReadOnlySpan<byte> bytes, int fixedLength)
    {
        var digits = new int[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            digits[i] = bytes[i];
        }

        var values = ConvertBase(digits, digits.Length, 256, 62, fixedLength);
        var chars = new char[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            chars[i] = Alphabet[values[i]];
        }

        return new string(chars);
    }

    // Like the TS decoder: no character validation, one digit per code point (a surrogate pair
    // contributes its high surrogate), and out-of-range digits flow through the arithmetic.
    internal static byte[] Decode(string text, int fixedLength)
    {
        var digits = new int[text.Length];
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            int code = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }

            digits[count++] = code < 58 ? code - 48 : code < 91 ? code - 55 : code - 61;
        }

        var values = ConvertBase(digits, count, 62, 256, fixedLength);
        var bytes = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            bytes[i] = (byte)(values[i] & 0xFF);
        }

        return bytes;
    }

    // Floor quotients and sign-keeping remainders, as in JS; quotients overwrite consumed digits.
    private static int[] ConvertBase(int[] digits, int length, int from, int to, int fixedLength)
    {
        var result = new int[fixedLength];
        var offset = fixedLength;
        var remaining = length;
        while (remaining > 0)
        {
            if (offset == 0)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Fixed length of {0} is too small, expected at least {1}",
                    fixedLength,
                    (int)Math.Ceiling(length * Math.Log(from) / Math.Log(to))));
            }

            var quotients = 0;
            var remainder = 0;
            for (var i = 0; i < remaining; i++)
            {
                var acc = digits[i] + (remainder * from);
                var quotient = (int)Math.Floor(acc / (double)to);
                remainder = acc % to;
                if (quotients > 0 || quotient > 0)
                {
                    digits[quotients++] = quotient;
                }
            }

            result[--offset] = remainder;
            remaining = quotients;
        }

        return result;
    }
}
