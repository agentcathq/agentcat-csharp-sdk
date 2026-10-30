using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Handles;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

// The traced tool call whose handler runs on the current async flow: the handler and the work it starts, also when that
// work runs on after the call has returned. A custom event that this flow publishes through the call's own server,
// without a SessionId, takes the session of the call's event.
internal sealed class EnclosingToolCall
{
    private static readonly AsyncLocal<EnclosingToolCall?> Current = new();

    private readonly McpServer _server;

    private EnclosingToolCall(McpServer server, HandleResolution resolution, PendingHooks? hooks)
    {
        _server = server;
        if (resolution.HookMode && hooks is { ResolvesSession: true })
        {
            SessionId = string.Empty;
            Prepare = hooks.ApplySessionAsync;
        }
        else
        {
            SessionId = resolution.SessionId;
        }
    }

    // The session of the call's event when the request path knows it. Empty for an invalid or foreign session_id, and in
    // hook mode, where Prepare gives the session.
    internal string SessionId { get; }

    // Hook mode: the pipeline stage that gives a custom event its session from the call's hook value.
    internal Func<AgentCatEvent, CancellationToken, ValueTask>? Prepare { get; }

    // Makes the call the enclosing call of the caller's async flow from here on. The caller is an async method, which
    // gives its own caller back that caller's execution context, so the value never reaches further up.
    internal static void Enter(McpServer? server, HandleResolution resolution, PendingHooks? hooks)
    {
        try
        {
            Current.Value = server is null ? null : new EnclosingToolCall(server, resolution, hooks);
        }
        catch (Exception)
        {
        }
    }

    // The enclosing call when its server is this same instance; null outside a traced tool call, for another server, or
    // when the lookup fails.
    internal static EnclosingToolCall? Of(McpServer server)
    {
        try
        {
            return Current.Value is { } call && ReferenceEquals(call._server, server) ? call : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
