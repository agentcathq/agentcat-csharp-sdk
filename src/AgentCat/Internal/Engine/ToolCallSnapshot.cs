using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using AgentCat.Internal.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal static class ToolCallSnapshot
{
    internal static AgentCatToolCall Build(RequestContext<CallToolRequestParams> context, RequestCapture? request = null)
    {
        var parameters = context.Params;
        var arguments = parameters?.Arguments;
        var (clientName, clientVersion) = ClientIdentity.For(context);
        var server = context.Server;
        return new AgentCatToolCall(
            parameters?.Name ?? string.Empty,
            arguments as IReadOnlyDictionary<string, JsonElement>
                ?? (arguments is null ? null : new ReadOnlyDictionary<string, JsonElement>(arguments)),
            parameters?.Meta,
            clientName,
            clientVersion,
            ClientIdentity.ProtocolVersion(context) ?? server?.NegotiatedProtocolVersion,
            server?.SessionId,
            context.User,
            context,
            request?.Headers);
    }
}
