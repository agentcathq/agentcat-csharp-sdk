using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.TestSupport;

internal class McpHarness : IAsyncDisposable
{
    internal const string LegacyProtocol = "2025-11-25";

    private readonly List<McpServerTool> _tools = new();
    private readonly List<HandlerTool> _handlerTools = new();
    private readonly List<Action<McpServerOptions>> _configure = new();
    private readonly List<Action<IServiceCollection>> _services = new();
    private readonly List<Action<IMcpServerBuilder>> _builderSteps = new();
    private readonly List<Action<McpClientOptions>> _clientSteps = new();
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _stop = new();
    private ServiceProvider? _provider;
    private McpServer? _server;
    private Task? _serverTask;
    private McpClient? _client;
    private McpServerOptions? _givenOptions;
    private ILoggerFactory? _loggerFactory;

    internal static int McpMajor => typeof(McpServer).Assembly.GetName().Version!.Major;

    public static TheoryData<string?> ProtocolVersions => new() { (string?)null, LegacyProtocol };

    public static TheoryData<string?, bool> Startups => new()
    {
        { null, false },
        { null, true },
        { LegacyProtocol, false },
        { LegacyProtocol, true },
    };

    internal McpClient Client => _client ?? throw new InvalidOperationException("The harness is not started.");

    internal McpServer Server => _server ?? throw new InvalidOperationException("The harness is not started.");

    internal McpServerOptions ServerOptions => Server.ServerOptions;

    internal IServiceProvider Services => _provider ?? throw new InvalidOperationException("The harness has no service provider.");

    internal TimeSpan CallBound { get; set; } = FakeAgentCatApi.WaitBound;

    internal McpHarness AddTool(Delegate method, string name, string? description = null)
        => AddTool(McpServerTool.Create(method, new McpServerToolCreateOptions { Name = name, Description = description }));

    internal McpHarness AddTool(McpServerTool tool)
    {
        _tools.Add(tool);
        return this;
    }

    internal McpHarness AddHandlerTool(string toolJson, McpRequestHandler<CallToolRequestParams, CallToolResult> handler)
    {
        var name = (string)JsonNode.Parse(toolJson)!["name"]!;
        _handlerTools.Add(new HandlerTool(name, toolJson, handler));
        return this;
    }

    internal McpHarness Configure(Action<McpServerOptions> configure)
    {
        _configure.Add(configure);
        return this;
    }

    internal McpHarness ConfigureServices(Action<IServiceCollection> configure)
    {
        _services.Add(configure);
        return this;
    }

    // DI only: runs on the server builder after the harness tools, in the order given.
    internal McpHarness ConfigureBuilder(Action<IMcpServerBuilder> configure)
    {
        _builderSteps.Add(configure);
        return this;
    }

    // Without DI: the server uses these options as they are, instead of options that the harness builds.
    internal McpHarness UseOptions(McpServerOptions options)
    {
        _givenOptions = options;
        return this;
    }

