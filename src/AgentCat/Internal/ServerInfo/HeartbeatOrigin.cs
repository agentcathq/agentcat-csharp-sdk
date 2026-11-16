using System;
using System.Security.Claims;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.ServerInfo;

// What the heartbeat keeps of the request that started it: the parts a list handler reads from its context.
internal sealed class HeartbeatOrigin
{
    internal HeartbeatOrigin(McpServer server, RequestId requestId, ClaimsPrincipal? user, IServiceProvider? services)
    {
        Server = server;
        RequestId = requestId;
        User = user;
        Services = services;
    }

    internal McpServer Server { get; }

    internal RequestId RequestId { get; }

    internal ClaimsPrincipal? User { get; }

    internal IServiceProvider? Services { get; }
}
