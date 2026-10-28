using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry;
using AgentCat.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentCat.Tests.E2E;

// A started lane: the server, its fake AgentCat API and the connected clients. In-process lanes (HTTP) host the server
// in an ASP.NET Core TestServer (or on Kestrel, LaneSetup.Kestrel) with a test AgentCatRuntime; the stdio lanes run the
// server in a child process. On the stdio lanes, wait for the events before disposing: the C# stdio client kills the
// server when it disposes.
internal sealed class LaneInstance : IAsyncDisposable
{
    internal static readonly TimeSpan EventBound = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan CallBound = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan StdioConnectBound = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StdioSettle = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan StdioShutdownTimeout = TimeSpan.FromMilliseconds(500);

    private readonly List<McpClient> _clients = new();
    private readonly List<HttpClient> _httpClients = new();
    private readonly List<string> _stderr = new();
    private readonly LaneSetup _setup;
    private WebApplication? _app;
    private SseCounter? _counter;
    private Uri? _baseUri;
    private RecordingLog? _log;
    private HttpEventSender? _sender;
    private AgentCatRuntime? _runtime;

    private LaneInstance(Lane lane, string? scenario, LaneSetup setup)
    {
        Lane = lane;
        Scenario = scenario;
        _setup = setup;
        ServerLogPath = lane.IsHttp ? null : Lanes.NewLogPath();
    }

    internal Lane Lane { get; }

    internal string? Scenario { get; }

    internal FakeAgentCatApi Api { get; } = new();

    // The first client the lane connected.
    internal McpClient Client => _clients.Count > 0 ? _clients[0] : throw new InvalidOperationException("The lane has no client.");

    // In-process lanes: the log lines of the lane's AgentCatRuntime. stdio: empty, the server writes its AgentCat log
    // to ServerLogPath.
    internal IReadOnlyList<string> Log => _log?.Lines ?? Array.Empty<string>();

    // stdio: the server's own AgentCat log file, deleted when the lane is disposed.
    internal string? ServerLogPath { get; }

