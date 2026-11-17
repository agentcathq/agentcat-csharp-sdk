using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.ServerInfo;

// What the heartbeat keeps of the request that started it: the server, the request ID and the user for the context
// of a list call, and the scope factory of the application. It never keeps the service provider of the request.
internal sealed class HeartbeatOrigin
{
    internal HeartbeatOrigin(McpServer server, RequestId requestId, ClaimsPrincipal? user, IServiceScopeFactory? scopeFactory)
    {
        Server = server;
        RequestId = requestId;
        User = user;
        ScopeFactory = scopeFactory;
    }

    internal McpServer Server { get; }

    internal RequestId RequestId { get; }

    internal ClaimsPrincipal? User { get; }

    // Null on a server that was built without dependency injection.
    internal IServiceScopeFactory? ScopeFactory { get; }
}
