using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Json;

// The bytes JSON.stringify writes. System.Text.Json escapes characters JSON.stringify writes verbatim (non-ASCII,
// <>&') and throws on values JS writes (NaN, the infinities, lone surrogates), so measuring and sending share
// this writer instead.
internal static class JsJson
{
    internal const int MaxDepth = 1000;

    private const double TwoToThe53 = 9007199254740992.0;

    private static readonly JsonDocumentOptions ReadBackOptions = new() { MaxDepth = MaxDepth };

    internal static byte[] ToUtf8(JsonNode? node)
    {
        var output = Output.Writing();
        WriteNode(output, node, 0);
        return output.ToArray();
    }

    internal static long Length(JsonNode? node)
    {
        var output = Output.Counting();
        WriteNode(output, node, 0);
        return output.Length;
    }

    internal static long Length(JsonElement element)
    {
        var output = Output.Counting();
        WriteElement(output, element, 0);
        return output.Length;
    }

    internal static long LengthOfObject(IReadOnlyDictionary<string, JsonElement> properties)
    {
        var output = Output.Counting();
        output.Append((byte)'{');
        var first = true;
        foreach (var property in properties)
        {
            if (property.Value.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            if (!first)
            {
                output.Append((byte)',');
            }

            first = false;
            WriteString(output, property.Key);
            output.Append((byte)':');
            WriteElement(output, property.Value, 1);
        }

        output.Append((byte)'}');
        return output.Length;
    }

    // JSON.parse keeps lone surrogates and the transport decodes invalid UTF-8 to U+FFFD, where GetString throws.
    internal static string ReadString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The element is not a string.");
        }