    // stdio: the server's stderr lines.
    internal IReadOnlyList<string> ServerStderr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToArray();
            }
        }
    }

    // In-process lanes: the lane's AgentCatRuntime.
    internal AgentCatRuntime Runtime => _runtime ?? throw new InvalidOperationException("Lane " + Lane.Id + " has no in-process runtime.");

    // In-process lanes: the server's base URL, http://localhost/ on the TestServer and http://127.0.0.1:<port>/ on
    // Kestrel. MapMcp serves streamable HTTP at the root and legacy SSE at sse.
    internal Uri BaseUri => _baseUri ?? throw new InvalidOperationException("Lane " + Lane.Id + " has no HTTP transport.");

    // In-process lanes: the host's services.
    internal IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Lane " + Lane.Id + " has no in-process services.");

    // HTTP lanes: POST responses with text/event-stream, from every client of the lane.
    internal int SsePosts => Counter.SsePosts;

    // HTTP lanes: POST responses with application/json.
    internal int JsonPosts => Counter.JsonPosts;

    // The events the fake API has received so far, in call order: by timestamp (milliseconds), then by JSON-RPC request
    // ID, then by arrival. The pipeline sends up to five events at once, so the arrival order is not the call order.
    internal IReadOnlyList<WireEvent> Events
        => Api.Requests.Select(WireEvent.Parse)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.RequestId ?? long.MaxValue)
            .ThenBy(e => e.Arrival)
            .ToArray();

    private SseCounter Counter => _counter ?? throw new InvalidOperationException("Lane " + Lane.Id + " has no HTTP transport.");

    internal static async Task<LaneInstance> StartAsync(Lane lane, string? scenario, LaneSetup setup)
    {
        var instance = new LaneInstance(lane, scenario, setup);
        try
        {
            if (lane.IsHttp)
            {
                await instance.StartHttpAsync();
            }
            else
            {
                await instance.StartStdioAsync();
            }

            return instance;
        }
        catch
        {
            await instance.DisposeAsync();
            throw;
        }
    }

    // Another client on the same server (HTTP lanes), optionally sending headers on every request, stamping and
    // recording its requests in requests, or speaking legacy SSE (GET sse, POST message) instead of streamable HTTP.
    // The stdio server serves the one client that started it.
    internal async Task<McpClient> ConnectClientAsync(
        Action<McpClientOptions>? configure = null,
        IDictionary<string, string>? headers = null,
        ClientRequests? requests = null,
        bool legacySse = false)
    {
        if (!Lane.IsHttp && (_clients.Count > 0 || headers is not null || requests is not null || legacySse))
        {
            throw new NotSupportedException("A stdio server serves only the client that started it, without HTTP headers.");
        }

        var options = Lanes.ClientOptions(Lane.ProtocolVersion);
        _setup.Client?.Invoke(options);
        configure?.Invoke(options);
        McpClient client;
        if (Lane.IsHttp)
        {
            var http = new HttpClient(requests?.Wrap(Counter) ?? Counter, disposeHandler: false);
            _httpClients.Add(http);
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = legacySse ? new Uri(BaseUri, "sse") : BaseUri,
                    TransportMode = legacySse ? HttpTransportMode.Sse : HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = headers,
                },
                http);
            client = await BoundedAsync("McpClient.CreateAsync", CallBound, ct => McpClient.CreateAsync(transport, options, cancellationToken: ct));
        }
        else
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "agentcat-e2e-" + Lane.Id,
                Command = "dotnet",
                Arguments = new List<string> { Lanes.ServerDll },
                EnvironmentVariables = Lanes.StdioEnvironment(Scenario!, Api.BaseUrl, Lane.HandlerTools, ServerLogPath!),
                StandardErrorLines = line =>
                {
                    lock (_stderr)
                    {
                        _stderr.Add(line);
                    }
                },
                ShutdownTimeout = StdioShutdownTimeout,
            });
            try
            {
                client = await BoundedAsync("McpClient.CreateAsync", StdioConnectBound, ct => McpClient.CreateAsync(transport, options, cancellationToken: ct));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("The stdio server did not start. Its stderr:\n" + string.Join("\n", ServerStderr), ex);
            }
        }

        _clients.Add(client);
        return client;
    }

    internal Task<ListToolsResult> ListToolsAsync(McpClient? client = null)
        => BoundedAsync("tools/list", CallBound, ct => (client ?? Client).ListToolsAsync(Mcp.ListParams(null), ct).AsTask());

    internal Task<CallToolResult> CallToolAsync(string name, string argumentsJson = "{}", McpClient? client = null)
        => CallToolParamsAsync("{\"name\":" + JsonSerializer.Serialize(name) + ",\"arguments\":" + argumentsJson + "}", client);

    // The params as JSON, for _meta and other members besides the arguments.
    internal Task<CallToolResult> CallToolParamsAsync(string paramsJson, McpClient? client = null)
    {
        var request = Mcp.CallToolParams(paramsJson);
        return BoundedAsync("tools/call " + request.Name, CallBound, ct => (client ?? Client).CallToolAsync(request, ct).AsTask());
    }

    // Sends the params as written, so members the loaded client's CallToolRequestParams lacks still reach the server.
    internal async Task<CallToolResult> CallToolRawAsync(string paramsJson, McpClient? client = null)
    {
        var response = await BoundedAsync(
            "raw tools/call",
            CallBound,
            ct => (client ?? Client).SendRequestAsync(new JsonRpcRequest { Method = RequestMethods.ToolsCall, Params = JsonNode.Parse(paramsJson) }, ct));
        return (CallToolResult)response.Result.Deserialize(McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult)))!;
    }

    // Waits up to 10 s until the fake API has at least count events, and returns all of them in call order (Events).
    // The body is recorded after the pipeline's last stage, so hook results are final.
    internal async Task<IReadOnlyList<WireEvent>> WaitForEventsAsync(int count)
    {
        try
        {
            await Api.WaitForRequestsAsync(count, EventBound);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                "Lane " + Lane.Id + " expected at least " + count + " events within " + EventBound + " and got " + Events.Count + ".\n" + Describe(),
                ex);
        }

        return Events;
    }

    // In-process lanes: waits up to bound until a line of the lane's AgentCat log matches, and returns the first such line.
    internal async Task<string> WaitForLogAsync(Func<string, bool> match, TimeSpan bound)
    {
        var deadline = DateTime.UtcNow + bound;
        while (true)
        {
            if (Log.FirstOrDefault(match) is { } line)
            {
                return line;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Lane " + Lane.Id + " wrote no matching log line within " + bound + ".\n" + Describe());
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // Exactly count events, after the in-process runtime drained its queue or, on stdio, after a 500 ms settle.
    internal async Task AssertEventCountAsync(int count)
    {
        if (count > 0)
        {
            await WaitForEventsAsync(count);
        }

        if (_runtime is not null)
        {
            var flushed = _runtime.Pipeline.FlushAsync(EventBound);
            await FakeAgentCatApi.WithinAsync(flushed, EventBound + TimeSpan.FromSeconds(1));
            Assert.True(await flushed, "The event pipeline of lane " + Lane.Id + " did not drain.");
        }
        else
        {
            await Task.Delay(StdioSettle, TestContext.Current.CancellationToken);
        }

        var events = Events;
        Assert.True(events.Count == count, "Lane " + Lane.Id + " expected exactly " + count + " events and got " + events.Count + ".\n" + Describe());
    }

    // Disposes everything even when a step throws, then rethrows what failed.
    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        try
        {
            if (_app is not null)
            {
                using var stop = new CancellationTokenSource(FakeAgentCatApi.WaitBound);
                await _app.StopAsync(stop.Token);
                await _app.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        foreach (var http in _httpClients)
        {
            http.Dispose();
        }

        _counter?.Dispose();
        _runtime?.Pipeline.Dispose();
        _sender?.Dispose();
        Api.Dispose();
        StdioServerProcess.DeleteLog(ServerLogPath);
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("Lane " + Lane.Id + " did not dispose cleanly.", failures);
        }
    }

    private async Task StartHttpAsync()
    {
        _log = new RecordingLog();
        _sender = new HttpEventSender(new PinnedEndpoint(new Uri(Api.BaseUrl)));
        _sender.SetBaseUrl(Api.BaseUrl);
        _runtime = new AgentCatRuntime(
            _log,
            _sender,
            gate: _setup.TestRun ? new EnabledGate(static _ => null, static () => true, _log) : null,
            exportHandler: _setup.ExportHandler,
            exporterBuilder: _setup.ExportHandler is null ? null : ExporterFactory.Build);

        var builder = WebApplication.CreateSlimBuilder();
        if (_setup.Kestrel)
        {
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_runtime);
        var mcp = builder.Services.AddMcpServer(o => o.ServerInfo = E2EToolkit.ServerInfo());
        bool? stateless = Lane.Transport switch
        {
            LaneTransport.HttpStateful => false,
            LaneTransport.HttpStateless => true,
            _ => null,
        };
        // The server's callbacks capture locals only: a callback that held this lane would hold the whole host.
        var configureTransport = _setup.Transport;
        if (stateless is null && configureTransport is null)
        {
            mcp.WithHttpTransport();
        }
        else
        {
            mcp.WithHttpTransport(o =>
            {
                if (stateless is { } value)
                {
                    o.Stateless = value;
                }

                configureTransport?.Invoke(o);
            });
        }

        mcp.WithE2ETools(Lane.HandlerTools);
        var scenario = Scenario;
        var configureAgentCat = _setup.AgentCat;
        var projectId = _setup.ProjectId ?? E2EToolkit.ProjectId;
        if (scenario is null && configureAgentCat is null)
        {
            mcp.WithAgentCat(projectId);
        }
        else
        {
            mcp.WithAgentCat(projectId, o =>
            {
                if (scenario is not null)
                {
                    Scenarios.Apply(scenario, o);
                }

                configureAgentCat?.Invoke(o);
            });
        }

        _setup.Server?.Invoke(mcp);
        _app = builder.Build();
        _setup.App?.Invoke(_app);
        _app.MapMcp();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        if (_setup.Kestrel)
        {
            _baseUri = new Uri(_app.Urls.Single().TrimEnd('/') + "/");
            _counter = new SseCounter(new SocketsHttpHandler { ActivityHeadersPropagator = null });
        }
        else
        {
            _baseUri = new Uri("http://localhost/");
            _counter = new SseCounter(_app.GetTestServer().CreateHandler());
        }

        await ConnectClientAsync();
    }

    private async Task StartStdioAsync()
    {
        if (Scenario is null)
        {
            throw new ArgumentException("The stdio server always applies a scenario.");
        }

        if (_setup.CustomizesServer)
        {
            throw new NotSupportedException("The stdio server runs in another process: the server hooks work on the in-process lanes only.");
        }

        await ConnectClientAsync();
    }

    private string Describe()
    {
        var lines = new List<string> { "Events:" };
        lines.AddRange(Events.Select(e => "  " + e));
        if (_log is not null)
        {
            lines.Add("AgentCat log:");
            lines.AddRange(Log.Select(l => "  " + l));
        }

        if (!Lane.IsHttp)
        {
            lines.Add("Server stderr:");
            lines.AddRange(ServerStderr.Select(l => "  " + l));
            lines.Add("Server AgentCat log:");
            lines.AddRange(StdioServerProcess.ReadLog(ServerLogPath!).Select(l => "  " + l));
        }

        return string.Join("\n", lines);
    }

    private static async Task<T> BoundedAsync<T>(string operation, TimeSpan bound, Func<CancellationToken, Task<T>> call)
    {
        var test = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(test);
        cts.CancelAfter(bound);
        try
        {
            return await call(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !test.IsCancellationRequested)
        {
            throw new TimeoutException(operation + " did not finish within " + bound + ".");
        }
    }

    // Sends every event to the lane's fake API, whatever base URL the environment gives the runtime.
    private sealed class PinnedEndpoint(Uri api) : DelegatingHandler(new SocketsHttpHandler { ActivityHeadersPropagator = null })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(api, request.RequestUri!.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
