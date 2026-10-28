using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Json;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Events;

internal static class EventSanitizer
{
    internal const int Base64ScanMinLength = 10240;

    private const string BinaryDataMarker = "[binary data redacted - not supported by AgentCat]";

    internal static void Sanitize(AgentCatEvent e)
    {
        var response = e.Response is null ? null : SanitizeResponse(e.Response);
        var parameters = JsonTreeCopy.Copy(e.Parameters, ScanString);

        e.Response = response;
        e.Parameters = parameters;
    }

    private static JsonNode? SanitizeResponse(JsonNode response)
    {
        var node = JsonTreeCopy.Expand(response);
        if (node is not JsonObject obj)
        {
            return JsonTreeCopy.Copy(node);
        }

        var result = new JsonObject(new JsonNodeOptions());
        foreach (var property in obj.ToArray())
        {
            var value = property.Value;
            if (value is null)
            {
                result.Add(property.Key, null);
                continue;
            }

            var child = JsonTreeCopy.Expand(value);
            if (property.Key == "content" && child is JsonArray blocks)
            {
                var sanitized = new JsonArray(new JsonNodeOptions());
                foreach (var block in blocks.ToArray())
                {
                    sanitized.Add(block is null ? null : SanitizeBlock(block));
                }

                result.Add(property.Key, sanitized);
            }
            else if (property.Key == "structuredContent" && child is JsonObject or JsonArray)
            {
                result.Add(property.Key, JsonTreeCopy.Copy(child, ScanString));
            }
            else
            {
                result.Add(property.Key, JsonTreeCopy.Copy(child));
            }
        }

        return result;
    }

    private static JsonNode? SanitizeBlock(JsonNode original)
    {
        var block = JsonTreeCopy.Expand(original);
        if (block is JsonArray)
        {
            return TextBlock(UnsupportedMarker("undefined"));
        }

        if (block is not JsonObject obj)
        {
            return JsonTreeCopy.Copy(block);
        }

        var hasType = obj.TryGetPropertyValue("type", out var typeNode);
        var type = typeNode is null ? null : JsonTreeCopy.Expand(typeNode);
        var typeName = type is JsonValue v && v.GetValueKind() == JsonValueKind.String ? JsonTreeCopy.StringOf(v) : null;

        switch (typeName)
        {
            case "text":
            case "resource_link":
                return JsonTreeCopy.Copy(block);
            case "image":
                return TextBlock("[image content redacted - not supported by AgentCat]");
            case "audio":
                return TextBlock("[audio content redacted - not supported by AgentCat]");
            case "resource":
                return obj.TryGetPropertyValue("resource", out var resource)
                    && resource is not null
                    && JsonTreeCopy.Expand(resource) is JsonObject resourceObj
                    && resourceObj.ContainsKey("blob")
                        ? TextBlock("[binary resource content redacted - not supported by AgentCat]")
                        : JsonTreeCopy.Copy(block);
            default:
                return TextBlock(UnsupportedMarker(hasType ? JsSemantics.ToJsString(type) : "undefined"));
        }
    }

    private static string UnsupportedMarker(string typeName)
        => "[unsupported content type \"" + typeName + "\" redacted - not supported by AgentCat]";

    private static JsonObject TextBlock(string text)
        => new(new JsonNodeOptions()) { ["type"] = "text", ["text"] = text };

    internal static bool IsLargeBase64(string s)
    {
        if (s.Length < Base64ScanMinLength)
        {
            return false;
        }

        var bodyLength = 0;
        var i = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '+' || c == '/' || c == '\n' || c == '\r'))
            {
                break;
            }

            bodyLength++;
        }

        if (bodyLength == 0)
        {
            return false;
        }

        for (; i < s.Length; i++)
        {
            if (s[i] != '=')
            {
                return false;
            }
        }

        return true;
    }

    private static string ScanString(string text) => IsLargeBase64(text) ? BinaryDataMarker : text;
}
