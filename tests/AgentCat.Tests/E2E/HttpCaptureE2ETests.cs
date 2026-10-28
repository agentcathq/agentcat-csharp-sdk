using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using ModelContextProtocol.AspNetCore;

namespace AgentCat.Tests.E2E;

// The HTTP request of a tool call, captured on real MCP servers: its headers and URL in parameters.extra.requestInfo and
// in AgentCatToolCall.Headers. Every header goes out verbatim, credentials included; RedactEvent removes them.
public class HttpCaptureE2ETests
{
    private const string SentinelHeader = "X-AgentCat-E2E-Sentinel";
    private const string SentinelValue = "sentinel-7f3a";
    private const string AuthorizationValue = "Bearer e2e-secret-token";
    private const string CookieValue = "e2e_session=e2e-cookie-secret; theme=dark";
    private const string AuthorizationSecret = "e2e-secret-token";
    private const string CookieSecret = "e2e-cookie-secret";

    // A streamable HTTP request of a session carries this header; the initialize POST that starts the session does not.
    private const string SessionIdHeader = "Mcp-Session-Id";

    // TestServer sets Host from the request URI; the client sends none of its own.
    private const string TestServerHost = "localhost";
    private const string TestServerUrl = "http://localhost/";

    private static readonly string[] InfoKeys = { "headers", "url" };

    // parameters.extra of a stateful call, _meta left out: the client sends it on some versions only.
    private static readonly string[] StatefulExtraKeys = { "sessionId", "requestId", "requestInfo" };

    // The client sends a sentinel and the two credentials that a customer may have to redact on every request. Each
    // call's event carries the URL and every header of the POST that carried that call, and of no other request.
    [Theory]
    [MemberData(nameof(LaneData.HttpLanes), MemberType = typeof(LaneData))]
    public async Task Headers_and_url_reach_the_event(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults);
        var requests = new ClientRequests();
        var client = await e2e.ConnectClientAsync(headers: CredentialHeaders(), requests: requests);

        var posts = new List<SentRequest[]>();
        for (var i = 0; i < 2; i++)
        {
            var before = requests.Sent.Count;
            E2EAssert.Succeeded(await e2e.CallToolAsync("echo", "{\"msg\":\"call" + i + "\",\"context\":\"headers reach the event\"}", client));
            posts.Add(requests.Sent.Skip(before).Where(r => r.Method == "POST").ToArray());
        }

        await e2e.AssertEventCountAsync(2);
        var stamps = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var info = RequestInfo(Assert.Single(e2e.Events, e => e.Argument("msg") == "call" + i));
            Assert.Equal(InfoKeys, info.Select(p => p.Key));
            Assert.Equal(TestServerUrl, (string?)info["url"]);
            var headers = HeadersOf(info);
            AssertNamesLowerCaseAndSorted(headers);
            Assert.Equal(SentinelValue, (string?)headers["x-agentcat-e2e-sentinel"]);
            Assert.Equal(AuthorizationValue, (string?)headers["authorization"]);
            Assert.Equal(CookieValue, (string?)headers["cookie"]);

