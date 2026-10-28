using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Json;
using AgentCat.Internal.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

// The SDK parses _meta as a case-insensitive object, so keys that collide in it make every read throw; such a
// _meta is read as absent.
internal static class ClientIdentity
{
    internal static (string? Name, string? Version) For(RequestContext<CallToolRequestParams> context)
    {
        if (FromMeta(context) is { } fromMeta)
        {
            return fromMeta;
        }

        var session = context.Server?.ClientInfo;
        return (session?.Name, session?.Version);
    }

    internal static string? ProtocolVersion(RequestContext<CallToolRequestParams> context)
    {
        try
        {
            if (context.Params?.Meta is { } meta
                && meta.TryGetPropertyValue(WireNames.MetaProtocolVersionKey, out var node))
            {
                var value = StringOf(node);
                return value is { Length: > 0 } ? value : null;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static (string? Name, string? Version)? FromMeta(RequestContext<CallToolRequestParams> context)
    {
        try
        {
            if (context.Params?.Meta is { } meta
                && meta.TryGetPropertyValue(WireNames.MetaClientInfoKey, out var node)
                && node is JsonObject info)
            {
                info.TryGetPropertyValue("name", out var name);
                info.TryGetPropertyValue("version", out var version);
                var narrowedName = StringOf(name);
                var narrowedVersion = StringOf(version);
                if (narrowedName is not null || narrowedVersion is not null)
                {
                    return (narrowedName, narrowedVersion);
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static string? StringOf(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out JsonElement element))
        {
            return element.ValueKind == JsonValueKind.String ? JsJson.ReadString(element) : null;
        }

        return value.TryGetValue(out string? text) ? text : null;
    }
}
