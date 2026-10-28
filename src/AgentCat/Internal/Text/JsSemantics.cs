using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Json;

namespace AgentCat.Internal.Text;

internal static class JsSemantics
{
    internal static string Trim(string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsWhiteSpaceOrLineTerminator(s[start]))
        {
            start++;
        }

        while (end > start && IsWhiteSpaceOrLineTerminator(s[end - 1]))
        {
            end--;
        }

        return start == 0 && end == s.Length ? s : s.Substring(start, end - start);
    }

    internal static bool IsTruthy(JsonNode? node)
    {
        if (node is null)
        {
            return false;
        }

        switch (node.GetValueKind())
        {
            case JsonValueKind.Object:
            case JsonValueKind.Array:
            case JsonValueKind.True:
                return true;
            case JsonValueKind.String:
                return IsNonEmptyString(node.AsValue());
            case JsonValueKind.Number:
                return !double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || number != 0;
            default:
                return false;
        }
    }

    // JS String(value). Arrays join their elements with "," as Array.prototype.join does (nested arrays
    // flatten, null elements render empty), walked without recursion.
    internal static string ToJsString(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        node = JsonTreeCopy.Expand(node);
        if (node is not JsonArray root)
        {
            return ScalarOrObjectString(node);
        }

        var text = new StringBuilder();
        var stack = new Stack<KeyValuePair<JsonArray, int>>();
        stack.Push(new KeyValuePair<JsonArray, int>(root, 0));
        while (stack.Count > 0)
        {
            var top = stack.Pop();
            var items = top.Key;
            var next = top.Value;
            if (next == items.Count)
            {
                continue;
            }

            if (next > 0)
            {
                text.Append(',');
            }

            stack.Push(new KeyValuePair<JsonArray, int>(items, next + 1));
            var item = items[next];
            if (item is null)
            {
                continue;
            }

            item = JsonTreeCopy.Expand(item);
            if (item is JsonArray nested)
            {
                stack.Push(new KeyValuePair<JsonArray, int>(nested, 0));
            }
            else if (item.GetValueKind() != JsonValueKind.Null)
            {
                text.Append(ScalarOrObjectString(item));
            }
        }

        return text.ToString();
    }

    // How Array.prototype.join renders one element: null and undefined render empty.
    internal static string ToJsJoinElement(JsonNode? node)
        => node is null || node.GetValueKind() == JsonValueKind.Null ? string.Empty : ToJsString(node);

    private static string ScalarOrObjectString(JsonNode node)
    {
        if (node is JsonObject)
        {
            return "[object Object]";
        }

        var value = (JsonValue)node;
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                return JsonTreeCopy.StringOf(value);
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Null:
                return "null";
        }

        return NonFiniteName(value) ?? value.ToJsonString();
    }

    // JS String() of a number built in code that JSON cannot hold ("NaN", "Infinity", "-Infinity"); null otherwise.
    internal static string? NonFiniteName(JsonValue value)
    {
        if (value.TryGetValue(out JsonElement _))
        {
            return null;
        }

        var number = value.TryGetValue(out double d) ? d : value.TryGetValue(out float f) ? f : 0;
        return double.IsNaN(number) ? "NaN" : double.IsInfinity(number) ? (number > 0 ? "Infinity" : "-Infinity") : null;
    }

    private static bool IsNonEmptyString(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            return JsonMarshal.GetRawUtf8Value(element).Length > 2;
        }

        return !value.TryGetValue<string>(out var text) || text.Length > 0;
    }

    // ECMAScript WhiteSpace and LineTerminator: the Zs set is listed in full so every target framework agrees.
    internal static bool IsWhiteSpaceOrLineTerminator(char c) => c switch
    {
        '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0' or '\u1680' => true,
        >= '\u2000' and <= '\u200A' => true,
        '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' => true,
        _ => false,
    };
}
