using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Injection;
using AgentCat.Internal.Json;
using AgentCat.Internal.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.ServerInfo;

// Reads the lists a client gets from the server: every page of a list handler, then the items of the matching
// collection, each item one time. The list handlers get a context built from the live server of the request.
internal static class ServerInfoLists
{
    internal const string ToolsKey = "tools";
    internal const string ResourcesKey = "resources";
    internal const string ResourceTemplatesKey = "resourceTemplates";
    internal const string PromptsKey = "prompts";

    internal const int MaxPages = 100;

    private const string NameKey = "name";
    private const string UriKey = "uri";
    private const string UriTemplateKey = "uriTemplate";
    private const string CursorKey = "cursor";

    private static readonly JsonTypeInfo ToolInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));
    private static readonly JsonTypeInfo ResourceInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Resource));
    private static readonly JsonTypeInfo ResourceTemplateInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ResourceTemplate));
    private static readonly JsonTypeInfo PromptInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Prompt));
    private static readonly JsonTypeInfo ListToolsParamsInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListToolsRequestParams));
    private static readonly JsonTypeInfo ListResourcesParamsInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListResourcesRequestParams));
    private static readonly JsonTypeInfo ListResourceTemplatesParamsInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListResourceTemplatesRequestParams));
    private static readonly JsonTypeInfo ListPromptsParamsInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListPromptsRequestParams));

    // The features of the server. A server without a source for a list has no key for it. A failed tool list throws;
    // any other failed list is left out and reported through skipped. The list handlers get services, which are
    // never those of the request that started the heartbeat.
    internal static async Task<JsonObject> ReadAsync(
        HeartbeatOrigin origin,
        IServiceProvider? services,
        InjectionSettings injection,
        TimeSpan listTimeout,
        Action<string, Exception> skipped,
        CancellationToken cancellationToken)
    {
        var options = origin.Server.ServerOptions;
        var handlers = options.Handlers;
        var features = new JsonObject();

        var tools = options.ToolCollection;
        if (handlers.ListToolsHandler is not null || tools is { Count: > 0 })
        {
            try
            {
                features[ToolsKey] = await ListAsync(
                    origin,
                    services,
                    RequestMethods.ToolsList,
                    handlers.ListToolsHandler,
                    ListToolsParamsInfo,
                    static result => result.Tools,
                    tools is null ? null : ProtocolTools(tools),
                    ToolInfo,
                    NameKey,
                    page => Inject(injection, page),
                    listTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HeartbeatFailure("the " + ToolsKey + " list failed: " + HeartbeatFailure.ReasonOf(ex));
            }
        }

        var resources = options.ResourceCollection;
        if (handlers.ListResourcesHandler is not null || handlers.ListResourceTemplatesHandler is not null || resources is { Count: > 0 })
        {
            try
            {
                features[ResourcesKey] = await ListAsync(
                    origin,
                    services,
                    RequestMethods.ResourcesList,
                    handlers.ListResourcesHandler,
                    ListResourcesParamsInfo,
                    static result => result.Resources,
                    resources is null ? null : ProtocolResources(resources),
                    ResourceInfo,
                    UriKey,
                    null,
                    listTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                skipped(ResourcesKey, ex);
            }

            try
            {
                features[ResourceTemplatesKey] = await ListAsync(
                    origin,
                    services,
                    RequestMethods.ResourcesTemplatesList,
                    handlers.ListResourceTemplatesHandler,
                    ListResourceTemplatesParamsInfo,
                    static result => result.ResourceTemplates,
                    resources is null ? null : ProtocolResourceTemplates(resources),
                    ResourceTemplateInfo,
                    UriTemplateKey,
                    null,
                    listTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                skipped(ResourceTemplatesKey, ex);
            }
        }

        var prompts = options.PromptCollection;
        if (handlers.ListPromptsHandler is not null || prompts is { Count: > 0 })
        {
            try
            {
                features[PromptsKey] = await ListAsync(
                    origin,
                    services,
                    RequestMethods.PromptsList,
                    handlers.ListPromptsHandler,
                    ListPromptsParamsInfo,
                    static result => result.Prompts,
                    prompts is null ? null : ProtocolPrompts(prompts),
                    PromptInfo,
                    NameKey,
                    null,
                    listTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                skipped(PromptsKey, ex);
            }
        }

        return features;
    }

    private static IEnumerable<Tool> ProtocolTools(IEnumerable<McpServerTool> tools)
    {
        foreach (var tool in tools)
        {
            yield return tool.ProtocolTool;
        }
    }

    private static IEnumerable<Resource> ProtocolResources(IEnumerable<McpServerResource> resources)
    {
        foreach (var resource in resources)
        {
            if (!resource.IsTemplated && resource.ProtocolResource is { } listed)
            {
                yield return listed;
            }
        }
    }

    private static IEnumerable<ResourceTemplate> ProtocolResourceTemplates(IEnumerable<McpServerResource> resources)
    {
        foreach (var resource in resources)
        {
            if (resource.IsTemplated)
            {
                yield return resource.ProtocolResourceTemplate;
            }
        }
    }

    private static IEnumerable<Prompt> ProtocolPrompts(IEnumerable<McpServerPrompt> prompts)
    {
        foreach (var prompt in prompts)
        {
            yield return prompt.ProtocolPrompt;
        }
    }

    // The collection items join the first page, as the server adds them for a request without a cursor. The time limit
    // covers all pages of the list.
    private static async Task<JsonArray> ListAsync<TParams, TResult, TItem>(
        HeartbeatOrigin origin,
        IServiceProvider? services,
        string method,
        McpRequestHandler<TParams, TResult>? handler,
        JsonTypeInfo paramsInfo,
        Func<TResult, IList<TItem>?> itemsOf,
        IEnumerable<TItem>? collection,
        JsonTypeInfo itemInfo,
        string identityKey,
        Func<List<JsonObject>, List<JsonObject>>? eachPage,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where TResult : PaginatedResult
    {
        var listed = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var elapsed = Stopwatch.StartNew();
        string? cursor = null;
        for (var pages = 1; ; pages++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = new List<JsonObject>();
            string? next = null;
            if (handler is not null)
            {
                var context = Context<TParams>(origin, services, method, paramsInfo, cursor);
                var result = await Bounded.RunAsync(
                    token => handler(context, token).AsTask(),
                    timeout - elapsed.Elapsed,
                    TimeoutMessage(method, timeout),
                    cancellationToken).ConfigureAwait(false);
                if (result is null || itemsOf(result) is not { } items)
                {
                    throw new HeartbeatFailure(method + " returned no list");
                }

                foreach (var item in items)
                {
                    AddNode(page, item, itemInfo);
                }

                next = result.NextCursor;
            }

            if (cursor is null && collection is not null)
            {
                foreach (var item in collection)
                {
                    AddNode(page, item, itemInfo);
                }
            }

            foreach (var node in eachPage is null ? page : eachPage(page))
            {
                if (Identity(node, identityKey) is not { } identity || seen.Add(identity))
                {
                    listed.Add((JsonNode)node);
                }
            }

            if (next is null)
            {
                return listed;
            }

            if (!cursors.Add(next))
            {
                throw new HeartbeatFailure(method + " returned the same cursor again");
            }

            if (pages >= MaxPages)
            {
                throw new HeartbeatFailure(
                    method + " has more than " + MaxPages.ToString(CultureInfo.InvariantCulture) + " pages");
            }

            cursor = next;
        }
    }

    private static RequestContext<TParams> Context<TParams>(
        HeartbeatOrigin origin,
        IServiceProvider? services,
        string method,
        JsonTypeInfo paramsInfo,
        string? cursor)
    {
        var request = new JsonRpcRequest { Id = origin.RequestId, Method = method };
        if (origin.User is { } user)
        {
            request.Context = new JsonRpcMessageContext { User = user };
        }

        var json = cursor is null ? new JsonObject() : new JsonObject { [CursorKey] = cursor };
        return new RequestContext<TParams>(origin.Server, request)
        {
            Params = (TParams)JsonSerializer.Deserialize(json.ToJsonString(), paramsInfo)!,
            Services = services,
        };
    }

    private static void AddNode<TItem>(List<JsonObject> page, TItem item, JsonTypeInfo itemInfo)
    {
        if (item is null)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(item, itemInfo);
        if (JsonNode.Parse(bytes, new JsonNodeOptions()) is JsonObject node)
        {
            page.Add(node);
        }
    }

    private static string? Identity(JsonObject node, string key)
        => node.TryGetPropertyValue(key, out var value) && value is JsonValue text && text.GetValueKind() == JsonValueKind.String
            ? JsonTreeCopy.StringOf(text)
            : null;

    // What the tool list filter of AgentCat does to one page. A page it cannot change is listed as the server gave it.
    private static List<JsonObject> Inject(InjectionSettings injection, List<JsonObject> page)
    {
        if (page.Count == 0)
        {
            return page;
        }

        try
        {
            var injected = ToolListInjector.Build(injection, page.ToArray(), new InjectionState(), NullAgentCatLog.Instance);
            var listed = new List<JsonObject>(injected.Tools.Count);
            foreach (var node in injected.Tools)
            {
                if (ToolJson.ToNode(ToolJson.FromNode(node)) is { } tool)
                {
                    listed.Add(tool);
                }
            }

            return listed;
        }
        catch (Exception)
        {
            return page;
        }
    }

    private static string TimeoutMessage(string method, TimeSpan timeout)
        => method + " did not finish within " + ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";
}
