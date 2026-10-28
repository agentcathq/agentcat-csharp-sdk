using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentCat.Internal.Engine;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Engine;

public class ThrownExceptionTests
{
    private const string ToolCall = "mcp:tools/call";

    private const string PostHandlerPrefix = "Warning: AgentCat post-handler processing failed for tool ";

    private const string ServerInfoKey = "io.modelcontextprotocol/serverInfo";

    private const string EchoTool =
        """{"name":"echo","description":"echoes","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}""";

    private static readonly JsonSerializerOptions Reflection = new();

    private static readonly string SeamMarker = " " + typeof(CallToolSeam).FullName + ".";

    private static readonly string[] CancellationTypes = { "System.OperationCanceledException", "System.Threading.Tasks.TaskCanceledException" };

    public static TheoryData<string?> Protocols => McpHarness.ProtocolVersions;

    private static string Sid(string label)
    {
        var body = new StringBuilder();
        foreach (var c in label)
        {
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                body.Append(c);
            }
        }

        body.Append('0', 27);
        return "ses_" + body.ToString(0, 27);
    }

    private static string Quote(string text) => JsonSerializer.Serialize(text, Reflection);

    private static string Converted(string name) => "An error occurred invoking '" + name + "'.";

    private static string ConvertedResult(string text) => "{\"content\":[{\"type\":\"text\",\"text\":" + Quote(text) + "}],\"isError\":true}";

    private static int Tokens(string text) => (int)Math.Ceiling(Encoding.UTF8.GetByteCount(text) / 3.5);

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WithinAsync(Task task)
    {
        await FakeAgentCatApi.WithinAsync(task, FakeAgentCatApi.WaitBound);
        await task;
    }

    private static JsonObject CallEvent(IReadOnlyList<JsonObject> events) => Assert.Single(events, e => (string?)e["eventType"] == ToolCall);

    private static JsonObject EventFor(IReadOnlyList<JsonObject> events, string name)
        => Assert.Single(events, e => (string?)e["eventType"] == ToolCall && (string?)e["resourceName"] == name);

    private static string Json(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    private static string[] Texts(CallToolResult result) => result.Content.Select(c => Assert.IsType<TextContentBlock>(c).Text).ToArray();

    private static bool IsSessionText(string text) => text.StartsWith("[session_id", StringComparison.Ordinal);

    private static string HandleFrom(string text, string name) => new Regex(name + ": (\\S+)").Match(text).Groups[1].Value;

    private static JsonObject ErrorOf(JsonObject e) => Assert.IsType<JsonObject>(e["error"]);

    private static string[] Lines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

    private static InvalidOperationException ThrownBy(Func<int> action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The action did not throw.");
    }

    private static Exception Caught(Func<Exception> create)
    {
        try
        {
            throw create();
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static JsonObject[] FramesOf(JsonObject error) => Assert.IsType<JsonArray>(error["frames"]).Select(f => Assert.IsType<JsonObject>(f)).ToArray();

    private static void AssertFullError(JsonObject error, string type, string message)
    {
        Assert.Equal(type, (string?)error["type"]);
        Assert.Equal(message, (string?)error["message"]);
        Assert.Equal("csharp", (string?)error["platform"]);
        Assert.False(string.IsNullOrEmpty((string?)error["stack"]));
        Assert.NotEmpty(FramesOf(error));
    }

    private static string? PhaseOf(RequestContext<CallToolRequestParams> context)
        => context.Params?.Arguments is { } arguments && arguments.TryGetValue("phase", out var phase) ? phase.GetString() : null;

    private static McpRequestFilter<CallToolRequestParams, CallToolResult> Around(
        Func<RequestContext<CallToolRequestParams>, McpRequestHandler<CallToolRequestParams, CallToolResult>, CancellationToken, ValueTask<CallToolResult>> filter)
        => next => (context, cancellationToken) => filter(context, next, cancellationToken);

    private static void InstallWithOuterFilter(
        EngineHarness h,
        Func<RequestContext<CallToolRequestParams>, McpRequestHandler<CallToolRequestParams, CallToolResult>, CancellationToken, ValueTask<CallToolResult>> outer)
        => h.Configure(o =>
        {
            h.Install(o);
            o.Filters.Request.CallToolFilters.Insert(0, Around(outer));
        });

    private static void FailAt(EngineHarness h, string stage, string message)
        => h.Tracker!.OnCallStage = s =>
        {
            if (s == stage)
            {
                throw new InvalidOperationException(message);
            }
        };

    private static EngineHarness Todos(AgentCatOptions? options = null)
    {
        var h = new EngineHarness("test-project", options);
        var todos = new List<string>();
        h.AddTool(
            (string text) =>
            {
                lock (todos)
                {
                    todos.Add(text);
                    return "Added todo: \"" + text + "\" with ID " + todos.Count;
                }
            },
            "add_todo",
            "Add a new todo item");
        h.AddTool((string id) => CompleteTodo(id), "complete_todo", "Mark a todo item as completed");
        return h;
    }

    private static EngineHarness LowLevel(AgentCatOptions? options = null)
    {
        var h = new EngineHarness("proj_test", options);
        h.AddHandlerTool(EchoTool, (context, _) =>
        {
            var arguments = context.Params?.Arguments;
            var text = arguments is not null && arguments.TryGetValue("text", out var value) ? value.GetString() : null;
            if (text == "throw")
            {
                throw new InvalidOperationException("low-level handler exploded");
            }

            return new ValueTask<CallToolResult>(Mcp.Result("{\"content\":[{\"type\":\"text\",\"text\":" + Quote("echo: " + text) + "}]}"));
        });
        return h;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string CompleteTodo(string id) => throw new KeyNotFoundException("Todo with ID " + id + " not found");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string Explode() => throw new InvalidOperationException("secret detail");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ThrowBicep() => throw new FileNotFoundException("template.bicep missing");

    // engine-callwrap.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Publishes_an_error_event_with___agentcat_error_stack_when_present(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddHandlerTool(EchoTool, (context, _) =>
        {
            context.Items[CallToolSeam.ExceptionItem] = new InvalidOperationException("inner boom");
            return new ValueTask<CallToolResult>(Mcp.Result("""{"content":[],"isError":true}"""));
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("echo", "{}");

        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(true, (bool?)e["isError"]);
        Assert.Contains("inner boom", (string?)ErrorOf(e)["message"], StringComparison.Ordinal);
        Assert.Equal("System.InvalidOperationException", (string?)ErrorOf(e)["type"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Publishes_an_error_event_and_rethrows_when_the_original_handler_throws(string? protocolVersion)
    {
        var boom = new InvalidOperationException("handler boom");
        Exception? rethrown = null;
        await using var h = new EngineHarness();
        h.AddHandlerTool(EchoTool, (_, _) => throw boom);
        InstallWithOuterFilter(h, async (context, next, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception ex)
            {
                rethrown = ex;
                throw;
            }
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("echo", """{"msg":"x"}""");

        Assert.Same(boom, rethrown);
        Assert.True(result.IsError);
        Assert.Equal(Converted("echo"), Texts(result).Last());
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(true, (bool?)e["isError"]);
        Assert.Contains("handler boom", (string?)ErrorOf(e)["message"], StringComparison.Ordinal);
        Assert.True((int?)e["duration"] >= 0);
    }

    // engine-innertap.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Captures_thrown_errors_onto_extra___agentcat_error_and_rethrows(string? protocolVersion)
    {
        var boom = new InvalidOperationException("boom");
        object? stashed = null;
        Exception? caught = null;
        await using var h = new EngineHarness();
        h.AddHandlerTool(EchoTool, (_, _) => throw boom);
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around(async (context, next, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception ex)
            {
                caught = ex;
                context.Items.TryGetValue(CallToolSeam.ExceptionItem, out stashed);
                throw;
            }
        })));
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("echo", """{"text":"a"}""");

        Assert.Same(boom, caught);
        Assert.Same(boom, stashed);
        Assert.True(result.IsError);
        Assert.Equal("boom", (string?)ErrorOf(CallEvent(await h.FlushAsync()))["message"]);
    }

    // error-capture.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_capture_stack_traces_when_tool_throws_Error(string? protocolVersion)
    {
        await using var h = Todos(new AgentCatOptions { EnableTracing = true });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("complete_todo", """{"id":"nonexistent-id","context":"Testing error capture"}""");

        Assert.True(result.IsError);
        var e = EventFor(await h.FlushAsync(), "complete_todo");
        Assert.Equal(true, (bool?)e["isError"]);
        var error = ErrorOf(e);
        Assert.Contains("not found", (string?)error["message"], StringComparison.Ordinal);
        AssertFullError(error, "System.Collections.Generic.KeyNotFoundException", "Todo with ID nonexistent-id not found");
        Assert.Contains(FramesOf(error), f => (string?)f["function"] == nameof(CompleteTodo));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_capture_Error_cause_chains(string? protocolVersion)
    {
        await using var h = Todos();
        h.AddTool(
            new Func<string>(() => throw new InvalidOperationException("Wrapper error", new ArgumentException("Root cause error"))),
            "error_with_cause",
            "Throws error with cause");
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("error_with_cause", """{"context":"Testing error.cause chains"}""");

        Assert.True(result.IsError);
        var error = ErrorOf(EventFor(await h.FlushAsync(), "error_with_cause"));
        AssertFullError(error, "System.InvalidOperationException", "Wrapper error");
        var chained = Assert.Single(Assert.IsType<JsonArray>(error["chained_errors"]));
        Assert.Equal("Root cause error", (string?)chained!["message"]);
        Assert.Equal("System.ArgumentException", (string?)chained["type"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_capture_TypeError_with_correct_type(string? protocolVersion)
    {
        await using var h = Todos();
        h.AddTool(
            new Func<string>(() =>
            {
                string? missing = null;
                return missing!.ToUpperInvariant();
            }),
            "type_error_tool",
            "Throws NullReferenceException");
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("type_error_tool", """{"context":"Testing TypeError capture"}""");

        Assert.True(result.IsError);
        var error = ErrorOf(EventFor(await h.FlushAsync(), "type_error_tool"));
        Assert.Equal("System.NullReferenceException", (string?)error["type"]);
        Assert.False(string.IsNullOrEmpty((string?)error["message"]));
        Assert.False(string.IsNullOrEmpty((string?)error["stack"]));
        Assert.NotEmpty(FramesOf(error));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_still_propagate_errors_to_MCP_client(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("complete_todo", """{"id":"invalid","context":"Testing error propagation"}""");

        Assert.True(result.IsError);
        var texts = Texts(result);
        Assert.Equal(2, texts.Length);
        Assert.True(IsSessionText(texts[0]));
        Assert.Equal(Converted("complete_todo"), texts[1]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_capture_validation_errors_for_invalid_enum_values(string? protocolVersion)
    {
        await using var h = Todos();
        h.AddTool((Operation operation, double a, double b) => "Result: " + a, "calculate", "Perform calculations");
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("calculate", """{"operation":"modulo","a":10,"b":3,"context":"Testing validation error capture"}""");

        Assert.True(result.IsError);
        Assert.Equal(Converted("calculate"), Texts(result).Last());
        var e = EventFor(await h.FlushAsync(), "calculate");
        Assert.Equal(true, (bool?)e["isError"]);
        var error = ErrorOf(e);
        Assert.Contains("could not be converted", (string?)error["message"], StringComparison.Ordinal);
        Assert.Equal("System.Text.Json.JsonException", (string?)error["type"]);
        Assert.False(string.IsNullOrEmpty((string?)error["stack"]));
        Assert.Equal(ConvertedResult(Converted("calculate")), Json(e["response"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_capture_errors_for_unknown_tool_names(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);

        var thrown = await Assert.ThrowsAnyAsync<McpException>(
            () => h.CallToolAsync("nonexistent_tool", """{"context":"Testing unknown tool error"}"""));

        Assert.Contains("Unknown tool", thrown.Message, StringComparison.Ordinal);
        var e = EventFor(await h.FlushAsync(), "nonexistent_tool");
        Assert.Equal(true, (bool?)e["isError"]);
        var error = ErrorOf(e);
        Assert.Equal("Unknown tool: 'nonexistent_tool'", (string?)error["message"]);
        Assert.Equal("ModelContextProtocol.McpProtocolException", (string?)error["type"]);
        Assert.False(string.IsNullOrEmpty((string?)error["stack"]));
        Assert.Null(e["response"]);
        Assert.Null(e["outputTokens"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Should_capture_validation_errors_for_missing_required_parameters(string? protocolVersion)
    {
        await using var h = Todos();
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("add_todo", """{"context":"Testing missing parameter"}""");

        Assert.True(result.IsError);
        Assert.Equal(Converted("add_todo"), Texts(result).Last());
        var e = EventFor(await h.FlushAsync(), "add_todo");
        Assert.Equal(true, (bool?)e["isError"]);
        var error = ErrorOf(e);
        Assert.Contains("text", (string?)error["message"], StringComparison.Ordinal);
        Assert.Equal("System.ArgumentException", (string?)error["type"]);
        Assert.False(string.IsNullOrEmpty((string?)error["stack"]));
    }

    // v2/late-registration.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Captures_full_error_stacks_from_a_late_registered_tool_via_the_executor_tap(string? protocolVersion)
    {
        var boom = new InvalidOperationException("late kaboom with stack");
        await using var h = new EngineHarness();
        h.Configure(o =>
        {
            h.Install(o);
            o.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
            o.ToolCollection.Add(McpServerTool.Create(
                new Func<Task<string>>(async () =>
                {
                    await Task.Yield();
                    throw boom;
                }),
                new McpServerToolCreateOptions { Name = "late_explode", Description = "Throws" }));
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("late_explode", "{\"session_id\":" + Quote(Sid("late_err")) + "}");

        Assert.True(result.IsError);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("late_err"), (string?)e["sessionId"]);
        Assert.Equal(true, (bool?)e["isError"]);
        Assert.Contains("late kaboom with stack", (string?)ErrorOf(e)["message"], StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty((string?)ErrorOf(e)["stack"]));
    }

    // v2/lowlevel-handles.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Publishes_the_event_and_rethrows_when_the_low_level_handler_throws(string? protocolVersion)
    {
        Exception? rethrown = null;
        await using var h = LowLevel();
        InstallWithOuterFilter(h, async (context, next, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception ex)
            {
                rethrown = ex;
                throw;
            }
        });
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("echo", "{\"text\":\"throw\",\"context\":\"testing\",\"session_id\":" + Quote(Sid("boom")) + "}");

        Assert.Equal("low-level handler exploded", Assert.IsType<InvalidOperationException>(rethrown).Message);
        Assert.True(result.IsError);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("boom"), (string?)e["sessionId"]);
        Assert.Equal(true, (bool?)e["isError"]);
        Assert.Contains("low-level handler exploded", (string?)ErrorOf(e)["message"], StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty((string?)ErrorOf(e)["stack"]));
    }

    // v2/track-highlevel.test.ts

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Echoes_a_supplied_session_id_and_captures_error_stacks_via_the_executor_tap(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool(new Func<string>(() => throw new InvalidOperationException("kaboom with stack")), "explode", "Throws");
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var result = await h.CallToolAsync("explode", "{\"session_id\":" + Quote(Sid("fixed123")) + "}");

        Assert.True(result.IsError);
        Assert.Equal(new[] { Converted("explode") }, Texts(result));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(Sid("fixed123"), (string?)e["sessionId"]);
        Assert.Equal(true, (bool?)e["isError"]);
        Assert.Contains("kaboom with stack", (string?)ErrorOf(e)["message"], StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty((string?)ErrorOf(e)["stack"]));
    }

    // C#: thrown exceptions and the outgoing seam

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task User_filter_that_converts_exceptions_still_yields_the_raw_exception(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool(new Func<string>(ThrowBicep), "deploy", "Deploys a template");
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around(async (context, next, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception ex)
            {
                return Mcp.Result("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":" + Quote("Bicep failed: " + ex.Message) + "}]}");
            }
        })));
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("deploy", "{}");

        Assert.True(result.IsError);
        var texts = Texts(result);
        Assert.Equal(2, texts.Length);
        Assert.True(IsSessionText(texts[0]));
        Assert.Equal("Bicep failed: template.bicep missing", texts[1]);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(true, (bool?)e["isError"]);
        var error = ErrorOf(e);
        AssertFullError(error, "System.IO.FileNotFoundException", "template.bicep missing");
        var frame = Assert.Single(FramesOf(error), f => (string?)f["function"] == nameof(ThrowBicep));
        Assert.Equal(typeof(ThrownExceptionTests).FullName, (string?)frame["module"]);
        Assert.Equal(ConvertedResult("Bicep failed: template.bicep missing"), Json(e["response"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Thrown_exception_result_carries_the_session_text(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool(new Func<string>(Explode), "explode", "Throws");
        await h.StartAsync(protocolVersion);
        await h.ListToolsAsync();

        var first = await h.CallToolAsync("explode", "{}");
        var sessionId = HandleFrom(Texts(first)[0], "session_id");
        var second = await h.CallToolAsync("explode", "{\"session_id\":" + Quote(sessionId) + "}");

        Assert.True(first.IsError);
        var texts = Texts(first);
        Assert.Equal(2, texts.Length);
        Assert.StartsWith("[session_id issued", texts[0], StringComparison.Ordinal);
        Assert.Equal(Converted("explode"), texts[1]);
        Assert.True(second.IsError);
        Assert.Equal(new[] { Converted("explode") }, Texts(second));
        var events = (await h.FlushAsync()).Where(e => (string?)e["eventType"] == ToolCall).ToArray();
        Assert.Equal(2, events.Length);
        foreach (var e in events)
        {
            Assert.Equal(sessionId, (string?)e["sessionId"]);
            Assert.Equal(true, (bool?)e["isError"]);
            Assert.Equal(ConvertedResult(Converted("explode")), Json(e["response"]));
            Assert.Equal(Tokens(Converted("explode")), (int?)e["outputTokens"]);
            AssertFullError(ErrorOf(e), "System.InvalidOperationException", "secret detail");
            Assert.Contains(FramesOf(ErrorOf(e)), f => (string?)f["function"] == nameof(Explode));
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Session_text_edit_keeps_the_2026_result_type_and_server_info(string? protocolVersion)
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "resultType and _meta.serverInfo exist only on MCP 2.x.");
        var sent = new List<string>();
        await using var h = new EngineHarness();
        h.AddTool(new Func<string>(Explode), "explode", "Throws");
        h.Configure(o => o.Filters.Message.OutgoingFilters.Add(next => (context, ct) =>
        {
            if (context.JsonRpcMessage is JsonRpcResponse { Result: JsonObject result } && result["isError"] is not null)
            {
                lock (sent)
                {
                    sent.Add(result.ToJsonString());
                }
            }

            return next(context, ct);
        }));
        await h.StartAsync(protocolVersion);

        var client = await h.CallToolAsync("explode", "{}");

        var wire = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.Single(sent)));
        var content = Assert.IsType<JsonArray>(wire["content"]);
        Assert.Equal(2, content.Count);
        Assert.StartsWith("[session_id issued", (string?)content[0]?["text"], StringComparison.Ordinal);
        Assert.Equal(Converted("explode"), (string?)content[1]?["text"]);
        Assert.Equal(true, (bool?)wire["isError"]);
        if (protocolVersion is null)
        {
            Assert.Equal("complete", (string?)wire["resultType"]);
            Assert.IsType<JsonObject>(wire["_meta"]?[ServerInfoKey]);
        }
        else
        {
            Assert.Null(wire["resultType"]);
            Assert.Null(wire["_meta"]);
        }

        Assert.Equal(Texts(client), content.Select(b => (string)b!["text"]!).ToArray());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Protocol_errors_and_cancellation_get_no_text_and_no_response(string? protocolVersion)
    {
        var started = Signal<string>();
        var finished = Signal<bool>();
        var textWritten = new Dictionary<string, bool>();
        await using var h = new EngineHarness();
        h.AddTool(new Func<string>(() => throw new McpProtocolException("bad shape", McpErrorCode.InvalidParams)), "bad_shape", "Protocol error");
        h.AddTool(
            async (CancellationToken cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return "never";
            },
            "wait_forever",
            "Waits until cancelled");
        InstallWithOuterFilter(h, async (context, next, ct) =>
        {
            var name = context.Params?.Name ?? string.Empty;
            if (name == "wait_forever")
            {
                started.TrySetResult(JsonSerializer.Serialize(context.JsonRpcRequest.Id, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(RequestId))));
            }

            try
            {
                return await next(context, ct);
            }
            catch (Exception)
            {
                lock (textWritten)
                {
                    textWritten[name] = context.Items.ContainsKey(CallToolSeam.SessionTextItem);
                }

                throw;
            }
            finally
            {
                if (name == "wait_forever")
                {
                    finished.TrySetResult(true);
                }
            }
        });
        await h.StartAsync(protocolVersion);

        var protocolError = await Assert.ThrowsAnyAsync<McpException>(() => h.CallToolAsync("bad_shape", "{}"));
        using (var abandon = new CancellationTokenSource())
        {
            var call = h.Client.CallToolAsync(Mcp.CallToolParams("""{"name":"wait_forever","arguments":{}}"""), abandon.Token).AsTask();
            await WithinAsync(started.Task);
            var cancelled = (JsonRpcMessage)JsonSerializer.Deserialize(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":" + await started.Task + ",\"reason\":\"test\"}}",
                McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage)))!;
            await h.Client.SendMessageAsync(cancelled, TestContext.Current.CancellationToken);
            await WithinAsync(finished.Task);
            abandon.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }

        Assert.Contains("bad shape", protocolError.Message, StringComparison.Ordinal);
        Assert.False(textWritten["bad_shape"]);
        Assert.False(textWritten["wait_forever"]);
        var events = await h.FlushAsync();
        var bad = EventFor(events, "bad_shape");
        Assert.Equal(true, (bool?)bad["isError"]);
        Assert.Equal("ModelContextProtocol.McpProtocolException", (string?)ErrorOf(bad)["type"]);
        Assert.Equal("bad shape", (string?)ErrorOf(bad)["message"]);
        Assert.Null(bad["response"]);
        Assert.Null(bad["outputTokens"]);
        var cancelledEvent = EventFor(events, "wait_forever");
        Assert.Equal(true, (bool?)cancelledEvent["isError"]);
        Assert.Contains((string?)ErrorOf(cancelledEvent)["type"], CancellationTypes);
        Assert.Null(cancelledEvent["response"]);
        Assert.Null(cancelledEvent["outputTokens"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Operation_canceled_without_a_cancelled_call_is_converted_and_gets_the_session_text(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool(new Func<string>(() => throw new OperationCanceledException("inner timeout")), "times_out", "Times out inside");
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("times_out", "{}");

        Assert.True(result.IsError);
        var texts = Texts(result);
        Assert.Equal(2, texts.Length);
        Assert.True(IsSessionText(texts[0]));
        Assert.Equal(Converted("times_out"), texts[1]);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("System.OperationCanceledException", (string?)ErrorOf(e)["type"]);
        Assert.Equal(ConvertedResult(Converted("times_out")), Json(e["response"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Aggregate_exception_thrown_through_the_server_follows_its_first_inner(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool(
            new Func<string>(() => throw new AggregateException("several failed", new InvalidOperationException("first"), new ArgumentException("second"))),
            "fan_out",
            "Throws an aggregate");
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("fan_out", "{}");

        Assert.Equal(Converted("fan_out"), Texts(result).Last());
        var error = ErrorOf(CallEvent(await h.FlushAsync()));
        Assert.Equal("System.AggregateException", (string?)error["type"]);
        var chained = Assert.Single(Assert.IsType<JsonArray>(error["chained_errors"]));
        Assert.Equal("first", (string?)chained!["message"]);
        Assert.Equal("System.InvalidOperationException", (string?)chained["type"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Mcp_exception_text_includes_the_message(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool(new Func<string>(() => throw new McpException("quota exhausted")), "quota", "Throws an McpException");
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("quota", "{}");

        var expected = "An error occurred invoking 'quota': quota exhausted";
        var texts = Texts(result);
        Assert.Equal(2, texts.Length);
        Assert.True(IsSessionText(texts[0]));
        Assert.Equal(expected, texts[1]);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(ConvertedResult(expected), Json(e["response"]));
        Assert.Equal(Tokens(expected), (int?)e["outputTokens"]);
        Assert.Equal("ModelContextProtocol.McpException", (string?)ErrorOf(e)["type"]);
        Assert.Equal("quota exhausted", (string?)ErrorOf(e)["message"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_user_filter_that_wraps_the_exception_keeps_the_raw_error_and_the_SDK_text_of_the_wrapper(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool(new Func<string>(Explode), "explode", "Throws");
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around(async (context, next, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (InvalidOperationException ex)
            {
                throw new McpException("wrapped: " + ex.Message, ex);
            }
        })));
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("explode", "{}");

        var expected = "An error occurred invoking 'explode': wrapped: secret detail";
        Assert.Equal(expected, Texts(result).Last());
        Assert.True(IsSessionText(Texts(result)[0]));
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal(ConvertedResult(expected), Json(e["response"]));
        AssertFullError(ErrorOf(e), "System.InvalidOperationException", "secret detail");
        Assert.Null(ErrorOf(e)["chained_errors"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_failure_while_handling_a_thrown_call_logs_and_passes_the_exception_on(string? protocolVersion)
    {
        Exception? rethrown = null;
        await using var h = new EngineHarness();
        h.AddTool(new Func<string>(Explode), "explode", "Throws");
        InstallWithOuterFilter(h, async (context, next, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception ex)
            {
                rethrown = ex;
                throw;
            }
        });
        await h.StartAsync(protocolVersion);
        FailAt(h, "fail", "fail boom");

        var result = await h.CallToolAsync("explode", "{}");

        Assert.Equal("secret detail", Assert.IsType<InvalidOperationException>(rethrown).Message);
        Assert.Equal(new[] { Converted("explode") }, Texts(result));
        Assert.Contains(
            PostHandlerPrefix + "explode; returning the customer's result untouched - InvalidOperationException: fail boom",
            h.Log.Lines);
        Assert.Empty(await h.FlushAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Tracing_off_thrown_calls_publish_nothing_and_get_no_text(string? protocolVersion)
    {
        var keys = new List<string[]>();
        await using var h = new EngineHarness(options: new AgentCatOptions { EnableTracing = false });
        h.AddTool(new Func<string>(Explode), "explode", "Throws");
        InstallWithOuterFilter(h, async (context, next, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception)
            {
                lock (keys)
                {
                    keys.Add(context.Items.Keys.ToArray());
                }

                throw;
            }
        });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("explode", "{}");

        Assert.Equal(new[] { Converted("explode") }, Texts(result));
        var seen = Assert.Single(keys);
        Assert.DoesNotContain(CallToolSeam.ExceptionItem, seen);
        Assert.DoesNotContain(CallToolSeam.SessionTextItem, seen);
        Assert.Empty(await h.FlushAsync());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Captured_frames_and_stack_do_not_depend_on_when_the_pipeline_reads_them(string? protocolVersion)
    {
        var beforeCaptured = Signal<bool>();
        var afterReleased = Signal<bool>();
        await using var h = new EngineHarness(options: new AgentCatOptions
        {
            Identify = async (call, _) =>
            {
                if (call.Arguments.TryGetValue("phase", out var phase) && phase.GetString() == "after")
                {
                    await afterReleased.Task;
                }

                return null;
            },
            RedactEvent = (e, _) =>
            {
                if ((string?)e.Parameters?["request"]?["params"]?["arguments"]?["phase"] == "before")
                {
                    beforeCaptured.TrySetResult(true);
                }

                return new ValueTask<AgentCatEvent?>(e);
            },
        });
        h.AddHandlerTool(
            """{"name":"explode","inputSchema":{"type":"object","properties":{"phase":{"type":"string"}}}}""",
            (_, _) => new ValueTask<CallToolResult>(Mcp.Result("{\"content\":[{\"type\":\"text\",\"text\":" + Quote(Explode()) + "}]}")));
        InstallWithOuterFilter(h, async (context, next, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception) when (PhaseOf(context) == "before")
            {
                await WithinAsync(beforeCaptured.Task);
                throw;
            }
        });
        await h.StartAsync(protocolVersion);

        var before = await h.CallToolAsync("explode", """{"phase":"before"}""");
        var after = await h.CallToolAsync("explode", """{"phase":"after"}""");
        afterReleased.SetResult(true);
        var events = await h.FlushAsync();

        Assert.True(before.IsError);
        Assert.True(after.IsError);
        var errors = events.Where(e => (string?)e["eventType"] == ToolCall).Select(ErrorOf).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Equal(Json(errors[0]["frames"]), Json(errors[1]["frames"]));
        Assert.Equal((string?)errors[0]["stack"], (string?)errors[1]["stack"]);
        var frames = FramesOf(errors[1]);
        Assert.Equal(nameof(Explode), (string?)frames[0]["function"]);
        Assert.Equal(typeof(CallToolSeam).FullName, (string?)frames[frames.Length - 1]["module"]);
        var stack = ((string?)errors[1]["stack"])!.Split('\n');
        Assert.Contains(typeof(CallToolSeam).FullName + ".", stack[stack.Length - 1], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_thrown_aggregate_keeps_every_inner_exception_after_the_seam_cut(string? protocolVersion)
    {
        var released = Signal<bool>();
        AggregateException? thrown = null;
        await using var h = new EngineHarness(options: new AgentCatOptions
        {
            Identify = async (_, _) =>
            {
                await released.Task;
                return null;
            },
        });
        h.AddHandlerTool(
            """{"name":"explode","inputSchema":{"type":"object"}}""",
            (_, _) =>
            {
                thrown = new AggregateException(
                    "both failed",
                    Caught(() => new InvalidOperationException("first inner")),
                    Caught(() => new ArgumentException("second inner")));
                throw thrown;
            });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("explode");
        released.SetResult(true);
        var error = ErrorOf(CallEvent(await h.FlushAsync()));

        Assert.True(result.IsError);
        var stack = (string)error["stack"]!;
        Assert.StartsWith("System.AggregateException: both failed", stack, StringComparison.Ordinal);
        Assert.Contains(thrown!.InnerExceptions[0].ToString(), stack, StringComparison.Ordinal);
        Assert.Contains(thrown.InnerExceptions[1].ToString(), stack, StringComparison.Ordinal);
        Assert.EndsWith("<---" + Environment.NewLine, stack, StringComparison.Ordinal);
        var lines = Lines(stack);
        var seam = Assert.Single(Enumerable.Range(0, lines.Length), i => lines[i].Contains(SeamMarker));
        Assert.StartsWith("---> ", lines[seam + 1].TrimStart(), StringComparison.Ordinal);
        Assert.DoesNotContain(lines.Skip(seam + 1), line => line.Contains(" ModelContextProtocol."));
        Assert.Equal(typeof(CallToolSeam).FullName, (string?)FramesOf(error).Last()["module"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_thrown_aggregate_with_a_compiled_lambda_inner_keeps_its_inner_sections(string? protocolVersion)
    {
        var released = Signal<bool>();
        AggregateException? thrown = null;
        await using var h = new EngineHarness(options: new AgentCatOptions
        {
            Identify = async (_, _) =>
            {
                await released.Task;
                return null;
            },
        });
        h.AddHandlerTool(
            """{"name":"explode","inputSchema":{"type":"object"}}""",
            (_, _) =>
            {
                var compiled = System.Linq.Expressions.Expression
                    .Lambda<Func<int>>(
                        System.Linq.Expressions.Expression.Throw(
                            System.Linq.Expressions.Expression.Constant(new InvalidOperationException("from a lambda")),
                            typeof(int)))
                    .Compile();
                thrown = new AggregateException(
                    "both failed",
                    Caught(() => new ArgumentException("first inner")),
                    ThrownBy(compiled));
                throw thrown;
            });
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolAsync("explode");
        released.SetResult(true);
        var error = ErrorOf(CallEvent(await h.FlushAsync()));

        Assert.True(result.IsError);
        var stack = (string)error["stack"]!;
        Assert.Contains("lambda_method(", stack, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"lambda_method\d", stack);
        Assert.Contains(thrown!.InnerExceptions[0].ToString(), stack, StringComparison.Ordinal);
        Assert.Contains("(Inner Exception #1)", stack, StringComparison.Ordinal);
        Assert.EndsWith("<---" + Environment.NewLine, stack, StringComparison.Ordinal);
        var lines = Lines(stack);
        var seam = Assert.Single(Enumerable.Range(0, lines.Length), i => lines[i].Contains(SeamMarker));
        Assert.StartsWith("---> ", lines[seam + 1].TrimStart(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task A_seam_frame_inside_an_inner_exception_does_not_cut_the_outer_trace(string? protocolVersion)
    {
        var released = Signal<bool>();
        Exception? earlier = null;
        await using var h = new EngineHarness(options: new AgentCatOptions
        {
            Identify = async (call, _) =>
            {
                if (call.Arguments.TryGetValue("phase", out var phase) && phase.GetString() == "wrap")
                {
                    await released.Task;
                }

                return null;
            },
        });
        h.AddHandlerTool(
            """{"name":"explode","inputSchema":{"type":"object","properties":{"phase":{"type":"string"}}}}""",
            (context, _) => PhaseOf(context) == "first"
                ? throw new InvalidOperationException("earlier failure")
                : throw new InvalidOperationException("wrapped failure", earlier));
        InstallWithOuterFilter(h, async (context, next, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception ex) when (PhaseOf(context) == "first")
            {
                earlier = ex;
                throw;
            }
        });
        await h.StartAsync(protocolVersion);

        await h.CallToolAsync("explode", """{"phase":"first"}""");
        var result = await h.CallToolAsync("explode", """{"phase":"wrap"}""");
        released.SetResult(true);
        var events = await h.FlushAsync();

        Assert.True(result.IsError);
        var error = ErrorOf(Assert.Single(events, e => (string?)e["parameters"]?["request"]?["params"]?["arguments"]?["phase"] == "wrap"));
        var stack = (string)error["stack"]!;
        Assert.StartsWith("System.InvalidOperationException: wrapped failure", stack, StringComparison.Ordinal);
        var inner = earlier!.ToString();
        Assert.Contains(SeamMarker, inner, StringComparison.Ordinal);
        var at = stack.IndexOf(inner, StringComparison.Ordinal);
        Assert.True(at > 0, stack);
        var own = Lines(stack.Substring(at + inner.Length));
        Assert.Single(own, line => line.Contains(SeamMarker));
        Assert.Contains(SeamMarker, own[own.Length - 1], StringComparison.Ordinal);
        Assert.Equal(typeof(CallToolSeam).FullName, (string?)FramesOf(error).Last()["module"]);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Thrown_errors_on_1x_task_calls_still_get_the_session_text(string? protocolVersion)
    {
        Assert.SkipWhen(McpHarness.McpMajor >= 2, "Core task-augmented tools/call exists only on MCP 1.x.");
        await using var h = new EngineHarness();
        h.AddTool((string text) => text, "echo", "Echo");
        h.Configure(o => o.Filters.Request.CallToolFilters.Add(Around((context, next, ct) =>
            context.JsonRpcRequest.Params is JsonObject raw && raw["task"] is JsonObject
                ? throw new InvalidOperationException("task creation refused")
                : next(context, ct))));
        await h.StartAsync(protocolVersion);

        var result = await h.CallToolParamsAsync("""{"name":"echo","arguments":{"text":"x"},"task":{}}""");

        Assert.True(result.IsError);
        var texts = Texts(result);
        Assert.Equal(2, texts.Length);
        Assert.StartsWith("[session_id issued", texts[0], StringComparison.Ordinal);
        Assert.Equal(Converted("echo"), texts[1]);
        var e = CallEvent(await h.FlushAsync());
        Assert.Equal("task creation refused", (string?)ErrorOf(e)["message"]);
        Assert.Equal(ConvertedResult(Converted("echo")), Json(e["response"]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Outgoing_seam_changes_only_responses_that_carry_the_session_text(string? protocolVersion)
    {
        await using var h = new EngineHarness();
        h.AddTool((string text) => text, "echo");
        await h.StartAsync(protocolVersion);
        var seam = new OutgoingSeam(h.Tracker!);
        var passed = new List<JsonRpcMessage>();
        var handler = seam.Wrap((context, _) =>
        {
            passed.Add(context.JsonRpcMessage);
            return Task.CompletedTask;
        });

        async Task<JsonRpcMessage> SendAsync(string json, IDictionary<string, object?>? items, bool withContext = true)
        {
            var message = (JsonRpcMessage)JsonSerializer.Deserialize(json, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage)))!;
            if (withContext)
            {
                message.Context = new JsonRpcMessageContext { Items = items };
            }

            await handler(new MessageContext(h.Server, message), CancellationToken.None);
            Assert.Same(message, passed.Last());
            return message;
        }

        string ResultOf(JsonRpcMessage message) => Json(Assert.IsType<JsonRpcResponse>(message).Result);

        Dictionary<string, object?> WithText(object? value) => new() { [CallToolSeam.SessionTextItem] = value, ["user.key"] = 1 };

        var decorated = await SendAsync("""{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"boom"}],"isError":true}}""", WithText("[session_id issued]"));
        Assert.Equal("""{"content":[{"type":"text","text":"[session_id issued]"},{"type":"text","text":"boom"}],"isError":true}""", ResultOf(decorated));
        Assert.Equal("user.key", Assert.Single(decorated.Context!.Items!.Keys));

        var noContent = await SendAsync("""{"jsonrpc":"2.0","id":2,"result":{"task":{"taskId":"t1","status":"working"}}}""", WithText("[session_id issued]"));
        Assert.Equal("""{"task":{"taskId":"t1","status":"working"}}""", ResultOf(noContent));
        Assert.True(noContent.Context!.Items!.ContainsKey(CallToolSeam.SessionTextItem));

        var stringContent = await SendAsync("""{"jsonrpc":"2.0","id":3,"result":{"content":"x"}}""", WithText("[session_id issued]"));
        Assert.Equal("""{"content":"x"}""", ResultOf(stringContent));

        var notText = await SendAsync("""{"jsonrpc":"2.0","id":4,"result":{"content":[]}}""", WithText(42));
        Assert.Equal("""{"content":[]}""", ResultOf(notText));
        Assert.True(notText.Context!.Items!.ContainsKey(CallToolSeam.SessionTextItem));

        var plain = await SendAsync("""{"jsonrpc":"2.0","id":5,"result":{"content":[]}}""", null, withContext: false);
        Assert.Equal("""{"content":[]}""", ResultOf(plain));
        Assert.Null(plain.Context);

        var noItems = await SendAsync("""{"jsonrpc":"2.0","id":6,"result":{"content":[]}}""", null);
        Assert.Equal("""{"content":[]}""", ResultOf(noItems));
        Assert.Null(noItems.Context!.Items);

        var request = await SendAsync("""{"jsonrpc":"2.0","id":7,"method":"elicitation/create","params":{"content":[]}}""", WithText("[session_id issued]"));
        Assert.Equal("""{"content":[]}""", Json(Assert.IsType<JsonRpcRequest>(request).Params));
        Assert.True(request.Context!.Items!.ContainsKey(CallToolSeam.SessionTextItem));

        var throwing = await SendAsync("""{"jsonrpc":"2.0","id":8,"result":{"content":[]}}""", new ThrowingItems(throwOnCount: false));
        Assert.Equal("""{"content":[]}""", ResultOf(throwing));

        var throwingCount = await SendAsync("""{"jsonrpc":"2.0","id":9,"result":{"content":[]}}""", new ThrowingItems(throwOnCount: true));
        Assert.Equal("""{"content":[]}""", ResultOf(throwingCount));

        var error = await SendAsync("""{"jsonrpc":"2.0","id":10,"error":{"code":-32603,"message":"boom"}}""", WithText("[session_id issued]"));
        var detail = Assert.IsType<JsonRpcError>(error).Error;
        Assert.Equal(-32603, detail.Code);
        Assert.Equal("boom", detail.Message);
        Assert.True(error.Context!.Items!.ContainsKey(CallToolSeam.SessionTextItem));
        Assert.Equal(10, passed.Count);
    }

    private enum Operation
    {
        Add,
        Subtract,
        Multiply,
        Divide,
    }

    private sealed class ThrowingItems : Dictionary<string, object?>, IDictionary<string, object?>
    {
        private readonly bool _throwOnCount;

        internal ThrowingItems(bool throwOnCount)
        {
            _throwOnCount = throwOnCount;
            Add(CallToolSeam.SessionTextItem, "[session_id issued]");
        }

        int ICollection<KeyValuePair<string, object?>>.Count => _throwOnCount ? throw new InvalidOperationException("count boom") : Count;

        bool IDictionary<string, object?>.TryGetValue(string key, out object? value) => throw new InvalidOperationException("items boom");
    }
}
