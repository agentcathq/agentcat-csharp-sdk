using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Injection;
using AgentCat.Internal.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal sealed class ListToolsSeam
{
    internal ListToolsSeam(AgentCatTracker tracker) => Tracker = tracker;

    internal AgentCatTracker Tracker { get; }

    internal McpRequestHandler<ListToolsRequestParams, ListToolsResult> Wrap(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next)
        => (context, cancellationToken) => InvokeAsync(Tracker, next, context, cancellationToken);

    private static async ValueTask<ListToolsResult> InvokeAsync(
        AgentCatTracker tracker,
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next,
        RequestContext<ListToolsRequestParams> context,
        CancellationToken cancellationToken)
    {
        var original = await next(context, cancellationToken).ConfigureAwait(false);
        var log = tracker.Runtime.CallLog;
        try
        {
            if (original?.Tools is not { Count: > 0 } tools)
            {
                log.Write("Warning: No tools found in the original list. This is likely due to the tools not being registered before AgentCat.track().");
                return original!;
            }

            var nodes = new JsonObject?[tools.Count];
            for (var i = 0; i < nodes.Length; i++)
            {
                nodes[i] = ToolJson.ToNode(tools[i]);
            }

            var injected = ToolListInjector.Build(tracker.Settings.Injection, nodes, tracker.State, log);
            var listed = new List<Tool?>(injected.Tools.Count);
            foreach (var node in injected.Tools)
            {
                listed.Add(ToolJson.FromNode(node));
            }

            var result = ToolJson.CopyWith(original, listed);
            tracker.Registries.Record(injected);
            return result;
        }
        catch (Exception ex)
        {
            log.Write("Warning: AgentCat tool-list injection failed; serving the original tool list unmodified - " + ErrorText.Of(ex));
            return original!;
        }
    }
}
