using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AgentCat.Internal.Json;
using AgentCat.Internal.Text;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal static class ResultDecorator
{
    private static readonly JsonTypeInfo ResultInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult));

    // MCP 2.x writes resultType on the object that the filters return, after they return, while the event still reads
    // that object on the pipeline, so every result there goes out as a copy. MCP 1.x writes nothing to it.
    private static readonly bool CopiesEveryResult = typeof(McpServer).Assembly.GetName().Version?.Major != 1;

    internal static CallToolResult Decorate(CallToolResult original, string? sessionText, JsonObject? mint, bool mirror)
    {
        var prepend = sessionText is not null && original.Content is not null;
        var mirrored = mirror
            && mint is not null
            && original.StructuredContent is { ValueKind: JsonValueKind.Object } structured
            && !HasSessionKey(structured);
        if (!prepend && !mirrored && !CopiesEveryResult)
        {
            return original;
        }

        var copy = (CallToolResult)JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(original, ResultInfo), ResultInfo)!;
        if (original.Content is null)
        {
            copy.Content = null!;
        }
        else if (prepend)
        {
            var content = new List<ContentBlock>(copy.Content.Count + 1) { new TextContentBlock { Text = sessionText! } };
            content.AddRange(copy.Content);
            copy.Content = content;
        }

        if (mirrored && copy.StructuredContent is { } copied)
        {
            copy.StructuredContent = Mirror(copied, mint!);
        }

        return copy;
    }

    private static bool HasSessionKey(JsonElement structured)
    {
        foreach (var property in structured.EnumerateObject())
        {
            if (property.NameEquals(WireNames.McpSessionKey))
            {
                return true;
            }
        }

        return false;
    }

    // Written rather than built as a JsonObject: a customer object may repeat a key, which JsonObject rejects.
    private static JsonElement Mirror(JsonElement structured, JsonObject mint)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(WireNames.McpSessionKey);
            mint.WriteTo(writer);
            foreach (var property in structured.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        var reader = new Utf8JsonReader(
            new ReadOnlySpan<byte>(buffer.GetBuffer(), 0, (int)buffer.Length),
            new JsonReaderOptions { MaxDepth = JsJson.MaxDepth });
        return JsonElement.ParseValue(ref reader);
    }
}
