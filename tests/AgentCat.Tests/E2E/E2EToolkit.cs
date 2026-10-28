using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.E2E;

// The TS e2e toolkit (echo, loose, strict, structured, boom) plus custom, transport and slow, as attribute tools and as
// handler tools with one listing: the handler listing is the attribute tools' listing. loose and strict read the
// arguments the tool received, so they show what AgentCat stripped. Compiled into the stdio server too.
internal static class E2EToolkit
{
    internal const string ProjectId = "proj_test";
    internal const string ServerName = "agentcat-e2e-server";
    internal const string ServerVersion = "1.0.0";
    internal const string ScenarioVariable = "AGENTCAT_E2E_SCENARIO";
    internal const string ToolsVariable = "AGENTCAT_E2E_TOOLS";
    internal const string HandlerToolsValue = "handler";
    internal const string AttributeToolsValue = "attribute";
    internal const string ModeVariable = "AGENTCAT_E2E_MODE";
    internal const string LogPathVariable = "AGENTCAT_TEST_LOG_PATH";

    // The stdio server without a .NET host: AgentCatSdk.Track and McpServer.Create.
    internal const string HostVariable = "AGENTCAT_E2E_HOST";
    internal const string NoHostValue = "none";

    // Sets AgentCatOptions.DisableShutdownHandlers on the stdio server: no ProcessExit or signal handler.
    internal const string ShutdownHandlersVariable = "AGENTCAT_E2E_SHUTDOWN_HANDLERS";
    internal const string OffValue = "off";

    // The stdio server's project ID: ProjectId when unset or empty (the MCP stdio client of some versions passes a
    // cleared variable as empty), none (telemetry-only) for TelemetryOnlyValue, else the value.
    internal const string ProjectVariable = "AGENTCAT_E2E_PROJECT";
    internal const string TelemetryOnlyValue = "telemetry-only";

    // The stdio server writes this line to stderr when a slow call starts.
    internal const string SlowStartedLine = "e2e: slow call started";

    // The stdio server without a .NET host writes this line to stderr when its process exit starts, before AgentCat's
    // process-exit handler runs.
    internal const string ProcessExitLine = "e2e: process exit started";

    // The stdio server with a .NET host writes this line to stderr when the host's stop starts, before the flush after
    // the stop runs.
    internal const string HostStoppingLine = "e2e: host stopping";

    // The custom tool passes its own project ID: the event must carry the tracked one.
    internal const string CustomCallerProjectId = "proj_custom_caller";
    internal const string CustomResourceName = "e2e_custom";
    internal const string CustomMessage = "custom event from a tool";
    internal const string CustomReply = "published";
    internal const string BoomMessage = "boom";
    internal const string UnexpectedArgumentPrefix = "unexpected argument: ";
    internal const string MissingArgumentPrefix = "missing argument: ";

    private static readonly Lazy<string> HandlerListing = new(BuildListing, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly object CreateGate = new();

    // Runs when a slow call starts, before it waits.
    internal static Action? SlowStarted { get; set; }

    // The line the stdio server writes to stderr at start: the AGENTCAT_ENABLED value of its own environment.
    internal static string EnabledLine(string? value)
        => value is null ? "e2e: AGENTCAT_ENABLED is unset" : "e2e: AGENTCAT_ENABLED=" + value;

    internal static IMcpServerBuilder WithE2ETools(this IMcpServerBuilder builder, bool handlerTools)
        => handlerTools
            ? builder.WithListToolsHandler(ListAsync).WithCallToolHandler(CallAsync)
            : builder.WithTools(CreateAttributeTools());

    // The same tools on options for McpServer.Create.
    internal static void AddTo(McpServerOptions options, bool handlerTools)
    {
        if (handlerTools)
        {
            options.Handlers.ListToolsHandler = ListAsync;
            options.Handlers.CallToolHandler = CallAsync;
            return;
        }

        var tools = options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in CreateAttributeTools())
        {
            tools.Add(tool);
        }
    }

    internal static Implementation ServerInfo()
        => FromJson<Implementation>("{\"name\":\"" + ServerName + "\",\"version\":\"" + ServerVersion + "\"}");

