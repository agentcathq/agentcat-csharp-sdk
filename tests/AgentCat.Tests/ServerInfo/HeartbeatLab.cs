using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.ServerInfo;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.ServerInfo;

// One runtime with a manual clock, an event recorder and a heartbeat recorder, and the servers tracked on it.
internal sealed class HeartbeatLab : IAsyncDisposable
{
    internal const string Project = "proj_0123456789abcdefghijklmnopq";

    internal const string OkResult = "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}";

    internal static readonly string LongDescription = new('d', 2000);

    private readonly List<McpHarness> _servers = new();

    // With an HTTP sender, the events and the heartbeats both go through it, as on the shared runtime.
    internal HeartbeatLab(HttpEventSender? http = null, EnabledGate? gate = null)
    {
        // A flush waits on the clock: with held delays its time limit never passes before the events are out.
        Clock.HoldDelays();
        Runtime = http is null
            ? new AgentCatRuntime(Log, Events, null, Clock, gate, serverInfoSender: Api)
            : new AgentCatRuntime(Log, http, null, Clock, gate, serverInfoSender: http);
        Settle();
    }

    internal RecordingLog Log { get; } = new();

    internal RecordingSender Events { get; } = new();

    internal ManualClock Clock { get; } = new();

    internal RecordingServerInfoSender Api { get; } = new();

    internal AgentCatRuntime Runtime { get; }

    internal ServerInfoHeartbeat Heartbeat => Runtime.ServerInfo;

    internal static T Json<T>(string json)
        => (T)JsonSerializer.Deserialize(json, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T)))!;

    internal static JsonNode Wire<T>(T value)
        => JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(value, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T))))!;

    internal static ValueTask<CallToolResult> Ok() => new(Mcp.Result(OkResult));

    // The text of the tool itself: AgentCat can put its session text in front of it.
    internal static string TextOf(CallToolResult result)
        => (string)Wire(result)["content"]!.AsArray().Last()!["text"]!;

    internal static string ToolJson(string name, string? description = null)
        => "{\"name\":" + JsonSerializer.Serialize(name)
            + (description is null ? "" : ",\"description\":" + JsonSerializer.Serialize(description))
            + ",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}";

    // The memory of the heartbeat is 60 seconds old.
    internal void Settle() => Heartbeat.Guard.StartMemory(Clock.UtcNow - HeartbeatGuard.SettledAge);

    // The memory of the heartbeat is empty, as in a new process.
    internal void NewMemory() => Heartbeat.Guard.Clear();

    internal void Advance(double seconds) => Clock.UtcNow += TimeSpan.FromSeconds(seconds);

    // Three tools, two resources, one resource template and two prompts, all from list handlers.
    internal static McpHarness DemoServer(string name = "demo-server", string instructions = "Use the demo tools.")
    {
        var harness = new McpHarness()
            .AddHandlerTool(ToolJson("alpha", "First tool."), (_, _) => Ok())
            .AddHandlerTool(
                "{\"name\":\"beta\",\"title\":\"Beta Tool\",\"description\":\"" + LongDescription + "\","
                + "\"inputSchema\":{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\",\"description\":\"The query.\"}},\"required\":[\"q\"]},"
                + "\"outputSchema\":{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"number\",\"description\":\"The count.\"}}},"
                + "\"annotations\":{\"title\":\"Beta\",\"readOnlyHint\":true}}",
                (_, _) => Ok())
            .AddHandlerTool(ToolJson("gamma"), (_, _) => Ok());
        return Describe(harness, name, instructions).Configure(o =>
        {
            SetResources(
                o,
                "{\"resources\":[{\"uri\":\"file:///one.txt\",\"name\":\"one\",\"description\":\"First file.\"},{\"uri\":\"file:///two.txt\",\"name\":\"two\"}]}",
                "{\"resourceTemplates\":[{\"uriTemplate\":\"file:///{path}\",\"name\":\"files\",\"description\":\"Any file.\"}]}");
            SetPrompts(
                o,
                "{\"prompts\":[{\"name\":\"greet\",\"description\":\"Says hello.\",\"arguments\":[{\"name\":\"who\",\"description\":\"The person.\",\"required\":true}]},{\"name\":\"bye\"}]}");
        });
    }

    internal static McpHarness Describe(McpHarness harness, string name, string? instructions = null, string title = "Demo Title")
        => harness.Configure(o =>
        {
            o.ServerInfo = Json<Implementation>(
                "{\"name\":" + JsonSerializer.Serialize(name) + ",\"version\":\"1.2.3\",\"title\":" + JsonSerializer.Serialize(title) + "}");
            o.ServerInstructions = instructions;
        });

    internal static void SetResources(McpServerOptions options, string resourcesJson, string templatesJson)
    {
        options.Handlers.ListResourcesHandler = (_, _) => new ValueTask<ListResourcesResult>(Json<ListResourcesResult>(resourcesJson));
        options.Handlers.ListResourceTemplatesHandler = (_, _) => new ValueTask<ListResourceTemplatesResult>(Json<ListResourceTemplatesResult>(templatesJson));
        options.Handlers.ReadResourceHandler = (_, _) => throw new McpProtocolException("no such resource", McpErrorCode.InvalidParams);
    }

    internal static void SetPrompts(McpServerOptions options, string promptsJson)
        => SetPrompts(options, (_, _) => new ValueTask<ListPromptsResult>(Json<ListPromptsResult>(promptsJson)));

    internal static void SetPrompts(McpServerOptions options, McpRequestHandler<ListPromptsRequestParams, ListPromptsResult> handler)
    {
        options.Handlers.ListPromptsHandler = handler;
        options.Handlers.GetPromptHandler = (_, _) => throw new McpProtocolException("no such prompt", McpErrorCode.InvalidParams);
    }

    // Tracks the server on the runtime of the lab, after every other configure step, and starts it.
    internal async Task<McpHarness> StartAsync(
        McpHarness server,
        string? project = Project,
        AgentCatOptions? options = null,
        bool withServices = false)
    {
        _servers.Add(server);
        server.Configure(o => Assert.NotNull(AgentCatEngine.Install(o, project, options, Runtime, "options")));
        await server.StartAsync(null, withServices);
        return server;
    }

    // After a first tool list request a tool call needs no list of its own, so only the heartbeat reads the lists.
    internal async Task FirstHeartbeatAsync(McpHarness server)
    {
        await server.ListToolsAsync();
        await SingleBodyAsync();
        Advance(3600);
    }

    internal async Task WaitForLineAsync(string line)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (!Log.Lines.Contains(line))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("No log line \"" + line + "\". Log: " + string.Join(" / ", Log.Lines));
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    internal async Task<JsonObject> SingleBodyAsync()
    {
        var bodies = await Api.WaitForAsync(1);
        await IdleAsync();
        return Assert.Single(bodies);
    }

    // Waits until every started attempt has written its line: the sent line, or the reason it was not sent.
    internal async Task IdleAsync()
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (Finished() < Heartbeat.Guard.Begun)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("A heartbeat attempt did not end. Log: " + string.Join(" / ", Log.Lines));
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    internal string[] HeartbeatLines() => Log.Lines.Where(l => l.Contains("erver info heartbeat", StringComparison.Ordinal)).ToArray();

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var server in _servers)
            {
                await server.DisposeAsync();
            }
        }
        finally
        {
            Runtime.Pipeline.Dispose();
        }
    }

    private long Finished()
        => Log.Lines.Count(l => l.StartsWith("Sent server info heartbeat", StringComparison.Ordinal)
            || l.StartsWith("Server info heartbeat not sent: ", StringComparison.Ordinal));
}
