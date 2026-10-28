#if NET
using System.Text.Json.Nodes;
using AgentCat.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCat.Tests.Http;

public class ProjectionTests
{
    private const string ToolCall = "mcp:tools/call";

    // One capture reads the accessor twice: for the copy, and after it to see that the request is still current.
    private const int ReadsPerCapture = 2;

    private const string TasksOptIn = """{"io.modelcontextprotocol/clientCapabilities":{"extensions":{"io.modelcontextprotocol/tasks":{}}}}""";

    [Fact]
    public async Task Extra_projection_has_request_info_after_the_other_keys()
    {
        var accessor = new CountingAccessor(Request());
        await using var h = Harness(accessor, new AgentCatOptions());
        await h.StartAsync(null, withServices: true);

        await h.CallToolParamsAsync("""{"name":"echo","arguments":{},"_meta":{"k":1}}""");

        var extra = Extra(await h.FlushAsync());
        var keys = extra.Select(p => p.Key).ToArray();
        Assert.Equal("requestId,_meta,requestInfo", string.Join(",", keys.Where(k => k != "sessionId")));
        Assert.Equal("requestInfo", keys[^1]);
        var info = Assert.IsType<JsonObject>(extra["requestInfo"]);
        Assert.Equal("headers,url", string.Join(",", info.Select(p => p.Key)));
        Assert.Equal("http://localhost/mcp", (string?)info["url"]);
        var headers = Assert.IsType<JsonObject>(info["headers"]);
        Assert.Equal("authorization,host,x-multi", string.Join(",", headers.Select(p => p.Key)));
        Assert.Equal("Bearer t", (string?)headers["authorization"]);
        Assert.Equal("a, b", (string?)headers["x-multi"]);
    }

    [Fact]
    public async Task No_capture_gives_no_request_info()
    {
        await using var h = Harness(new CountingAccessor(null), new AgentCatOptions());
        await h.StartAsync(null, withServices: true);

        await h.CallToolAsync("echo", "{}");

        Assert.False(Extra(await h.FlushAsync()).ContainsKey("requestInfo"));
    }

