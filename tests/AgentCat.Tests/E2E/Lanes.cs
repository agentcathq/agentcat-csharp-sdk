using System.Reflection;
using System.Text.Json;
using AgentCat.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentCat.Tests.E2E;

internal enum LaneTransport
{
    Stdio,
    HttpStateful,
    HttpStateless,

    // WithHttpTransport() with no lambda: stateful on MCP 1.x, stateless on 2.x.
    HttpDefault,
}

// One end-to-end lane: a real MCP server with AgentCat and a real MCP client of the loaded MCP version.
internal sealed class Lane
{
    internal const string Stateful = "stateful";
    internal const string PerRequest = "per-request";

    internal Lane(string id, LaneTransport transport, string? protocolVersion, bool handlerTools)
    {
        Id = id;
        Transport = transport;
        ProtocolVersion = protocolVersion;
        HandlerTools = handlerTools;
    }

    internal string Id { get; }

    internal LaneTransport Transport { get; }

    // The client's McpClientOptions.ProtocolVersion; null is the client default (2026-07-28 on MCP 2.x).
    internal string? ProtocolVersion { get; }

    // Handler tools (WithListToolsHandler, WithCallToolHandler) instead of attribute tools (WithTools).
    internal bool HandlerTools { get; }

    internal bool IsHttp => Transport != LaneTransport.Stdio;

    // A new McpServerOptions instance for every POST.
    internal bool IsStateless => Transport == LaneTransport.HttpStateless || (Transport == LaneTransport.HttpDefault && Lanes.McpMajor >= 2);

    internal string Topology => IsStateless ? PerRequest : Stateful;

    // The client speaks the 2026-07-28 protocol: client info and protocol version ride in _meta on every request.
    internal bool Envelope => ProtocolVersion is null && Lanes.McpMajor >= 2;

    // The server knows the client's name and version on a tool call: from the envelope in _meta, or from the handshake
    // of a stateful server. A legacy client on a stateless server initialized an instance that is gone.
    internal bool KnowsClientIdentity => Envelope || Topology == Stateful;

    // Starts the server with WithAgentCat("proj_test", o => Scenarios.Apply(scenario, o)) and connects one client.
    // A null scenario calls WithAgentCat("proj_test") with no options (in-process lanes only).
    internal Task<LaneInstance> StartAsync(string? scenario, LaneSetup? setup = null)
        => LaneInstance.StartAsync(this, scenario, setup ?? new LaneSetup());

    public override string ToString() => Id;
}

// Per-test customization. The server hooks work on the in-process lanes only; the stdio lane rejects them.
internal sealed class LaneSetup
{
    // The project ID that WithAgentCat gets; null is proj_test.
    internal string? ProjectId { get; init; }

    // Runs after Scenarios.Apply on the options that WithAgentCat gets.
    internal Action<AgentCatOptions>? AgentCat { get; init; }

    // Runs on the server builder after the toolkit and WithAgentCat. A PostConfigure registered here runs after
    // AgentCat's; Configure steps run before it wherever they are registered.
    internal Action<IMcpServerBuilder>? Server { get; init; }

    // Runs on the HTTP transport options after the lane sets Stateless.
    internal Action<HttpServerTransportOptions>? Transport { get; init; }

    // Runs on the app before MapMcp: middleware added here sees every request of the in-process server.
    internal Action<IApplicationBuilder>? App { get; init; }

    // Runs on the options of every client the lane connects, before the per-call configure.
    internal Action<McpClientOptions>? Client { get; init; }

    // The lane's runtime hosts the real exporters and sends their requests through this handler. Without it, the
    // runtime hosts none, so the exporters that WithAgentCat gets build nothing.
    internal HttpMessageHandler? ExportHandler { get; init; }

    // The lane's runtime gate sees a test run and no AGENTCAT_ENABLED, as AgentCatRuntime.Shared does in a customer's
    // test run: WithAgentCat installs nothing. A stopped install is remembered for the process (EnabledGate).
    internal bool TestRun { get; init; }

    // Hosts the server on Kestrel at a free loopback port instead of the in-memory TestServer.
    internal bool Kestrel { get; init; }

    internal bool CustomizesServer
        => ProjectId is not null || AgentCat is not null || Server is not null || Transport is not null || App is not null || ExportHandler is not null || TestRun || Kestrel;
}

// The lane sets the end-to-end theories run over, for the loaded MCP version.
public static class LaneData
{
    public static TheoryData<string> AllLanes => Lanes.Ids();

    // The in-process lanes.
    public static TheoryData<string> HttpLanes => Lanes.Ids(l => l.IsHttp);

    public static TheoryData<string> StatelessLanes => Lanes.Ids(l => l.IsStateless);

    public static TheoryData<string> StatefulHttpLanes => Lanes.Ids(l => l.Transport == LaneTransport.HttpStateful);

    public static TheoryData<string> StdioLanes => Lanes.Ids(l => l.Transport == LaneTransport.Stdio);

    // A server instance that has served no listing: the per-request lanes, and stdio, which starts fresh for each test.
    public static TheoryData<string> RebuildLanes => Lanes.Ids(l => l.IsStateless || l.Transport == LaneTransport.Stdio);
}

