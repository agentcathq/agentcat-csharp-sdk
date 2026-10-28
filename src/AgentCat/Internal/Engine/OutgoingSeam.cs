using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal sealed class OutgoingSeam
{
    internal OutgoingSeam(AgentCatTracker tracker) => Tracker = tracker;

    internal AgentCatTracker Tracker { get; }

    internal McpMessageHandler Wrap(McpMessageHandler next)
        => Tracker.Settings.EnableTracing ? (context, cancellationToken) => InvokeAsync(next, context, cancellationToken) : next;

    private static Task InvokeAsync(McpMessageHandler next, MessageContext context, CancellationToken cancellationToken)
    {
        if (context.JsonRpcMessage is JsonRpcResponse response)
        {
            AddSessionText(response);
        }

        return next(context, cancellationToken);
    }

    private static void AddSessionText(JsonRpcResponse response)
    {
        try
        {
            if (response.Context?.Items is { Count: > 0 } items
                && items.TryGetValue(CallToolSeam.SessionTextItem, out var value)
                && value is string text
                && response.Result is JsonObject result
                && result.TryGetPropertyValue("content", out var content)
                && content is JsonArray blocks)
            {
                blocks.Insert(0, new JsonObject { ["type"] = "text", ["text"] = text });
                items.Remove(CallToolSeam.SessionTextItem);
            }
        }
        catch (Exception)
        {
        }
    }
}