    [Fact]
    public async Task Hook_headers_match_the_event_headers()
    {
        AgentCatToolCall? seen = null;
        var options = new AgentCatOptions
        {
            EventProperties = (call, _) =>
            {
                seen = call;
                return new ValueTask<JsonObject?>((JsonObject?)null);
            },
        };
        await using var h = Harness(new CountingAccessor(Request()), options);
        await h.StartAsync(null, withServices: true);

        await h.CallToolAsync("echo", "{}");

        var headers = Assert.IsType<JsonObject>(Extra(await h.FlushAsync())["requestInfo"]?["headers"]);
        Assert.NotNull(seen);
        Assert.Equal(
            headers.Select(p => new KeyValuePair<string, string?>(p.Key, (string?)p.Value)).ToArray(),
            seen.Headers.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)).ToArray());
    }

    [Fact]
    public async Task Hook_headers_are_case_insensitive()
    {
        AgentCatToolCall? seen = null;
        var options = new AgentCatOptions
        {
            EventProperties = (call, _) =>
            {
                seen = call;
                return new ValueTask<JsonObject?>((JsonObject?)null);
            },
        };
        await using var h = Harness(new CountingAccessor(Request()), options);
        await h.StartAsync(null, withServices: true);

        await h.CallToolAsync("echo", "{}");

        Assert.NotNull(seen);
        Assert.Equal("Bearer t", seen.Headers["Authorization"]);
        Assert.Equal("Bearer t", seen.Headers["authorization"]);
        Assert.True(seen.Headers.TryGetValue("X-MULTI", out var multi));
        Assert.Equal("a, b", multi);
        Assert.All(seen.Headers.Keys, key => Assert.Equal(key.ToLowerInvariant(), key));
    }

    [Fact]
    public async Task Hook_headers_are_empty_without_a_capture()
    {
        AgentCatToolCall? seen = null;
        var options = new AgentCatOptions
        {
            EventProperties = (call, _) =>
            {
                seen = call;
                return new ValueTask<JsonObject?>((JsonObject?)null);
            },
        };
        await using var h = Harness(new CountingAccessor(null), options);
        await h.StartAsync(null, withServices: true);

        await h.CallToolAsync("echo", "{}");

        Assert.NotNull(seen);
        Assert.Empty(seen.Headers);
        Assert.False(seen.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task The_capture_happens_once_per_call()
    {
        var accessor = new CountingAccessor(Request());
        var options = new AgentCatOptions
        {
            EventProperties = (_, _) => new ValueTask<JsonObject?>((JsonObject?)null),
            Identify = (_, _) => new ValueTask<UserIdentity?>((UserIdentity?)null),
        };
        await using var h = Harness(accessor, options);
        await h.StartAsync(null, withServices: true);
        var before = accessor.Reads;

        await h.CallToolAsync("echo", "{}");
        Assert.Equal(ReadsPerCapture, accessor.Reads - before);
        await h.CallToolAsync("echo", "{}");
        Assert.Equal(2 * ReadsPerCapture, accessor.Reads - before);
        await h.FlushAsync();
    }

    [Fact]
    public async Task Tracing_off_reads_the_request_zero_times()
    {
        var accessor = new CountingAccessor(Request());
        await using var h = Harness(accessor, new AgentCatOptions { EnableTracing = false });
        await h.StartAsync(null, withServices: true);
        var before = accessor.Reads;

        await h.CallToolAsync("echo", "{}");
        await h.FlushAsync();

        Assert.Equal(0, accessor.Reads - before);
    }

    // MCP 2.x runs a call whose client opts in to tasks on a Task.Run that it starts before the POST returns, so the
    // request can end, and its context serve the next request on the connection, while AgentCat would read it. The
    // opt-in exists only on the 2026-07-28 protocol, the 2.x default: the server rejects it on 2025-11-25.
    [Fact]
    public async Task Calls_that_opt_in_to_mcp2_tasks_read_the_request_zero_times()
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "The MCP Tasks extension runs calls in the background only on ModelContextProtocol 2.x.");
        AgentCatToolCall? seen = null;
        var accessor = new CountingAccessor(Request());
        var options = new AgentCatOptions
        {
            EventProperties = (call, _) =>
            {
                seen = call;
                return new ValueTask<JsonObject?>((JsonObject?)null);
            },
        };
        await using var h = Harness(accessor, options);
        await h.StartAsync(null, withServices: true);
        var before = accessor.Reads;

        await h.CallToolParamsAsync("""{"name":"echo","arguments":{},"_meta":""" + TasksOptIn + "}");

        var extra = Extra(await h.FlushAsync());
        Assert.Equal(0, accessor.Reads - before);
        Assert.False(extra.ContainsKey("requestInfo"));
        Assert.NotNull(seen);
        Assert.Empty(seen.Headers);
    }

    private static EngineHarness Harness(CountingAccessor accessor, AgentCatOptions options)
    {
        var h = new EngineHarness(options: options);
        h.AddHandlerTool(
            """{"name":"echo","inputSchema":{"type":"object","properties":{}}}""",
            (_, _) => new ValueTask<ModelContextProtocol.Protocol.CallToolResult>(
                new ModelContextProtocol.Protocol.CallToolResult { Content = new List<ModelContextProtocol.Protocol.ContentBlock> { new ModelContextProtocol.Protocol.TextContentBlock { Text = "ok" } } }));
        h.ConfigureServices(s => s.AddSingleton<IHttpContextAccessor>(accessor));
        return h;
    }

    private static DefaultHttpContext Request()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = new PathString("/mcp");
        context.Request.Headers.Authorization = "Bearer t";
        context.Request.Headers.Append("X-Multi", "a");
        context.Request.Headers.Append("X-Multi", "b");
        return context;
    }

    private static JsonObject Extra(IReadOnlyList<JsonObject> events)
    {
        var call = Assert.Single(events, e => (string?)e["eventType"] == ToolCall);
        return Assert.IsType<JsonObject>(call["parameters"]?["extra"]);
    }

    private sealed class CountingAccessor(HttpContext? context) : IHttpContextAccessor
    {
        private int _reads;

        internal int Reads => Volatile.Read(ref _reads);

        public HttpContext? HttpContext
        {
            get
            {
                Interlocked.Increment(ref _reads);
                return context;
            }

            set { }
        }
    }
}
#endif
