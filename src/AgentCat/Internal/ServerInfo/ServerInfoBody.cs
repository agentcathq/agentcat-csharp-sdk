using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Api.Model;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Json;

namespace AgentCat.Internal.ServerInfo;

// One heartbeat: the six keys of the server info request.
internal sealed class ServerInfoBody
{
    private const string InputSchemaKey = "inputSchema";
    private const string OutputSchemaKey = "outputSchema";

    private static readonly PublishServerInfoRequestJsonConverter Converter = new();

    internal ServerInfoBody(string projectId, string serverName, string serverVersion, string serverTitle, string instructions, JsonObject features)
    {
        ProjectId = projectId;
        ServerName = serverName;
        ServerVersion = serverVersion;
        ServerTitle = serverTitle;
        Instructions = instructions;
        Features = features;
    }

    internal string ProjectId { get; }

    internal string ServerName { get; }

    internal string ServerVersion { get; }

    internal string ServerTitle { get; }

    internal string Instructions { get; }

    internal JsonObject Features { get; }

    internal int ToolCount => Features[ServerInfoLists.ToolsKey] is JsonArray tools ? tools.Count : 0;

    internal byte[] ToUtf8()
    {
        var request = new PublishServerInfoRequest(projectId: ProjectId)
        {
            ServerName = ServerName,
            ServerVersion = ServerVersion,
            ServerTitle = ServerTitle,
            Instructions = Instructions,
            Features = new RawJson(JsJson.ToUtf8(Features)),
        };

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WireBody.WriterOptions))
        {
            Converter.Write(writer, request, WireBody.SerializerOptions);
        }

        return buffer.ToArray();
    }

    // The same heartbeat with no input schema and no output schema on any tool. Every other field stays.
    internal ServerInfoBody WithoutToolSchemas()
    {
        var features = new JsonObject();
        foreach (var list in Features)
        {
            var copy = JsonTreeCopy.Copy(list.Value);
            if (list.Key == ServerInfoLists.ToolsKey && copy is JsonArray tools)
            {
                foreach (var tool in tools)
                {
                    if (tool is JsonObject fields)
                    {
                        fields.Remove(InputSchemaKey);
                        fields.Remove(OutputSchemaKey);
                    }
                }
            }

            features[list.Key] = copy;
        }

        return new ServerInfoBody(ProjectId, ServerName, ServerVersion, ServerTitle, Instructions, features);
    }
}
