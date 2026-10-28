using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AgentCat.Internal.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal readonly struct ExtraSnapshot
{
    internal ExtraSnapshot(string? sessionId, RequestId requestId, JsonObject? meta, RequestCapture? request = null)
    {
        SessionId = sessionId;
        RequestId = requestId;
        Meta = meta;
        Request = request;
    }

    internal string? SessionId { get; }

    internal RequestId RequestId { get; }

    internal JsonObject? Meta { get; }

    internal RequestCapture? Request { get; }
}

internal static class ExtraProjection
{
    private static readonly JsonTypeInfo RequestIdInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(RequestId));

    internal static ExtraSnapshot Capture(RequestContext<CallToolRequestParams> context, RequestCapture? request = null)
        => new(context.Server?.SessionId, context.JsonRpcRequest.Id, context.Params?.Meta, request);

    internal static JsonObject Project(RequestContext<CallToolRequestParams> context) => Project(Capture(context));

    internal static JsonObject Project(ExtraSnapshot snapshot)
    {
        var extra = new JsonObject();
        if (snapshot.SessionId is not null)
        {
            extra["sessionId"] = snapshot.SessionId;
        }

        extra["requestId"] = EventNodes.FromElement(JsonSerializer.SerializeToElement(snapshot.RequestId, RequestIdInfo));
        if (snapshot.Meta is not null)
        {
            extra["_meta"] = EventNodes.FromJson(snapshot.Meta);
        }

        if (snapshot.Request is { } request)
        {
            var headers = new JsonObject();
            foreach (var header in request.Headers)
            {
                headers[header.Key] = header.Value;
            }

            var info = new JsonObject { ["headers"] = headers };
            if (request.Url is not null)
            {
                info["url"] = request.Url;
            }

            extra["requestInfo"] = info;
        }

        return extra;
    }
}