        return Decode(Unquote(JsonMarshal.GetRawUtf8Value(value)));
    }

    internal static string ReadName(JsonProperty property) => Decode(JsonMarshal.GetRawUtf8PropertyName(property));

    private static string Decode(ReadOnlySpan<byte> source)
    {
        var text = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var codePoint = ReadCodePoint(source, ref i);
            if (codePoint < 0x10000)
            {
                text.Append((char)codePoint);
            }
            else
            {
                text.Append((char)(0xD800 + ((codePoint - 0x10000) >> 10)));
                text.Append((char)(0xDC00 + ((codePoint - 0x10000) & 0x3FF)));
            }
        }

        return text.ToString();
    }

    internal static void WriteNode(Output output, JsonNode? node, int depth)
    {
        switch (node)
        {
            case null:
                output.Append("null");
                return;
            case JsonObject obj when !CanEnumerate(obj):
            {
                using var document = JsonDocument.Parse(obj.ToJsonString(), ReadBackOptions);
                WriteElement(output, document.RootElement, depth);
                return;
            }

            case JsonObject obj:
            {
                EnterContainer(depth);
                output.Append((byte)'{');
                var first = true;
                foreach (var property in obj)
                {
                    if (!first)
                    {
                        output.Append((byte)',');
                    }

                    first = false;
                    WriteString(output, property.Key);
                    output.Append((byte)':');
                    WriteNode(output, property.Value, depth + 1);
                }

                output.Append((byte)'}');
                return;
            }

            case JsonArray array:
                EnterContainer(depth);
                output.Append((byte)'[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        output.Append((byte)',');
                    }

                    WriteNode(output, array[i], depth + 1);
                }

                output.Append((byte)']');
                return;
            default:
                WriteValue(output, (JsonValue)node, depth);
                return;
        }
    }

    // A case-insensitive object over source keys that differ only in case throws when first enumerated, while
    // STJ still writes its source.
    private static bool CanEnumerate(JsonObject obj)
    {
        try
        {
            _ = obj.Count;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void WriteValue(Output output, JsonValue value, int depth)
    {
        if (value.TryGetValue(out JsonElement element))
        {
            WriteElement(output, element, depth);
            return;
        }

        // A value built in code hands back the value it stores when asked for an object.
        var stored = value.TryGetValue(out object? boxed) ? boxed : null;
        switch (stored)
        {
            case string text:
                WriteString(output, text);
                break;
            case bool flag:
                output.Append(flag ? "true" : "false");
                break;
            case double number:
                output.Append(NumberText(number));
                break;
            case float number:
                output.Append(NumberText(number));
                break;
            case decimal number:
                output.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case int or long or short or byte or sbyte or ushort or uint or ulong:
                output.Append(((IFormattable)stored).ToString(null, CultureInfo.InvariantCulture));
                break;
            case DateTime date:
                WriteString(output, DateText(date));
                break;
            case DateTimeOffset date:
                WriteString(output, DateText(date.UtcDateTime));
                break;
            case Guid id:
                WriteString(output, id.ToString("D", CultureInfo.InvariantCulture));
                break;
            case char unit:
                WriteString(output, new string(unit, 1));
                break;
            default:
                WriteSerialized(output, value, depth);
                break;
        }
    }

    // A value of any other type is written by its own type info, and that JSON is then read like parsed JSON.
    private static void WriteSerialized(Output output, JsonValue value, int depth)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            value.WriteTo(writer);
        }

        using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, (int)buffer.Length), ReadBackOptions);
        WriteElement(output, document.RootElement, depth);
    }

    private static void WriteElement(Output output, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                EnterContainer(depth);
                output.Append((byte)'{');
                var first = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!first)
                    {
                        output.Append((byte)',');
                    }

                    first = false;
                    WriteSourceString(output, JsonMarshal.GetRawUtf8PropertyName(property));
                    output.Append((byte)':');
                    WriteElement(output, property.Value, depth + 1);
                }

                output.Append((byte)'}');
                return;
            }

            case JsonValueKind.Array:
            {
                EnterContainer(depth);
                output.Append((byte)'[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        output.Append((byte)',');
                    }

                    first = false;
                    WriteElement(output, item, depth + 1);
                }

                output.Append((byte)']');
                return;
            }

            case JsonValueKind.String:
                WriteSourceString(output, Unquote(JsonMarshal.GetRawUtf8Value(element)));
                return;
            case JsonValueKind.Number:
                output.Append(JsonMarshal.GetRawUtf8Value(element));
                return;
            case JsonValueKind.True:
                output.Append("true");
                return;
            case JsonValueKind.False:
                output.Append("false");
                return;
            case JsonValueKind.Null:
                output.Append("null");
                return;
            default:
                throw new ArgumentException("The element holds no JSON value.", nameof(element));
        }
    }

    internal static void CheckDepth(int depth)
    {
        if (depth >= MaxDepth)
        {
            throw new InvalidOperationException("JSON nesting exceeds " + MaxDepth + " levels.");
        }
    }

    private static void EnterContainer(int depth)
    {
        CheckDepth(depth);
        RuntimeHelpers.EnsureSufficientExecutionStack();
    }

    private static ReadOnlySpan<byte> Unquote(ReadOnlySpan<byte> quoted) => quoted.Slice(1, quoted.Length - 2);

    internal static void WriteString(Output output, string text)
    {
        output.Append((byte)'"');
        for (var i = 0; i < text.Length; i++)
        {
            int codePoint = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }

            WriteCodePoint(output, codePoint);
        }

        output.Append((byte)'"');
    }

    private static void WriteSourceString(Output output, ReadOnlySpan<byte> source)
    {
        output.Append((byte)'"');
        var i = 0;
        while (i < source.Length)
        {
            WriteCodePoint(output, ReadCodePoint(source, ref i));
        }

        output.Append((byte)'"');
    }

    // A lone surrogate arrives as itself and is written as an escape, as the well-formed JSON.stringify does.
    private static void WriteCodePoint(Output output, int codePoint)
    {
        switch (codePoint)
        {
            case '"':
                output.Append("\\\"");
                return;
            case '\\':
                output.Append("\\\\");
                return;
            case '\b':
                output.Append("\\b");
                return;
            case '\f':
                output.Append("\\f");
                return;
            case '\n':
                output.Append("\\n");
                return;
            case '\r':
                output.Append("\\r");
                return;
            case '\t':
                output.Append("\\t");
                return;
        }

        if (codePoint < 0x20 || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
        {
            Span<byte> unit = stackalloc byte[] { (byte)(codePoint >> 8), (byte)codePoint };
            output.Append("\\u" + Hex.Lower(unit));
        }
        else if (codePoint < 0x80)
        {
            output.Append((byte)codePoint);
        }
        else if (codePoint < 0x800)
        {
            output.Append((byte)(0xC0 | (codePoint >> 6)));
            output.Append((byte)(0x80 | (codePoint & 0x3F)));
        }
        else if (codePoint < 0x10000)
        {
            output.Append((byte)(0xE0 | (codePoint >> 12)));
            output.Append((byte)(0x80 | ((codePoint >> 6) & 0x3F)));
            output.Append((byte)(0x80 | (codePoint & 0x3F)));
        }
        else
        {
            output.Append((byte)(0xF0 | (codePoint >> 18)));
            output.Append((byte)(0x80 | ((codePoint >> 12) & 0x3F)));
            output.Append((byte)(0x80 | ((codePoint >> 6) & 0x3F)));
            output.Append((byte)(0x80 | (codePoint & 0x3F)));
        }
    }

    // One code point of a JSON string's source as JSON.parse reads it after a WHATWG UTF-8 decode: an escaped
    // surrogate pair joins, any other escaped surrogate stays lone, and invalid UTF-8 becomes U+FFFD.
    private static int ReadCodePoint(ReadOnlySpan<byte> source, ref int i)
    {
        var lead = source[i];
        if (lead != (byte)'\\')
        {
            if (lead < 0x80)
            {
                i++;
                return lead;
            }

            return ReadUtf8(source, ref i);
        }

        var escape = source[i + 1];
        if (escape != (byte)'u')
        {
            i += 2;
            return Unescape(escape);
        }

        var unit = ParseHex4(source.Slice(i + 2, 4));
        i += 6;
        if (unit >= 0xD800 && unit <= 0xDBFF && i + 6 <= source.Length && source[i] == (byte)'\\' && source[i + 1] == (byte)'u')
        {
            var low = ParseHex4(source.Slice(i + 2, 4));
            if (low >= 0xDC00 && low <= 0xDFFF)
            {
                i += 6;
                return 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
            }
        }

        return unit;
    }

    // Each maximal invalid subpart becomes one U+FFFD, as TextDecoder and Node's Buffer decode it.
    private static int ReadUtf8(ReadOnlySpan<byte> source, ref int i)
    {
        var lead = source[i++];
        int continuations;
        int codePoint;
        var lower = 0x80;
        var upper = 0xBF;
        if (lead >= 0xC2 && lead <= 0xDF)
        {
            continuations = 1;
            codePoint = lead & 0x1F;
        }
        else if (lead >= 0xE0 && lead <= 0xEF)
        {
            continuations = 2;
            codePoint = lead & 0x0F;
            lower = lead == 0xE0 ? 0xA0 : lower;
            upper = lead == 0xED ? 0x9F : upper;
        }
        else if (lead >= 0xF0 && lead <= 0xF4)
        {
            continuations = 3;
            codePoint = lead & 0x07;
            lower = lead == 0xF0 ? 0x90 : lower;
            upper = lead == 0xF4 ? 0x8F : upper;
        }
        else
        {
            return 0xFFFD;
        }

        for (var seen = 0; seen < continuations; seen++)
        {
            if (i >= source.Length || source[i] < lower || source[i] > upper)
            {
                return 0xFFFD;
            }

            codePoint = (codePoint << 6) | (source[i++] & 0x3F);
            lower = 0x80;
            upper = 0xBF;
        }

        return codePoint;
    }

    private static int ParseHex4(ReadOnlySpan<byte> hex)
    {
        var value = 0;
        foreach (var digit in hex)
        {
            value = (value << 4) | digit switch
            {
                >= (byte)'0' and <= (byte)'9' => digit - '0',
                >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
                _ => digit - 'A' + 10,
            };
        }

        return value;
    }

    private static int Unescape(byte escape) => escape switch
    {
        (byte)'b' => '\b',
        (byte)'f' => '\f',
        (byte)'n' => '\n',
        (byte)'r' => '\r',
        (byte)'t' => '\t',
        _ => escape,
    };

    internal static string DateText(DateTime value)
        => (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value)
            .ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'", CultureInfo.InvariantCulture);

    // ECMAScript Number::toString, with null for the values JSON cannot hold.
    internal static string NumberText(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "null";
        }

        if (Math.Abs(value) < TwoToThe53 && Math.Floor(value) == value)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        var text = new StringBuilder(24);
        if (value < 0)
        {
            text.Append('-');
            value = -value;
        }

        var digits = ShortestDigits(value, out var point);
        var count = digits.Length;
        if (count <= point && point <= 21)
        {
            text.Append(digits).Append('0', point - count);
        }
        else if (point > 0 && point <= 21)
        {
            text.Append(digits, 0, point).Append('.').Append(digits, point, count - point);
        }
        else if (point > -6 && point <= 0)
        {
            text.Append("0.").Append('0', -point).Append(digits);
        }
        else
        {
            text.Append(digits[0]);
            if (count > 1)
            {
                text.Append('.').Append(digits, 1, count - 1);
            }

            text.Append(point > 0 ? "e+" : "e-").Append(Math.Abs(point - 1).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    // The fewest decimal digits that read back as the positive finite value, nearest to it with ties to an even
    // digit (Steele and White's free-format algorithm on exact integers): value ≈ 0.digits × 10^point.
    private static string ShortestDigits(double value, out int point)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var biased = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xFFFFFFFFFFFFFL;
        var mantissa = biased == 0 ? fraction : fraction | (1L << 52);
        var exponent = (biased == 0 ? 1 : biased) - 1075;
        var lowerGapIsHalf = fraction == 0 && biased > 1;
        var boundsRoundBack = (mantissa & 1) == 0;

        // value = r / s; the next doubles up and down lie 2·mPlus / s and 2·mMinus / s away.
        BigInteger r;
        BigInteger s;
        BigInteger mPlus;
        BigInteger mMinus;
        var scale = lowerGapIsHalf ? 4 : 2;
        if (exponent >= 0)
        {
            var gap = BigInteger.One << exponent;
            r = mantissa * gap * scale;
            s = scale;
            mPlus = gap * (scale / 2);
            mMinus = gap;
        }
        else
        {
            r = new BigInteger(mantissa) * scale;
            s = (BigInteger.One << -exponent) * scale;
            mPlus = scale / 2;
            mMinus = BigInteger.One;
        }

        point = (int)Math.Ceiling(Math.Log10(value) - 1e-10);
        if (point >= 0)
        {
            s *= BigInteger.Pow(10, point);
        }
        else
        {
            var power = BigInteger.Pow(10, -point);
            r *= power;
            mPlus *= power;
            mMinus *= power;
        }

        if (boundsRoundBack ? r + mPlus >= s : r + mPlus > s)
        {
            s *= 10;
            point++;
        }

        var digits = new StringBuilder(17);
        while (true)
        {
            r *= 10;
            mPlus *= 10;
            mMinus *= 10;
            var digit = (int)BigInteger.DivRem(r, s, out r);
            var low = boundsRoundBack ? r <= mMinus : r < mMinus;
            var high = boundsRoundBack ? r + mPlus >= s : r + mPlus > s;
            if (!low && !high)
            {
                digits.Append((char)('0' + digit));
                continue;
            }

            var roundUp = high;
            if (low && high)
            {
                var half = (r * 2).CompareTo(s);
                roundUp = half > 0 || (half == 0 && digit % 2 == 1);
            }

            digits.Append((char)('0' + digit + (roundUp ? 1 : 0)));
            return digits.ToString();
        }
    }

    // Counts the bytes written, and keeps them when it has a buffer.
    internal sealed class Output
    {
        private byte[]? _buffer;

        private Output(byte[]? buffer) => _buffer = buffer;

        internal long Length { get; private set; }

        internal static Output Counting() => new(null);

        internal static Output Writing() => new(new byte[256]);

        internal void Append(byte value)
        {
            if (_buffer is not null)
            {
                Reserve(1);
                _buffer[Length] = value;
            }

            Length++;
        }

        internal void Append(ReadOnlySpan<byte> bytes)
        {
            if (_buffer is not null)
            {
                Reserve(bytes.Length);
                bytes.CopyTo(_buffer.AsSpan((int)Length));
            }

            Length += bytes.Length;
        }

        internal void Append(string ascii)
        {
            foreach (var c in ascii)
            {
                Append((byte)c);
            }
        }

        internal byte[] ToArray()
        {
            var bytes = new byte[Length];
            Array.Copy(_buffer!, bytes, Length);
            return bytes;
        }

        private void Reserve(int count)
        {
            if (Length + count > _buffer!.Length)
            {
                Array.Resize(ref _buffer, (int)Math.Max(_buffer.Length * 2L, Length + count));
            }
        }
    }
}