    // The server logs through this factory, as with McpServer.Create(transport, options, loggerFactory).
    internal McpHarness UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        return this;
    }

    internal McpHarness ConfigureClient(Action<McpClientOptions> configure)
    {
        _clientSteps.Add(configure);
        return this;
    }

    internal async Task<McpClient> StartAsync(string? protocolVersion = null, bool withServices = false)
    {
        if (!withServices && _builderSteps.Count > 0)
        {
            throw new InvalidOperationException("ConfigureBuilder steps need StartAsync(withServices: true).");
        }

        if (withServices && _givenOptions is not null)
        {
            throw new InvalidOperationException("UseOptions works only without services.");
        }

        var serverInput = _clientToServer.Reader.AsStream();
        var serverOutput = _serverToClient.Writer.AsStream();
        if (withServices)
        {
            var services = new ServiceCollection();
            if (_loggerFactory is not null)
            {
                services.AddSingleton(_loggerFactory);
            }

            var builder = services.AddMcpServer(ApplyConfiguration)
                .WithStreamServerTransport(serverInput, serverOutput)
                .WithTools(_tools);
            foreach (var step in _builderSteps)
            {
                step(builder);
            }

            services.PostConfigure<McpServerOptions>(BeforeServerCreate);
            foreach (var configure in _services)
            {
                configure(services);
            }

            _provider = services.BuildServiceProvider();
            _server = _provider.GetRequiredService<McpServer>();
        }
        else if (_givenOptions is not null)
        {
            _server = McpServer.Create(new StreamServerTransport(serverInput, serverOutput), _givenOptions, _loggerFactory);
        }
        else
        {
            var options = new McpServerOptions();
            if (_tools.Count > 0)
            {
                options.ToolCollection = new McpServerPrimitiveCollection<McpServerTool>();
                foreach (var tool in _tools)
                {
                    options.ToolCollection.Add(tool);
                }
            }

            ApplyConfiguration(options);
            BeforeServerCreate(options);
            _server = McpServer.Create(new StreamServerTransport(serverInput, serverOutput), options, _loggerFactory);
        }

        _serverTask = _server.RunAsync(_stop.Token);
        var clientOutput = _clientToServer.Writer.AsStream();
        var clientInput = _serverToClient.Reader.AsStream();
        var transport = new StreamClientTransport(clientOutput, clientInput);
        var clientOptions = ClientOptions(protocolVersion);
        foreach (var configure in _clientSteps)
        {
            configure(clientOptions);
        }

        try
        {
            _client = await BoundedAsync(
                "McpClient.CreateAsync",
                ct => McpClient.CreateAsync(transport, clientOptions, cancellationToken: ct));
        }
        catch (TimeoutException)
        {
            // The connect is cancelled and returned, but its half-open session can still hold these two streams.
            // Closing them lets that session and its reader loop end now instead of at harness disposal.
            clientOutput.Dispose();
            clientInput.Dispose();
            throw;
        }

        return _client;
    }

    internal Task<ListToolsResult> ListToolsAsync(string? cursor = null)
        => BoundedAsync("tools/list", ct => Client.ListToolsAsync(Mcp.ListParams(cursor), ct).AsTask());

    internal Task<CallToolResult> CallToolAsync(string name, string argumentsJson = "{}")
        => CallToolParamsAsync("{\"name\":" + JsonSerializer.Serialize(name) + ",\"arguments\":" + argumentsJson + "}");

    internal Task<CallToolResult> CallToolParamsAsync(string paramsJson)
    {
        var request = Mcp.CallToolParams(paramsJson);
        return BoundedAsync("tools/call " + request.Name, ct => Client.CallToolAsync(request, ct).AsTask());
    }

    // Sends the params as written, so members the loaded client's CallToolRequestParams lacks still reach the server.
    internal async Task<CallToolResult> CallToolRawAsync(string paramsJson)
    {
        var response = await BoundedAsync(
            "raw tools/call",
            ct => Client.SendRequestAsync(new JsonRpcRequest { Method = RequestMethods.ToolsCall, Params = JsonNode.Parse(paramsJson) }, ct));
        return (CallToolResult)response.Result.Deserialize(McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult)))!;
    }

    // 2.x clients fall back to the legacy protocol without a word when the server/discover probe times out,
    // so the probe gets no timeout of its own and the call bound fails a slow start instead.
    internal static McpClientOptions ClientOptions(string? protocolVersion)
    {
        var options = new McpClientOptions { ProtocolVersion = protocolVersion };
        options.ClientInfo = (Implementation)JsonSerializer.Deserialize(
            "{\"name\":\"harness-client\",\"version\":\"3.1.4\"}",
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Implementation)))!;
        typeof(McpClientOptions).GetProperty("DiscoverProbeTimeout")?.SetValue(options, Timeout.InfiniteTimeSpan);
        return options;
    }

    protected virtual void BeforeServerCreate(McpServerOptions options)
    {
    }

    public virtual async ValueTask DisposeAsync()
    {
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }
        }
        finally
        {
            try
            {
                _stop.Cancel();
                _clientToServer.Writer.Complete();
                _serverToClient.Writer.Complete();
                if (_serverTask is not null)
                {
                    await FakeAgentCatApi.WithinAsync(_serverTask, FakeAgentCatApi.WaitBound);
                    await _serverTask;
                }
            }
            finally
            {
                try
                {
                    if (_server is not null && (_serverTask is null || _givenOptions is not null))
                    {
                        await _server.DisposeAsync();
                    }
                }
                finally
                {
                    try
                    {
                        if (_provider is not null)
                        {
                            await _provider.DisposeAsync();
                        }
                    }
                    finally
                    {
                        _stop.Dispose();
                    }
                }
            }
        }
    }

    private async Task<T> BoundedAsync<T>(string operation, Func<CancellationToken, Task<T>> call)
    {
        var test = TestContext.Current.CancellationToken;
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(test);
        bound.CancelAfter(CallBound);
        try
        {
            return await call(bound.Token);
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested && !test.IsCancellationRequested)
        {
            throw new TimeoutException(operation + " did not finish within " + CallBound + ".");
        }
    }

    private void ApplyConfiguration(McpServerOptions options)
    {
        if (_handlerTools.Count > 0)
        {
            var tools = _handlerTools.ToArray();
            options.Handlers.ListToolsHandler = (_, _) => new ValueTask<ListToolsResult>(Listing(tools));
            options.Handlers.CallToolHandler = (context, cancellationToken) => Dispatch(tools, context, cancellationToken);
        }

        foreach (var configure in _configure)
        {
            configure(options);
        }
    }

    private static ListToolsResult Listing(HandlerTool[] tools)
        => (ListToolsResult)JsonSerializer.Deserialize(
            "{\"tools\":[" + string.Join(",", tools.Select(t => t.Json)) + "]}",
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListToolsResult)))!;

    private static ValueTask<CallToolResult> Dispatch(
        HandlerTool[] tools,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        var name = context.Params?.Name;
        var tool = tools.FirstOrDefault(t => t.Name == name)
            ?? throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);
        return tool.Handler(context, cancellationToken);
    }

    private sealed class HandlerTool(
        string name,
        string json,
        McpRequestHandler<CallToolRequestParams, CallToolResult> handler)
    {
        internal string Name { get; } = name;

        internal string Json { get; } = json;

        internal McpRequestHandler<CallToolRequestParams, CallToolResult> Handler { get; } = handler;
    }
}