internal static class Lanes
{
    internal const string Stdio = "stdio";
    internal const string StdioLegacy = "stdio-legacy";
    internal const string HttpStateful = "http-stateful";
    internal const string HttpStatelessLegacy = "http-stateless-legacy";
    internal const string HttpStatelessHandler = "http-stateless-handler";
    internal const string HttpStateless = "http-stateless";
    internal const string HttpDefault = "http-default";

    internal const string LegacyProtocol = McpHarness.LegacyProtocol;
    internal const string EnvelopeProtocol = "2026-07-28";
    internal const string ClientName = "agentcat-e2e-client";
    internal const string ClientVersion = "1.0.0";

    private static readonly Lazy<IReadOnlyList<Lane>> AllLanes = new(Create);

    private static readonly Lazy<string> ServerDllPath = new(FindServerDll);

    internal static int McpMajor => typeof(McpClient).Assembly.GetName().Version!.Major;

    // The lanes that differ on the loaded MCP version.
    internal static IReadOnlyList<Lane> All => AllLanes.Value;

    // The SDK default HTTP transport, attribute tools, default client. Not part of All.
    internal static Lane Default { get; } = new(HttpDefault, LaneTransport.HttpDefault, null, handlerTools: false);

    // Read on first use, so a missing server fails the stdio lanes only.
    internal static string ServerDll => ServerDllPath.Value;

    internal static Lane Get(string id)
        => id == HttpDefault ? Default : All.SingleOrDefault(l => l.Id == id) ?? throw new ArgumentException("No lane " + id + " on MCP " + McpMajor + ".x", nameof(id));

    internal static TheoryData<string> Ids(Func<Lane, bool>? where = null)
    {
        var data = new TheoryData<string>();
        foreach (var lane in All.Where(where ?? (_ => true)))
        {
            data.Add(lane.Id);
        }

        return data;
    }

    internal static McpClientOptions ClientOptions(string? protocolVersion)
    {
        var options = new McpClientOptions { ProtocolVersion = protocolVersion };
        options.ClientInfo = (Implementation)JsonSerializer.Deserialize(
            "{\"name\":\"" + ClientName + "\",\"version\":\"" + ClientVersion + "\"}",
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Implementation)))!;

        // A 2.x client falls back to the legacy protocol without a word when the server/discover probe times out, so
        // the probe gets no timeout of its own and the connect bound fails a slow start instead.
        typeof(McpClientOptions).GetProperty("DiscoverProbeTimeout")?.SetValue(options, Timeout.InfiniteTimeSpan);
        return options;
    }

    // Every AgentCat variable the stdio server reads is set or cleared, so the test environment cannot leak in. The
    // server writes its AgentCat log to logPath, never to the home directory, and sends no diagnostics.
    internal static Dictionary<string, string?> StdioEnvironment(string scenario, string apiBaseUrl, bool handlerTools, string logPath) => new()
    {
        [E2EToolkit.ScenarioVariable] = scenario,
        [E2EToolkit.ToolsVariable] = handlerTools ? E2EToolkit.HandlerToolsValue : E2EToolkit.AttributeToolsValue,
        [E2EToolkit.ModeVariable] = null,
        [E2EToolkit.HostVariable] = null,
        [E2EToolkit.ShutdownHandlersVariable] = null,
        [E2EToolkit.LogPathVariable] = logPath,
        [E2EToolkit.ProjectVariable] = null,
        [E2EExporters.HostVariable] = null,
        ["AGENTCAT_API_URL"] = apiBaseUrl,
        ["AGENTCAT_ENABLED"] = "true",
        ["AGENTCAT_DISABLE_DIAGNOSTICS"] = "true",
        ["AGENTCAT_PROJECT_ID"] = null,
        ["MCPCAT_API_URL"] = null,
    };

    // A new path in the temp directory for one stdio server's AgentCat log. Nothing creates the file before the server
    // writes its first line.
    internal static string NewLogPath() => Path.Combine(Path.GetTempPath(), "agentcat-e2e-" + Guid.NewGuid().ToString("N") + ".log");

    private static string FindServerDll()
    {
        var path = typeof(Lanes).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "AgentCatStdioServerDll")?.Value;
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException(
                "The test assembly has no AgentCatStdioServerDll metadata: the AddStdioServerPath target of AgentCat.Tests.csproj did not run.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException("The stdio server is not built: " + path);
        }

        return path;
    }

    // http-stateless, the reference lane, serves attribute tools; each MCP major also has a stateless lane with handler
    // tools.
    private static Lane[] Create()
        => McpMajor >= 2
            ? new[]
            {
                new Lane(Stdio, LaneTransport.Stdio, null, handlerTools: false),
                new Lane(StdioLegacy, LaneTransport.Stdio, LegacyProtocol, handlerTools: true),
                new Lane(HttpStateful, LaneTransport.HttpStateful, LegacyProtocol, handlerTools: false),
                new Lane(HttpStatelessLegacy, LaneTransport.HttpStateless, LegacyProtocol, handlerTools: true),
                new Lane(HttpStateless, LaneTransport.HttpStateless, null, handlerTools: false),
            }
            : new[]
            {
                new Lane(Stdio, LaneTransport.Stdio, null, handlerTools: false),
                new Lane(HttpStateful, LaneTransport.HttpStateful, null, handlerTools: false),
                new Lane(HttpStatelessHandler, LaneTransport.HttpStateless, null, handlerTools: true),
                new Lane(HttpStateless, LaneTransport.HttpStateless, null, handlerTools: false),
            };
}