    // New tool objects for every server: strict gets "additionalProperties": false, as the TS strict schema has.
    internal static IReadOnlyList<McpServerTool> CreateAttributeTools()
    {
        var tools = new List<McpServerTool>();
        foreach (var name in new[]
        {
            nameof(AttributeTools.Echo),
            nameof(AttributeTools.Loose),
            nameof(AttributeTools.Strict),
            nameof(AttributeTools.Structured),
            nameof(AttributeTools.Boom),
            nameof(AttributeTools.Custom),
            nameof(AttributeTools.Transport),
            nameof(AttributeTools.Slow),
        })
        {
            var tool = CreateTool(typeof(AttributeTools).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!);
            if (tool.ProtocolTool.Name == "strict")
            {
                var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
                schema["additionalProperties"] = false;
                tool.ProtocolTool.InputSchema = Element(schema.ToJsonString());
            }

            tools.Add(tool);
        }

        return tools;
    }

    // One at a time: two threads that create a tool from the same method for the first time can get different
    // ParameterInfo objects for one parameter, and McpServerTool.Create then lists a RequestContext parameter in the
    // input schema as if the client had to send it.
    internal static McpServerTool CreateTool(MethodInfo method)
    {
        lock (CreateGate)
        {
            return McpServerTool.Create(method, target: null, new McpServerToolCreateOptions());
        }
    }

    internal static CallToolResult Loose(IDictionary<string, JsonElement>? arguments) => Text(ReceivedJson(arguments));

    // The arguments a tool received, as one JSON object in their order.
    internal static string ReceivedJson(IDictionary<string, JsonElement>? arguments)
    {
        var received = new JsonObject();
        foreach (var argument in arguments ?? new Dictionary<string, JsonElement>())
        {
            received[argument.Key] = JsonNode.Parse(argument.Value.GetRawText());
        }

        return received.ToJsonString();
    }

    internal static CallToolResult Strict(IDictionary<string, JsonElement>? arguments)
    {
        var unexpected = (arguments ?? new Dictionary<string, JsonElement>()).Keys
            .Where(k => !string.Equals(k, "msg", StringComparison.Ordinal))
            .ToArray();
        if (unexpected.Length > 0)
        {
            return Error(UnexpectedArgumentPrefix + string.Join(", ", unexpected));
        }

        var msg = StringArgument(arguments, "msg");
        return msg is null ? Error(MissingArgumentPrefix + "msg") : Text(msg);
    }

    internal static async Task<string> PublishCustomAsync(McpServer server, string? sid)
    {
        await AgentCatSdk.PublishCustomEventAsync(server, CustomCallerProjectId, new CustomEventData
        {
            SessionId = sid,
            ResourceName = CustomResourceName,
            Message = CustomMessage,
        }).ConfigureAwait(false);
        return CustomReply;
    }

    internal static string TransportSession(McpServer server) => server.SessionId ?? "";

    internal static async Task<string> SlowAsync(int ms, CancellationToken cancellationToken)
    {
        SlowStarted?.Invoke();
        await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
        return "slept " + ms;
    }

    private static ValueTask<ListToolsResult> ListAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
        => new(FromJson<ListToolsResult>(HandlerListing.Value));

