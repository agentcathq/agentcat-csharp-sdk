using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Injection;
using AgentCat.Internal.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal static class RegistryRebuilder
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private const string RebuiltLine = "Rebuilt injection registries on demand (tools/call before tools/list on this instance)";

    private const string FailedPrefix = "Warning: registry rebuild-on-demand failed - ";

    // A call needs a rebuild before any listing, or when the registry lacks its tool (the listings came from options
    // that do not have it) and its arguments carry a name AgentCat may have injected. A recorded tool costs one lookup.
    internal static bool IsNeeded(ToolRegistries registries, RequestContext<CallToolRequestParams> context)
    {
        try
        {
            if (!registries.HasListing)
            {
                return true;
            }

            var parameters = context.Params;
            return parameters?.Name is { } name
                && !registries.InjectedParams.ContainsKey(name)
                && parameters.Arguments is { Count: > 0 } arguments
                && (arguments.ContainsKey(WireNames.SessionIdParam)
                    || arguments.ContainsKey(WireNames.AgentIdParam)
                    || arguments.ContainsKey(WireNames.ContextParam));
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Rebuilds from the listing the SDK composes for this request's options, without the list filters: the
    // handler's first page, then the collection tools.
    internal static async Task EnsureAsync(AgentCatTracker tracker, RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var log = tracker.Runtime.CallLog;
        try
        {
            var options = context.Server.ServerOptions;
            var handler = options.Handlers.ListToolsHandler;
            var collection = options.ToolCollection;
            if (handler is null && collection is null)
            {
                return;
            }

            var tools = new List<Tool?>();
            if (handler is not null)
            {
                var page = await PageAsync(handler, context, tracker.RebuildTimeout, cancellationToken).ConfigureAwait(false);
                if (page?.Tools is not { } listed)
                {
                    return;
                }

                tools.AddRange(listed);
            }

            if (collection is not null)
            {
                foreach (var tool in collection)
                {
                    tools.Add(tool.ProtocolTool);
                }
            }

            var nodes = new JsonObject?[tools.Count];
            for (var i = 0; i < nodes.Length; i++)
            {
                nodes[i] = ToolJson.ToNode(tools[i]);
            }

            if (tracker.Registries.RecordMissing(ToolListInjector.Build(tracker.Settings.Injection, nodes, tracker.State, log)))
            {
                log.Write(RebuiltLine);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.Write(FailedPrefix + ErrorText.Of(ex));
        }
    }

    internal static string TimeoutMessage(TimeSpan timeout)
        => "tools/list did not respond within " + ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";

    private static async Task<ListToolsResult?> PageAsync(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> handler,
        RequestContext<CallToolRequestParams> context,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var request = new JsonRpcRequest { Id = context.JsonRpcRequest.Id, Method = RequestMethods.ToolsList };
        if (context.User is { } user)
        {
            request.Context = new JsonRpcMessageContext { User = user };
        }

        var listing = new RequestContext<ListToolsRequestParams>(context.Server, request)
        {
            Params = new ListToolsRequestParams(),
            Services = context.Services,
        };

        var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var handedOff = false;
        try
        {
            var page = handler(listing, abandon.Token).AsTask();
            bool timedOut;
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timedOut = await Task.WhenAny(page, Task.Delay(timeout, timer.Token)).ConfigureAwait(false) != page;
                timer.Cancel();
            }

            if (!timedOut)
            {
                return await page.ConfigureAwait(false);
            }

            handedOff = true;
            _ = AbandonAsync(page, abandon);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(TimeoutMessage(timeout));
        }
        finally
        {
            if (!handedOff)
            {
                abandon.Dispose();
            }
        }
    }

    // The handler that missed the deadline keeps its token until it finishes; its outcome is observed and dropped.
    private static async Task AbandonAsync(Task page, CancellationTokenSource abandon)
    {
        try
        {
            abandon.Cancel();
        }
        catch (Exception)
        {
        }

        try
        {
            await page.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            abandon.Dispose();
        }
    }
}