            // The POST that carried this call, with every header it had.
            var sent = requests[(string)headers[Stamp]!];
            Assert.Contains(sent, posts[i]);
            Assert.Equal("/", sent.PathAndQuery);
            AssertSameHeaders(sent, headers, TestServerHost);
            stamps.Add(sent.Stamp);
        }

        Assert.Equal(2, stamps.Distinct().Count());
    }

    // A stateful session: parameters.extra carries the transport session and the request, in the TS key order.
    [Theory]
    [MemberData(nameof(LaneData.StatefulHttpLanes), MemberType = typeof(LaneData))]
    public async Task Session_id_and_request_info_together(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults);
        var client = await e2e.ConnectClientAsync(headers: CredentialHeaders());
        var transportSession = client.SessionId;
        Assert.False(string.IsNullOrEmpty(transportSession));

        E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi","context":"session and request"}""", client));

        await e2e.AssertEventCountAsync(1);
        var extra = e2e.Events[0].Extra!;
        Assert.Equal(StatefulExtraKeys, extra.Select(p => p.Key).Where(k => k != "_meta"));
        Assert.Equal("requestInfo", extra.Last().Key);
        Assert.Equal(transportSession, (string?)extra["sessionId"]);
        var headers = HeadersOf(RequestInfo(e2e.Events[0]));
        Assert.Equal(transportSession, (string?)headers["mcp-session-id"]);
        Assert.Equal(SentinelValue, (string?)headers["x-agentcat-e2e-sentinel"]);
        Assert.Equal(AuthorizationValue, (string?)headers["authorization"]);
    }

    // The hooks get the same headers as the event, in a dictionary whose lookup ignores case.
    [Theory]
    [MemberData(nameof(LaneData.HttpLanes), MemberType = typeof(LaneData))]
    public async Task Hook_sees_the_request_headers(string lane)
    {
        var seen = new ConcurrentQueue<IReadOnlyDictionary<string, string>>();
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup { AgentCat = o => o.EventProperties = Recording(seen) });
        var client = await e2e.ConnectClientAsync(headers: CredentialHeaders());

        E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi","context":"hook headers"}""", client));

        await e2e.AssertEventCountAsync(1);
        var hook = Assert.Single(seen);
        Assert.Equal(SentinelValue, hook[SentinelHeader]);
        Assert.Equal(SentinelValue, hook["x-agentcat-e2e-sentinel"]);
        Assert.Equal(AuthorizationValue, hook["Authorization"]);
        Assert.Equal(CookieValue, hook["cookie"]);
        Assert.Equal(TestServerHost, hook["Host"]);
        Assert.All(hook.Keys, k => Assert.Equal(k.ToLowerInvariant(), k));
        Assert.Equal(Lines(HeadersOf(RequestInfo(e2e.Events[0]))), Lines(hook));
    }

    // Review Focus 3. The documented RedactEvent sample removes the credentials: they reach neither the AgentCat API
    // nor any exporter, while the rest of the request still does.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task Redacted_headers_reach_no_wire_or_exporter_body(string lane)
    {
        using var host = new FakeExporterHost();
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, host.Lane(configure: o => o.RedactEvent = RedactEventSample.Hook));
        var client = await e2e.ConnectClientAsync(headers: CredentialHeaders());

        E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi","context":"redacted credentials"}""", client));

        await e2e.AssertEventCountAsync(1);
        var sent = e2e.Events[0];
        var wire = Encoding.UTF8.GetString(Assert.Single(e2e.Api.Requests).Body);
        AssertNoCredentials(wire);
        var headers = HeadersOf(RequestInfo(sent));
        Assert.False(headers.ContainsKey("authorization"));
        Assert.False(headers.ContainsKey("cookie"));
        Assert.Equal(SentinelValue, (string?)headers["x-agentcat-e2e-sentinel"]);

        ExporterE2ETests.AssertOneExportOf(host, sent.Id!, E2EAssert.SessionId(sent.SessionId));
        Assert.All(host.Requests, r => AssertNoCredentials(FakeExporterHost.Text(r)));
        var parameters = FakeExporterHost.Json(Assert.Single(host.PostHog))["batch"]![0]!["properties"]!["parameters"];
        Assert.True(JsonNode.DeepEquals(sent.Parameters, parameters), "PostHog parameters: " + parameters?.ToJsonString());
        Assert.Equal(SentinelValue, (string?)parameters?["extra"]?["requestInfo"]?["headers"]?["x-agentcat-e2e-sentinel"]);
    }

    // A stdio server has no HTTP request: the event has no requestInfo, and AgentCat writes no line about it.
    [Theory]
    [MemberData(nameof(LaneData.StdioLanes), MemberType = typeof(LaneData))]
    public async Task Stdio_events_have_no_request_info(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults);

        E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi","context":"no request"}"""));

        await e2e.AssertEventCountAsync(1);
        var extra = e2e.Events[0].Extra;
        Assert.NotNull(extra);
        Assert.True(extra.ContainsKey("requestId"));
        Assert.False(extra.ContainsKey("requestInfo"));
        Assert.DoesNotContain(StdioServerProcess.ReadLog(e2e.ServerLogPath!), l => l.Contains("request capture", StringComparison.Ordinal));
    }

    // Review Focus 4. With PerSessionExecutionContext, the handlers run on the session's execution context, which holds
    // the request that started the session: no requestInfo, empty hook headers, no exception and no line. That request
    // outlives its response while middleware after it or an OnCompleted callback runs, and IHttpContextAccessor still
    // returns it until ASP.NET Core disposes it; here a middleware holds the initialize POST open during the call.
    [Theory]
    [MemberData(nameof(LaneData.StatefulHttpLanes), MemberType = typeof(LaneData))]
    public async Task Per_session_execution_context_gives_no_request_info(string lane)
    {
        var seen = new ConcurrentQueue<IReadOnlyDictionary<string, string>>();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup
        {
            AgentCat = o => o.EventProperties = Recording(seen),
            Transport = PerSessionExecutionContext,
            App = app => app.Use(async (context, next) =>
            {
                await next(context);
                if (context.Request.Headers.ContainsKey(SentinelHeader) && !context.Request.Headers.ContainsKey(SessionIdHeader))
                {
                    held.TrySetResult();
                    await release.Task;
                }
            }),
        });
        try
        {
            var requests = new ClientRequests();
            var client = await e2e.ConnectClientAsync(headers: CredentialHeaders(), requests: requests);
            await held.Task.WaitAsync(LaneInstance.CallBound, TestContext.Current.CancellationToken);

            E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi","context":"per-session context"}""", client));

            await e2e.AssertEventCountAsync(1);
            var extra = e2e.Events[0].Extra!;
            Assert.Equal(client.SessionId, (string?)extra["sessionId"]);
            Assert.False(extra.ContainsKey("requestInfo"), "requestInfo " + extra["requestInfo"]?.ToJsonString() + " of the requests " + string.Join(", ", requests.Sent));
            Assert.Empty(Assert.Single(seen));
            Assert.DoesNotContain(e2e.Log, l => l.Contains("request capture", StringComparison.Ordinal));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    // PerSessionExecutionContext applies to stateful sessions only: a stateless session runs inside the POST of its
    // call, so the event carries that POST.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task Per_session_execution_context_keeps_stateless_request_info(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup { Transport = PerSessionExecutionContext });
        var requests = new ClientRequests();
        var client = await e2e.ConnectClientAsync(headers: CredentialHeaders(), requests: requests);

        E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi","context":"stateless per-session option"}""", client));

        await e2e.AssertEventCountAsync(1);
        var headers = HeadersOf(RequestInfo(e2e.Events[0]));
        Assert.Same(requests.Sent.Last(r => r.Method == "POST"), requests[(string)headers[Stamp]!]);
        Assert.Equal(AuthorizationValue, (string?)headers["authorization"]);
    }

    // Legacy SSE runs the session's server inside its GET sse request, and POST message only queues the message,
    // so every call of the session captures that GET: its URL and its headers, never a POST's.
    [Theory]
    [MemberData(nameof(LaneData.StatefulHttpLanes), MemberType = typeof(LaneData))]
    public async Task Legacy_sse_carries_the_session_request(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup { Transport = EnableLegacySse });
        var requests = new ClientRequests();
        var client = await e2e.ConnectClientAsync(headers: CredentialHeaders(), requests: requests, legacySse: true);
        var get = Assert.Single(requests.Sent, r => r.Method == "GET");
        Assert.Equal("/sse", get.PathAndQuery);

        for (var i = 0; i < 2; i++)
        {
            E2EAssert.Succeeded(await e2e.CallToolAsync("echo", "{\"msg\":\"sse" + i + "\",\"context\":\"legacy SSE\"}", client));
        }

        await e2e.AssertEventCountAsync(2);
        Assert.Contains(requests.Sent, r => r.Method == "POST" && r.PathAndQuery.StartsWith("/message?", StringComparison.Ordinal));
        Assert.All(e2e.Events, e =>
        {
            var info = RequestInfo(e);
            Assert.Equal(InfoKeys, info.Select(p => p.Key));
            Assert.Equal(TestServerUrl + "sse", (string?)info["url"]);
            var headers = HeadersOf(info);
            Assert.Equal(get.Stamp, (string?)headers[Stamp]);
            Assert.False(headers.ContainsKey("content-type"));
            Assert.Equal(AuthorizationValue, (string?)headers["authorization"]);
            AssertSameHeaders(get, headers, TestServerHost);
        });
    }

    // Kestrel keeps two HTTP/1.1 Cookie lines as two values: they join with "; " as fetch joins them, other repeated
    // headers with ", ", and every name is lower-cased. HttpClient writes one line per header, so the request is raw.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task Two_cookie_lines_join_with_semicolon_space(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup { Kestrel = true });
        const string call = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"msg":"raw","context":"two cookie lines"}}}""";

        var response = await PostRawAsync(
            e2e.BaseUri,
            "Cookie: a=1\r\nCookie: b=2\r\nX-Repeated: one\r\nX-Repeated: two\r\nX-MiXeD-Case: Kept As Sent\r\n",
            call);

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        await e2e.AssertEventCountAsync(1);
        var info = RequestInfo(e2e.Events[0]);
        Assert.Equal(e2e.BaseUri.ToString(), (string?)info["url"]);
        var headers = HeadersOf(info);
        AssertNamesLowerCaseAndSorted(headers);
        Assert.Equal("a=1; b=2", (string?)headers["cookie"]);
        Assert.Equal("one, two", (string?)headers["x-repeated"]);
        Assert.Equal("Kept As Sent", (string?)headers["x-mixed-case"]);
        Assert.Equal(e2e.BaseUri.Authority, (string?)headers["host"]);
    }

    private static string Stamp => ClientRequests.StampHeader.ToLowerInvariant();

    private static Dictionary<string, string> CredentialHeaders() => new()
    {
        [SentinelHeader] = SentinelValue,
        ["Authorization"] = AuthorizationValue,
        ["Cookie"] = CookieValue,
    };

    private static Func<AgentCatToolCall, CancellationToken, ValueTask<JsonObject?>> Recording(ConcurrentQueue<IReadOnlyDictionary<string, string>> seen)
        => (call, _) =>
        {
            seen.Enqueue(call.Headers);
            return new ValueTask<JsonObject?>((JsonObject?)null);
        };

    private static void PerSessionExecutionContext(HttpServerTransportOptions options)
    {
#pragma warning disable MCP9006
        options.PerSessionExecutionContext = true;
#pragma warning restore MCP9006
    }

    // MCP 1.0.0 and 1.1.0 map legacy SSE on every stateful server; later versions need EnableLegacySse, which 1.0.0
    // lacks.
    private static void EnableLegacySse(HttpServerTransportOptions options)
        => typeof(HttpServerTransportOptions).GetProperty("EnableLegacySse")?.SetValue(options, true);

    private static JsonObject RequestInfo(WireEvent e) => Assert.IsType<JsonObject>(e.Extra?["requestInfo"]);

    // parameters.extra.requestInfo.headers, in wire order.
    private static JsonObject HeadersOf(JsonObject info) => Assert.IsType<JsonObject>(info["headers"]);

    private static void AssertNamesLowerCaseAndSorted(JsonObject headers)
    {
        var names = headers.Select(h => h.Key).ToArray();
        Assert.All(names, n => Assert.Equal(n.ToLowerInvariant(), n));
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    // Every header the client sent, plus the Host header that the server adds, and nothing else, in name order.
    private static void AssertSameHeaders(SentRequest sent, JsonObject captured, string host)
    {
        var expected = new Dictionary<string, string>(sent.Headers) { ["host"] = host };
        Assert.Equal(Lines(expected), captured.Select(h => h.Key + ": " + (string?)h.Value));
    }

    // name: value, sorted by name.
    private static string[] Lines(IEnumerable<KeyValuePair<string, string>> headers)
        => headers.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Key + ": " + h.Value).ToArray();

    private static string[] Lines(JsonObject headers)
        => Lines(headers.Select(h => new KeyValuePair<string, string>(h.Key, (string)h.Value!)));

    private static void AssertNoCredentials(string body)
    {
        Assert.DoesNotContain(AuthorizationSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(CookieSecret, body, StringComparison.Ordinal);
    }

    // One HTTP/1.1 POST written by hand to the server's root, with extra header lines as given. Returns the whole
    // response, which the server ends by closing the connection.
    private static async Task<string> PostRawAsync(Uri server, string headerLines, string body)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(LaneInstance.CallBound);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port, cts.Token);
        var stream = tcp.GetStream();
        var content = Encoding.UTF8.GetBytes(body);
        var head = "POST / HTTP/1.1\r\n"
            + "Host: " + server.Authority + "\r\n"
            + "Content-Type: application/json\r\n"
            + "Accept: application/json, text/event-stream\r\n"
            + headerLines
            + "Content-Length: " + content.Length + "\r\n"
            + "Connection: close\r\n"
            + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cts.Token);
        await stream.WriteAsync(content, cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cts.Token);
    }
}