    private static async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var name = context.Params?.Name;
        var arguments = context.Params?.Arguments;
        switch (name)
        {
            case "echo":
                return Text(StringArgument(arguments, "msg") ?? "");
            case "loose":
                await OverlapGate.PassAsync(context.Services, cancellationToken).ConfigureAwait(false);
                return Loose(arguments);
            case "strict":
                return Strict(arguments);
            case "structured":
                // As the attribute tool: the text is the JSON of the structured value.
                var echoed = new JsonObject { ["echoed"] = StringArgument(arguments, "msg") }.ToJsonString();
                return FromJson<CallToolResult>(
                    "{\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(echoed, TypeInfo<string>()) + "}],\"structuredContent\":" + echoed + "}");
            case "boom":
                throw new InvalidOperationException(BoomMessage);
            case "custom":
                return Text(await PublishCustomAsync(context.Server, StringArgument(arguments, "sid")).ConfigureAwait(false));
            case "transport":
                return Text(TransportSession(context.Server));
            case "slow":
                return Text(await SlowAsync(IntArgument(arguments, "ms") ?? 0, cancellationToken).ConfigureAwait(false));
            default:
                throw new McpProtocolException("Unknown tool: '" + name + "'", McpErrorCode.InvalidParams);
        }
    }

    private static string BuildListing()
        => "{\"tools\":["
            + string.Join(",", CreateAttributeTools().Select(t => JsonSerializer.Serialize(t.ProtocolTool, TypeInfo<Tool>())))
            + "]}";

    private static string? StringArgument(IDictionary<string, JsonElement>? arguments, string name)
        => arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? IntArgument(IDictionary<string, JsonElement>? arguments, string name)
        => arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static CallToolResult Text(string text)
        => FromJson<CallToolResult>("{\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text, TypeInfo<string>()) + "}]}");

    private static CallToolResult Error(string text)
        => FromJson<CallToolResult>(
            "{\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text, TypeInfo<string>()) + "}],\"isError\":true}");

    private static T FromJson<T>(string json)
        => (T)JsonSerializer.Deserialize(json, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T)))!;

    private static JsonTypeInfo<T> TypeInfo<T>()
        => (JsonTypeInfo<T>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T));

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static class AttributeTools
    {
        [McpServerTool(Name = "echo")]
        [Description("Echo a message back")]
        public static string Echo([Description("Message to echo")] string? msg = null) => msg ?? "";

        [McpServerTool(Name = "loose")]
        [Description("Loose schema: unknown keys pass through to the handler")]
        public static async Task<CallToolResult> Loose(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken, string? msg = null)
        {
            _ = msg;
            await OverlapGate.PassAsync(context.Services, cancellationToken).ConfigureAwait(false);
            return E2EToolkit.Loose(context.Params?.Arguments);
        }

        [McpServerTool(Name = "strict")]
        [Description("Strict schema: additionalProperties are rejected")]
        public static CallToolResult Strict(RequestContext<CallToolRequestParams> context, string msg)
        {
            _ = msg;
            return E2EToolkit.Strict(context.Params?.Arguments);
        }

        [McpServerTool(Name = "structured", UseStructuredContent = true)]
        [Description("Returns structuredContent per its outputSchema")]
        public static Echoed Structured(string msg) => new(msg);

        [McpServerTool(Name = "boom")]
        [Description("Always throws")]
        public static string Boom() => throw new InvalidOperationException(BoomMessage);

        [McpServerTool(Name = "custom")]
        [Description("Publishes a custom event for the session in sid")]
        public static Task<string> Custom(McpServer server, string? sid = null) => PublishCustomAsync(server, sid);

        [McpServerTool(Name = "transport")]
        [Description("Returns the transport session ID of the server")]
        public static string Transport(McpServer server) => TransportSession(server);

        [McpServerTool(Name = "slow")]
        [Description("Waits, then answers; a cancelled call stops waiting")]
        public static Task<string> Slow([Description("Milliseconds to wait")] int ms, CancellationToken cancellationToken) => SlowAsync(ms, cancellationToken);
    }

    // Registered as a singleton on an in-process lane, it holds each loose call until the given number of calls are
    // inside a tool at once, so a test can prove that its calls overlapped. After the bound, a waiting call opens the gate
    // for every later call and Reached stays false, so serial calls fail the test without hanging it.
    internal sealed class OverlapGate(int participants, TimeSpan bound)
    {
        private readonly TaskCompletionSource<bool> _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private volatile bool _reached;

        internal bool Reached => _reached;

        internal int Participants => participants;

        internal static Task PassAsync(IServiceProvider? services, CancellationToken cancellationToken)
            => services?.GetService(typeof(OverlapGate)) is OverlapGate gate ? gate.PassAsync(cancellationToken) : Task.CompletedTask;

        internal async Task PassAsync(CancellationToken cancellationToken)
        {
            // The last participant opens the gate only if no call has given up waiting before it arrived.
            if (Interlocked.Increment(ref _arrived) == participants && _open.TrySetResult(true))
            {
                _reached = true;
            }

            try
            {
                await _open.Task.WaitAsync(bound, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _open.TrySetResult(false);
            }
        }
    }

    internal sealed class Echoed(string value)
    {
        [JsonPropertyName("echoed")]
        [Description("The echoed message")]
        public string Value { get; } = value;
    }
}
