using System;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal sealed class InnerTap
{
    internal InnerTap(AgentCatTracker tracker) => Tracker = tracker;

    internal AgentCatTracker Tracker { get; }

    internal McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => (context, cancellationToken) => InvokeAsync(Tracker, next, context, cancellationToken);

    private static ValueTask<CallToolResult> InvokeAsync(
        AgentCatTracker tracker,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        if (!tracker.Settings.EnableTracing)
        {
            return next(context, cancellationToken);
        }

        ValueTask<CallToolResult> pending;
        try
        {
            pending = next(context, cancellationToken);
        }
        catch (Exception ex)
        {
            Stash(context, ex);
            throw;
        }

        return pending.IsCompletedSuccessfully ? pending : ObserveAsync(pending, context);
    }

    private static async ValueTask<CallToolResult> ObserveAsync(ValueTask<CallToolResult> pending, RequestContext<CallToolRequestParams> context)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Stash(context, ex);
            throw;
        }
    }

    private static void Stash(RequestContext<CallToolRequestParams> context, Exception exception)
    {
        try
        {
            context.Items[CallToolSeam.ExceptionItem] = exception;
        }
        catch (Exception)
        {
        }
    }
}
