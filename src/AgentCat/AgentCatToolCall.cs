using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat;

/// <summary>A snapshot of a <c>tools/call</c> request, passed to AgentCat hooks.</summary>
/// <remarks>
/// <see cref="ToolName"/>, <see cref="Arguments"/> and <see cref="Meta"/> are copies taken when the call starts, so
/// later changes to the request do not change them. Each hook gets its own copy of <see cref="Meta"/>.
/// <see cref="Request"/> is the live request context, not a copy.
/// </remarks>
public sealed class AgentCatToolCall
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    internal AgentCatToolCall(
        string toolName,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        JsonObject? meta,
        string? clientName,
        string? clientVersion,
        string? protocolVersion,
        string? transportSessionId,
        ClaimsPrincipal? user,
        RequestContext<CallToolRequestParams> request,
        IReadOnlyList<KeyValuePair<string, string>>? headers = null)
    {
        ToolName = toolName;
        var copy = new Dictionary<string, JsonElement>();
        if (arguments is not null)
        {
            foreach (var pair in arguments)
            {
                copy[pair.Key] = pair.Value;
            }
        }

        Arguments = new ReadOnlyDictionary<string, JsonElement>(copy);
        Meta = CopyOf(meta);
        ClientName = clientName;
        ClientVersion = clientVersion;
        ProtocolVersion = protocolVersion;
        TransportSessionId = transportSessionId;
        User = user;
        Headers = HeadersOf(headers);
        Request = request;
    }

    private static IReadOnlyDictionary<string, string> HeadersOf(IReadOnlyList<KeyValuePair<string, string>>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return NoHeaders;
        }

        var copy = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            copy[header.Key] = header.Value;
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private AgentCatToolCall(AgentCatToolCall source)
    {
        ToolName = source.ToolName;
        Arguments = source.Arguments;
        Meta = CopyOf(source.Meta);
        ClientName = source.ClientName;
        ClientVersion = source.ClientVersion;
        ProtocolVersion = source.ProtocolVersion;
        TransportSessionId = source.TransportSessionId;
        User = source.User;
        Headers = source.Headers;
        Request = source.Request;
    }

    /// <summary>Name of the tool being called.</summary>
    public string ToolName { get; }

    /// <summary>The raw call arguments, including parameters AgentCat injected. Empty when the call has none.</summary>
    public IReadOnlyDictionary<string, JsonElement> Arguments { get; }

    /// <summary>A copy of the request <c>_meta</c> object, owned by the hook that receives it.</summary>
    /// <remarks>
    /// Null when the request has no <c>_meta</c>, or when its keys collide: the MCP SDK reads <c>_meta</c> without
    /// regard to case, so <c>"trace"</c> and <c>"Trace"</c> in one request make it unreadable.
    /// </remarks>
    public JsonObject? Meta { get; }

    /// <summary>Name of the connected MCP client, if known.</summary>
    public string? ClientName { get; }

    /// <summary>Version of the connected MCP client, if known.</summary>
    public string? ClientVersion { get; }

    /// <summary>MCP protocol version negotiated with the client, if known.</summary>
    public string? ProtocolVersion { get; }

    /// <summary>Identifier of the transport session (the <c>Mcp-Session-Id</c> value), if the transport has one.</summary>
    public string? TransportSessionId { get; }

    /// <summary>The authenticated user of the request, if any.</summary>
    public ClaimsPrincipal? User { get; }

    /// <summary>The HTTP request headers of the call, read when the call starts.</summary>
    /// <remarks>
    /// Names are lower-case and the lookup ignores case, so <c>Headers["User-Agent"]</c> and
    /// <c>Headers["user-agent"]</c> find the same value. A header sent several times has one value (joined with
    /// <c>", "</c>, or <c>"; "</c> for <c>cookie</c>). The dictionary is empty when no request is available: stdio,
    /// .NET Framework and <c>netstandard2.0</c> hosts, <c>PerSessionExecutionContext = true</c>, calls whose client opts
    /// in to MCP 2.x tasks, and <c>Track</c> without a registered <c>IHttpContextAccessor</c>. Under the legacy SSE transport the
    /// request is the session's <c>GET /sse</c> request.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The live request context of the call.</summary>
    /// <remarks>
    /// Request-scoped services can be disposed after a hook's first <c>await</c>. Read what you need from the request before awaiting.
    /// While the tool runs, <c>Request.Params.Arguments</c> holds the arguments without the parameters AgentCat injected;
    /// <see cref="Arguments"/> keeps them.
    /// </remarks>
    public RequestContext<CallToolRequestParams> Request { get; }

    internal AgentCatToolCall WithOwnMeta() => Meta is null ? this : new AgentCatToolCall(this);

    // A clone of a parsed object stays lazy, so the copy is walked once here: keys that collide in the SDK's
    // case-insensitive _meta throw on the first read, and such a _meta is treated as absent.
    private static JsonObject? CopyOf(JsonObject? meta)
    {
        if (meta is null)
        {
            return null;
        }

        try
        {
            var copy = meta.DeepClone().AsObject();
            var pending = new Stack<JsonNode>();
            pending.Push(copy);
            while (pending.Count > 0)
            {
                IEnumerable<JsonNode?> children = pending.Pop() switch
                {
                    JsonObject node => node.Select(property => property.Value),
                    JsonArray node => node,
                    _ => Array.Empty<JsonNode?>(),
                };
                foreach (var child in children)
                {
                    if (child is not null)
                    {
                        pending.Push(child);
                    }
                }
            }

            return copy;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
