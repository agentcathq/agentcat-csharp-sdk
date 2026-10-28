#pragma warning disable CA1861
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Telemetry;

public sealed class OtlpExporterTests : IDisposable
{
    private const string SessionId = "ses_2yXlShd4ODDcHLYliGW9VgH3m1R";
    private const string EventId = "evt_2yXlTL6bElM5Whm0HjT0Iz8LL0M";
    private const string SessionlessTraceId = "5fbf74784385eeb15609b0fed72cbb30";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly RecordingLog _log = new();
    private readonly RecordingHttpHandler _handler = new();
    private readonly List<AgentCatRuntime> _runtimes = new();

    // The lines after the exporter's own "Initialized telemetry exporter" line.
    private string[] ExportLines => Lines(_log);

    private static string[] Lines(RecordingLog log)
        => log.Lines.Where(l => !l.StartsWith("Initialized telemetry exporter", StringComparison.Ordinal)).ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var runtime in _runtimes)
        {
            runtime.Pipeline.Dispose();
        }
    }

    private AgentCatRuntime Runtime(IPipelineClock? clock = null, IRandomSource? random = null, HttpMessageHandler? handler = null)
    {
        var runtime = new AgentCatRuntime(
            _log,
            new RecordingSender(),
            clock: clock,
            exportHandler: handler ?? _handler,
            random: random,
            exporterBuilder: ExporterFactory.Build);
        _runtimes.Add(runtime);
        return runtime;
    }

    private static ITelemetryExporter Build(AgentCatRuntime runtime, OtlpExporter config, string name = "otlp")
        => ExporterFactory.Create(name, config, runtime) ?? throw new InvalidOperationException("The OTLP exporter was not built.");

    private static async Task DrainAsync(AgentCatRuntime runtime)
        => Assert.True(await runtime.Exports.WaitIdleAsync(Bound, Ct), "The exporter requests did not finish.");

    private static AgentCatEvent GoldenEvent(bool isError = false) => new()
    {
        Id = EventId,
        SessionId = SessionId,
        ProjectId = "proj_golden",
        EventType = "mcp:tools/call",
        Timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 5, 123, TimeSpan.Zero),
        Duration = 150,
        AgentCatVersion = "2.1.1",
        ServerName = "weather-server",
        ServerVersion = "1.0.0",
        ClientName = "claude-desktop",
        ClientVersion = "2.0.0",
        IdentifyActorGivenId = "user_abc",
        IdentifyActorName = "Alice",
        IdentifyActorData = new JsonObject { ["email"] = "alice@example.com" },
        ResourceName = "get_weather",
        Parameters = new JsonObject { ["city"] = "London" },
        Response = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "15C" }) },
        UserIntent = "Checking weather",
        IsError = isError,
        Error = isError
            ? new JsonObject { ["message"] = "boom", ["type"] = "TypeError", ["stack"] = "TypeError: boom\n    at x (/a.js:1:1)" }
            : null,
        Tags = new Dictionary<string, string> { ["env"] = "prod", ["Bad Key:x"] = "a,b" },
        Properties = new JsonObject { ["device"] = "desktop" },
    };

    private static AgentCatEvent SessionlessEvent() => new()
    {
        Id = EventId,
        SessionId = "",
        ProjectId = "",
        EventType = "mcp:tools/list",
        Timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 5, 123, TimeSpan.Zero),
        Duration = 0,
        ServerName = "s",
    };

    private static string[] FixtureLines(string name)
        => Fixtures.Text("exporters/" + name).Replace("\r\n", "\n").Split('\n');

    // The URL, HEADERS and BODY lines that follow "=== {section}" in golden-output.txt.
    private static (string Url, JsonObject Headers, string Body) Golden(string section)
    {
        var lines = FixtureLines("golden-output.txt");
        var start = Array.IndexOf(lines, "=== " + section);
        Assert.True(start >= 0, "Missing fixture section " + section);
        Assert.StartsWith("URL POST ", lines[start + 1], StringComparison.Ordinal);
        Assert.StartsWith("HEADERS ", lines[start + 2], StringComparison.Ordinal);
        Assert.StartsWith("BODY ", lines[start + 3], StringComparison.Ordinal);
        return (
            lines[start + 1].Substring("URL POST ".Length),
            (JsonObject)JsonNode.Parse(lines[start + 2].Substring("HEADERS ".Length))!,
            lines[start + 3].Substring("BODY ".Length));
    }

    // Content-Type is a content header in .NET, so the headers compare as a set: every fixture header is on the request
    // with its value, and the only others are the ones .NET and the SDK add.
    private static void AssertHeaders(RecordedHttpRequest request, JsonObject expected)
    {
        foreach (var header in expected)
        {
            Assert.Equal((string)header.Value!, request.Header(header.Key));
        }

        var extra = request.Headers
            .Select(h => h.Key)
            .Where(name => !expected.ContainsKey(name))
            .Select(name => name.ToLowerInvariant())
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(new[] { "content-length", "user-agent" }, extra);
        Assert.StartsWith("agentcat-csharp/", request.Header("User-Agent"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_waiting_OTLP_request_does_not_keep_its_event_alive()
    {
        var handler = RecordingHttpHandler.Silent();
        var runtime = Runtime(handler: handler);
        var exporter = Build(runtime, new OtlpExporter("http://localhost:4318/"));

        var reference = ExportOneEvent(exporter);
        await Task.Yield();
        var deadline = DateTime.UtcNow + Bound;
        while (handler.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(1, handler.Count);
        var live = await Retention.LiveAfterCollectingAsync(new[] { ("event", reference) });

        Assert.Empty(live);
        GC.KeepAlive(exporter);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference ExportOneEvent(ITelemetryExporter exporter)
    {
        var e = GoldenEvent();
        exporter.Export(e);
        return new WeakReference(e);
    }

    [Fact]
    public async Task The_golden_ok_request_matches_the_ts_exporter_byte_for_byte()
    {
        var golden = Golden("otlp ok");
        var runtime = Runtime();
        var exporter = Build(runtime, new OtlpExporter("http://localhost:4318/")
        {
            Headers = new Dictionary<string, string> { ["x-api-key"] = "k" },
        });

        exporter.Export(GoldenEvent());
        await DrainAsync(runtime);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(golden.Url, request.Uri.OriginalString);
        AssertHeaders(request, golden.Headers);
        Assert.Equal(golden.Body, Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public async Task The_golden_error_request_matches_the_ts_exporter_byte_for_byte()
    {
        var golden = Golden("otlp err");
        var runtime = Runtime();
        var exporter = Build(runtime, new OtlpExporter("http://localhost:4318/v1/traces"));

        exporter.Export(GoldenEvent(isError: true));
        await DrainAsync(runtime);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(golden.Url, request.Uri.OriginalString);
        AssertHeaders(request, golden.Headers);
        Assert.Equal(golden.Body, Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public async Task A_sessionless_event_takes_its_trace_id_from_the_runtime_random_source()
    {
        var lines = FixtureLines("golden2-output.txt");
        var section = Array.IndexOf(lines, "=== otlp");
        Assert.Equal("http://c:4318/v1/traces ", lines[section + 1]);
        var body = lines[section + 2];
        Assert.StartsWith(" ", body, StringComparison.Ordinal);
        var random = new FixedRandom(HexBytes(SessionlessTraceId));
        var runtime = Runtime(random: random);
        var exporter = Build(runtime, new OtlpExporter("http://c:4318"));

        exporter.Export(SessionlessEvent());
        await DrainAsync(runtime);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("http://c:4318/v1/traces", request.Uri.OriginalString);
        Assert.Equal(body.Substring(1), Encoding.UTF8.GetString(request.Body));
        Assert.Equal(16, random.BytesRequested);
    }

    [Fact]
    public async Task Each_sessionless_export_asks_for_new_random_bytes()
    {
        var runtime = Runtime();
        var exporter = Build(runtime, new OtlpExporter("http://c:4318"));

        exporter.Export(SessionlessEvent());
        exporter.Export(SessionlessEvent());
        await DrainAsync(runtime);

        var traceIds = _handler.Requests
            .Select(r => (string)JsonNode.Parse(r.Body)!["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!["traceId"]!)
            .ToArray();
        Assert.Equal(2, traceIds.Length);
        Assert.NotEqual(traceIds[0], traceIds[1]);
        Assert.All(traceIds, id => Assert.Matches("^[0-9a-f]{32}$", id));
    }

    [Theory]
    [InlineData("http://h:4318", "http://h:4318/v1/traces")]
    [InlineData("http://h:4318/", "http://h:4318/v1/traces")]
    [InlineData("http://h:4318///", "http://h:4318/v1/traces")]
    [InlineData("http://h:4318/v1/traces", "http://h:4318/v1/traces")]
    [InlineData("http://h:4318/v1/traces/", "http://h:4318/v1/traces")]
    [InlineData("http://h/otlp", "http://h/otlp/v1/traces")]
    [InlineData("http://h/x/v1/traces", "http://h/x/v1/traces")]
    public async Task The_endpoint_loses_its_trailing_slashes_and_gains_the_traces_path(string endpoint, string expected)
    {
        var runtime = Runtime();

        Build(runtime, new OtlpExporter(endpoint)).Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal(expected, Assert.Single(_handler.Requests).Uri.OriginalString);
    }

    [Fact]
    public async Task A_query_string_is_not_handled()
    {
        var runtime = Runtime();

        Build(runtime, new OtlpExporter("http://h?x=1")).Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal("/v1/traces", Assert.Single(_handler.Requests).Uri.Query.Substring("?x=1".Length));
    }

    [Fact]
    public async Task Custom_headers_can_replace_the_user_agent()
    {
        var runtime = Runtime();
        var exporter = Build(runtime, new OtlpExporter("http://h")
        {
            Headers = new Dictionary<string, string> { ["User-Agent"] = "my-agent/1", ["Authorization"] = "Bearer t" },
        });

        exporter.Export(GoldenEvent());
        await DrainAsync(runtime);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("my-agent/1", request.Header("User-Agent"));
        Assert.Equal("Bearer t", request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Content-Type"));
    }

    [Fact]
    public async Task A_content_type_header_replaces_the_default()
    {
        var runtime = Runtime();
        var exporter = Build(runtime, new OtlpExporter("http://h")
        {
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json; charset=utf-8" },
        });

        exporter.Export(GoldenEvent());
        await DrainAsync(runtime);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("application/json; charset=utf-8", request.Header("Content-Type"));
        Assert.Single(request.Headers, h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_null_tag_value_is_left_out()
    {
        var runtime = Runtime();
        var e = GoldenEvent();
        e.Tags = new Dictionary<string, string> { ["a"] = "1", ["b"] = null!, ["c"] = "", ["d"] = "4" };

        Build(runtime, new OtlpExporter("http://h")).Export(e);
        await DrainAsync(runtime);

        var attributes = JsonNode.Parse(Assert.Single(_handler.Requests).Body)!["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!["attributes"]!
            .AsArray()
            .Select(a => (string)a!["key"]!)
            .Where(k => k.StartsWith("agentcat.tag.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(new[] { "agentcat.tag.a", "agentcat.tag.d" }, attributes);
    }

    [Fact]
    public async Task Empty_properties_are_sent_as_an_empty_object_string_and_missing_ones_are_left_out()
    {
        var runtime = Runtime();
        var exporter = Build(runtime, new OtlpExporter("http://h"));
        var empty = GoldenEvent();
        empty.Properties = new JsonObject();
        var none = GoldenEvent();
        none.Properties = null;

        exporter.Export(empty);
        exporter.Export(none);
        await DrainAsync(runtime);

        var values = _handler.Requests
            .Select(r => JsonNode.Parse(r.Body)!["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!["attributes"]!.AsArray()
                .FirstOrDefault(a => (string)a!["key"]! == "agentcat.properties")?["value"]!["stringValue"]?.GetValue<string>())
            .ToArray();
        Assert.Equal(new string?[] { "{}", null }, values.OrderByDescending(v => v is not null));
    }

    [Fact]
    public async Task The_defaults_fill_the_values_a_span_lacks()
    {
        var runtime = Runtime();
        var e = new AgentCatEvent { Id = "evt_1", SessionId = "ses_1", Timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 5, 123, TimeSpan.Zero) };

        Build(runtime, new OtlpExporter("http://h")).Export(e);
        await DrainAsync(runtime);

        var root = JsonNode.Parse(Assert.Single(_handler.Requests).Body)!["resourceSpans"]![0]!;
        var resource = root["resource"]!["attributes"]!.AsArray();
        Assert.Equal("mcp-server", (string)resource[0]!["value"]!["stringValue"]!);
        Assert.Equal("unknown", (string)resource[1]!["value"]!["stringValue"]!);
        var scope = root["scopeSpans"]![0]!;
        Assert.Equal("unknown", (string)scope["scope"]!["version"]!);
        var span = scope["spans"]![0]!;
        Assert.Equal("mcp.event", (string)span["name"]!);
        Assert.Equal("1749988805123000000", (string)span["endTimeUnixNano"]!);
        Assert.Equal(1, (int)span["status"]!["code"]!);
        Assert.Equal(new[] { "source", "mcp.session_id" }, span["attributes"]!.AsArray().Select(a => (string)a!["key"]!));
    }

    [Fact]
    public async Task The_event_is_left_as_it_was_and_no_node_is_attached()
    {
        var runtime = Runtime();
        var e = GoldenEvent();
        var before = e.Clone();
        var exporter = Build(runtime, new OtlpExporter("http://h"));

        exporter.Export(e);
        exporter.Export(e);
        await DrainAsync(runtime);

        Assert.Equal(2, _handler.Count);
        Assert.Equal(_handler.Requests[0].Body, _handler.Requests[1].Body);
        Assert.Null(e.Properties!.Parent);
        Assert.Equal(before.Properties!.ToJsonString(), e.Properties.ToJsonString());
        Assert.Equal(before.Tags, e.Tags);
    }

    [Fact]
    public async Task A_2xx_answer_logs_the_success_line_and_ignores_the_body()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"partialSuccess\":{}}") });
        var runtime = Runtime();

        Build(runtime, new OtlpExporter("http://h")).Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal(new[] { "Successfully exported event to OTLP: " + EventId }, ExportLines);
        Assert.Contains(_log.Written, w => w.Line.StartsWith("Successfully exported", StringComparison.Ordinal) && w.Severity == LogSeverity.Information);
    }

    [Fact]
    public async Task A_404_logs_the_manager_line_with_the_status_text()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var runtime = Runtime();

        Build(runtime, new OtlpExporter("http://h"), "collector").Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal(
            new[] { "Telemetry export failed for collector: OTLP export error: Error: OTLP export failed: 404 Not Found" },
            ExportLines);
    }

    [Fact]
    public async Task A_status_without_a_reason_phrase_ends_the_line_with_a_space()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "" });
        var runtime = Runtime();

        Build(runtime, new OtlpExporter("http://h")).Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal(
            new[] { "Telemetry export failed for otlp: OTLP export error: Error: OTLP export failed: 404 " },
            ExportLines);
    }

    [Fact]
    public async Task A_301_that_is_not_followed_is_a_failure()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently));
        var runtime = Runtime();

        Build(runtime, new OtlpExporter("http://h")).Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal(
            new[] { "Telemetry export failed for otlp: OTLP export error: Error: OTLP export failed: 301 Moved Permanently" },
            ExportLines);
    }

    [Fact]
    public async Task A_network_error_logs_the_exception_type_and_message()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("connection refused");
        var runtime = Runtime();

        Build(runtime, new OtlpExporter("http://h")).Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal(
            new[] { "Telemetry export failed for otlp: OTLP export error: HttpRequestException: connection refused" },
            ExportLines);
    }

    [Fact]
    public async Task A_request_that_gets_no_answer_logs_the_timeout()
    {
        var clock = new ManualClock();
        clock.HoldDelays();
        var handler = RecordingHttpHandler.Silent();
        var runtime = Runtime(clock: clock, handler: handler);

        Build(runtime, new OtlpExporter("http://h")).Export(GoldenEvent());
        await FakeAgentCatApi.WithinAsync(WaitForRequestAsync(handler), Bound);
        Assert.Empty(ExportLines);

        clock.ReleaseDelays();
        await DrainAsync(runtime);

        Assert.Equal(
            new[] { "Telemetry export failed for otlp: OTLP export error: Request timed out after 10000 ms" },
            ExportLines);
    }

    private static async Task WaitForRequestAsync(RecordingHttpHandler handler)
    {
        while (handler.Count == 0)
        {
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public void A_body_that_cannot_be_built_throws_the_ts_error_for_the_manager_to_log()
    {
        var runtime = Runtime();
        var e = GoldenEvent();
        JsonNode deep = new JsonObject();
        for (var i = 0; i < 1100; i++)
        {
            deep = new JsonObject { ["a"] = deep };
        }

        e.Properties = (JsonObject)deep;

        var error = Assert.Throws<JsError>(() => Build(runtime, new OtlpExporter("http://h")).Export(e));

        Assert.StartsWith("OTLP export error: ", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, _handler.Count);
    }

    [Fact]
    public async Task Concurrent_exports_each_send_their_own_span()
    {
        var runtime = Runtime();
        var exporter = Build(runtime, new OtlpExporter("http://h"));

        Parallel.For(0, 40, i =>
        {
            var e = GoldenEvent();
            e.Id = "evt_" + i;
            exporter.Export(e);
        });
        await DrainAsync(runtime);

        var spanIds = _handler.Requests
            .Select(r => (string)JsonNode.Parse(r.Body)!["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!["spanId"]!)
            .Distinct()
            .Count();
        Assert.Equal(40, spanIds);
    }

#if NET
    // A status line with no reason phrase, on a real socket.
    [Fact]
    public async Task A_status_line_without_a_reason_phrase_on_the_wire_gives_an_empty_status_text()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            var stream = client.GetStream();
            var buffer = new byte[8192];
            _ = await stream.ReadAsync(buffer, Ct);
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 404\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, Ct);
        }, Ct);
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender(), exporterBuilder: ExporterFactory.Build);
        _runtimes.Add(runtime);

        Build(runtime, new OtlpExporter("http://127.0.0.1:" + port)).Export(GoldenEvent());
        Assert.True(await runtime.Exports.WaitIdleAsync(Bound, Ct));
        await served;

        Assert.Equal(
            new[] { "Telemetry export failed for otlp: OTLP export error: Error: OTLP export failed: 404 " },
            Lines(log));
    }

#endif

    private static byte[] HexBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    private sealed class FixedRandom : IRandomSource
    {
        private readonly byte[] _bytes;

        internal FixedRandom(byte[] bytes) => _bytes = bytes;

        internal int BytesRequested { get; private set; }

        public void Fill(Span<byte> buffer)
        {
            BytesRequested += buffer.Length;
            _bytes.AsSpan(0, buffer.Length).CopyTo(buffer);
        }
    }
}
