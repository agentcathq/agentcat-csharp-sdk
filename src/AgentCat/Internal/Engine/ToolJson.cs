using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentCat.Internal.Engine;

internal static class ToolJson
{
    private static readonly JsonTypeInfo ToolInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));
    private static readonly JsonTypeInfo ListInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListToolsResult));

    internal static JsonObject? ToNode(Tool? tool)
    {
        if (tool is null)
        {
            return null;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(tool, ToolInfo);
        return JsonNode.Parse(bytes, new JsonNodeOptions()) as JsonObject;
    }

    internal static Tool? FromNode(JsonObject? node)
        => node is null ? null : (Tool?)JsonSerializer.Deserialize(node.ToJsonString(), ToolInfo);

    internal static ListToolsResult CopyWith(ListToolsResult original, IList<Tool?> tools)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(original, ListInfo);
        var copy = (ListToolsResult)JsonSerializer.Deserialize(bytes, ListInfo)!;
        var list = new List<Tool>(tools.Count);
        foreach (var tool in tools)
        {
            list.Add(tool!);
        }

        copy.Tools = list;
        return copy;
    }
}
