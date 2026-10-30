using System.Text.Json;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Text;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Engine;

// Which tool call a custom event published with the server form, without a SessionId, belongs to.
public class EnclosingToolCallTests
{
    private const string NoSession =
        "publishCustomEvent: no sessionId provided; event will be published without a session. Pass eventData.sessionId to attribute it to a task.";

    private static CustomEventData Data(string resourceName) => new() { ResourceName = resourceName };

    private static async Task<EngineHarness> StartedAsync(string projectId, AgentCatOptions? options = null, Func<AgentCatClient, McpServer, Task>? publish = null)
    {
        var h = new EngineHarness(projectId, options);
        var client = new AgentCatClient(h.Runtime);
        h.AddTool(
            async (McpServer server, string msg) =>
            {
                if (publish is not null)
                {
                    await publish(client, server);
                }

                return "echo:" + msg;
            },
            "echo");
        await h.StartAsync();
        await h.ListToolsAsync();
        return h;
    }

#pragma warning disable MCP9003
    private static RequestContext<CallToolRequestParams> Context(McpServer server, string sessionId)
    {
        var paramsJson = "{\"name\":\"echo\",\"arguments\":{\"msg\":\"hi\",\"session_id\":" + JsonSerializer.Serialize(sessionId) + "}}";
        var request = (JsonRpcRequest)JsonSerializer.Deserialize(
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":" + paramsJson + "}",
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage)))!;
        return new RequestContext<CallToolRequestParams>(server, request) { Params = Mcp.CallToolParams(paramsJson) };
    }
#pragma warning restore MCP9003

    private static string? SessionOf(IEnumerable<AgentCatEvent> events, string resourceName)
        => Assert.Single(events, e => e.ResourceName == resourceName).SessionId;

    // Server A's tool runs a tool call of server B on its own async flow. Inside B's call only B's call counts, so a custom
    // event for A gets no session there; before and after it, A's call counts again; after A's call, nothing does.
    [Fact]
    public async Task Returns_only_the_innermost_call_for_nested_tool_calls()
    {
        await using var a = await StartedAsync("proj_a");
        await using var b = await StartedAsync("proj_b");
        var client = new AgentCatClient(a.Runtime);
        var sessionA = SessionIds.NewSessionId(DateTimeOffset.UtcNow);
        var sessionB = SessionIds.NewSessionId(DateTimeOffset.UtcNow);
        var inner = new CallToolSeam(b.Tracker!).Wrap(async (context, _) =>
        {
            await client.PublishCustomEventAsync(context.Server, "proj_x", Data("inner-own"));
            await client.PublishCustomEventAsync(a.Server, "proj_x", Data("inner-outer"));
            return Mcp.Result("""{"content":[{"type":"text","text":"inner"}]}""");
        });
        var outer = new CallToolSeam(a.Tracker!).Wrap(async (context, cancellationToken) =>
        {
            await client.PublishCustomEventAsync(context.Server, "proj_x", Data("outer-before"));
            await inner(Context(b.Server, sessionB), cancellationToken);
            await client.PublishCustomEventAsync(context.Server, "proj_x", Data("outer-after"));
            return Mcp.Result("""{"content":[{"type":"text","text":"outer"}]}""");
        });

        await outer(Context(a.Server, sessionA), TestContext.Current.CancellationToken);
        await client.PublishCustomEventAsync(a.Server, "proj_x", Data("after-the-call"));

        await a.FlushAsync();
        await b.FlushAsync();
        var custom = a.Sender.Sent.Where(e => e.EventType == WireNames.CustomEventType).ToArray();
        Assert.Equal(sessionA, Assert.Single(a.Sender.Sent, e => e.EventType == WireNames.ToolCallEventType).SessionId);
        Assert.Equal(sessionA, SessionOf(custom, "outer-before"));
        Assert.Equal("", SessionOf(custom, "inner-outer"));
        Assert.Equal(sessionA, SessionOf(custom, "outer-after"));
        Assert.Equal("", SessionOf(custom, "after-the-call"));
        Assert.Equal(2, a.Log.Lines.Count(l => l == NoSession));
        Assert.Equal(sessionB, Assert.Single(b.Sender.Sent, e => e.EventType == WireNames.ToolCallEventType).SessionId);
        Assert.Equal(sessionB, SessionOf(b.Sender.Sent.Where(e => e.EventType == WireNames.CustomEventType), "inner-own"));
        Assert.DoesNotContain(NoSession, b.Log.Lines);
    }

    // A call that AgentCat does not trace (tracing off, or its preparation failed) sets no enclosing call: the custom
    // event follows the no-session rule and its warning, and tracing off then drops it.
    [Theory]
    [InlineData("tracing off")]
    [InlineData("prepare fails")]
    public async Task Publishes_without_a_session_inside_a_call_that_AgentCat_does_not_trace(string reason)
    {
        await using var h = await StartedAsync(
            "proj_a",
            reason == "tracing off" ? new AgentCatOptions { EnableTracing = false } : null,
            (client, server) => client.PublishCustomEventAsync(server, "proj_x", Data("inside")));
        if (reason == "prepare fails")
        {
            h.Tracker!.OnCallStage = stage =>
            {
                if (stage == "prepare")
                {
                    throw new InvalidOperationException("prepare boom");
                }
            };
        }

        await h.CallToolAsync("echo", "{\"msg\":\"hi\",\"session_id\":" + JsonSerializer.Serialize(SessionIds.NewSessionId(DateTimeOffset.UtcNow)) + "}");

        await h.FlushAsync();
        Assert.Contains(NoSession, h.Log.Lines);
        Assert.Contains("Published custom event without a session with type 'agentcat:custom'", h.Log.Lines);
        if (reason == "tracing off")
        {
            Assert.Empty(h.Sender.Sent);
        }
        else
        {
            Assert.Equal("", Assert.Single(h.Sender.Sent).SessionId);
            Assert.Contains(h.Log.Lines, l => l.StartsWith("Warning: AgentCat tracing failed for tool echo", StringComparison.Ordinal));
        }
    }
}
