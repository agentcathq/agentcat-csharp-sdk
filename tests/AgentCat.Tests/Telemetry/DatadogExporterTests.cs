#pragma warning disable CA1861
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Telemetry;

public sealed class DatadogExporterTests : IDisposable
{
    private const string SessionId = "ses_2yXlShd4ODDcHLYliGW9VgH3m1R";
    private const string EventId = "evt_2yXlTL6bElM5Whm0HjT0Iz8LL0M";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly RecordingLog _log = new();
    private readonly RecordingHttpHandler _handler = new();
    private readonly List<AgentCatRuntime> _runtimes = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The lines after the exporter's own "Initialized telemetry exporter" line.
    private string[] ExportLines => _log.Lines.Where(l => !l.StartsWith("Initialized telemetry exporter", StringComparison.Ordinal)).ToArray();

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

    private static ITelemetryExporter Build(AgentCatRuntime runtime, DatadogExporter config, string name = "dd")
        => ExporterFactory.Create(name, config, runtime) ?? throw new InvalidOperationException("The Datadog exporter was not built.");

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

    // The two (URL, HEADERS, BODY) triples that follow "=== {section}" in golden-output.txt: logs, then metrics.
    private static (string Url, JsonObject Headers, string Body)[] Golden(string section)
    {
        var lines = FixtureLines("golden-output.txt");
        var start = Array.IndexOf(lines, "=== " + section);
        Assert.True(start >= 0, "Missing fixture section " + section);
        var result = new (string, JsonObject, string)[2];
        for (var i = 0; i < 2; i++)
        {
            var at = start + 1 + (i * 3);
            Assert.StartsWith("URL POST ", lines[at], StringComparison.Ordinal);
            Assert.StartsWith("HEADERS ", lines[at + 1], StringComparison.Ordinal);
            Assert.StartsWith("BODY ", lines[at + 2], StringComparison.Ordinal);
            result[i] = (
                lines[at].Substring("URL POST ".Length),
                (JsonObject)JsonNode.Parse(lines[at + 1].Substring("HEADERS ".Length))!,
                lines[at + 2].Substring("BODY ".Length));
        }

        return result;
    }

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

    private RecordedHttpRequest Request(string url)
        => Assert.Single(_handler.Requests, r => r.Uri.OriginalString == url);

    private async Task AssertGoldenAsync(string section, DatadogExporter config, AgentCatEvent e)
    {
        var golden = Golden(section);
        var runtime = Runtime();

        Build(runtime, config).Export(e);
        await DrainAsync(runtime);

        Assert.Equal(2, _handler.Count);
        foreach (var expected in golden)
        {
            var request = Request(expected.Url);
            Assert.Equal(HttpMethod.Post, request.Method);
            AssertHeaders(request, expected.Headers);
            Assert.Equal(expected.Body, Encoding.UTF8.GetString(request.Body));
        }
    }

    [Fact]
    public Task The_golden_ok_requests_match_the_ts_exporter_byte_for_byte()
        => AssertGoldenAsync("datadog ok", new DatadogExporter("ddkey", "https://datadoghq.eu/", "svc") { Env = "prod" }, GoldenEvent());

    [Fact]
    public Task The_golden_error_requests_match_the_ts_exporter_byte_for_byte()
        => AssertGoldenAsync("datadog err", new DatadogExporter("ddkey", "datadoghq.com", "svc"), GoldenEvent(isError: true));

    [Fact]
    public async Task The_golden2_sessionless_requests_match_the_ts_exporter_byte_for_byte()
    {
        var lines = FixtureLines("golden2-output.txt");
        var section = Array.IndexOf(lines, "=== dd");
        Assert.Equal("https://http-intake.logs.us3.datadoghq.com/api/v2/logs ", lines[section + 1]);
        Assert.Equal("https://api.us3.datadoghq.com/api/v1/series ", lines[section + 3]);
        var random = new FixedRandomSource("0000000000000000dfffd8f97263aab2");
        var runtime = Runtime(random: random);

        Build(runtime, new DatadogExporter("ddkey", "us3.datadoghq.com", "svc")).Export(SessionlessEvent());
        await DrainAsync(runtime);

        Assert.Equal(lines[section + 2].Substring(1), Encoding.UTF8.GetString(Request("https://http-intake.logs.us3.datadoghq.com/api/v2/logs").Body));
        Assert.Equal(lines[section + 4].Substring(1), Encoding.UTF8.GetString(Request("https://api.us3.datadoghq.com/api/v1/series").Body));
        Assert.Equal(1, random.Draws);
    }

    [Theory]
    [InlineData("datadoghq.com", "datadoghq.com")]
    [InlineData("https://datadoghq.eu", "datadoghq.eu")]
    [InlineData("http://datadoghq.eu/", "datadoghq.eu")]
    [InlineData("datadoghq.eu//", "datadoghq.eu/")]
    [InlineData("HTTPS://x.com", "HTTPS://x.com")]
    public async Task The_site_loses_one_scheme_and_one_trailing_slash(string site, string normalized)
    {
        var runtime = Runtime();

        Build(runtime, new DatadogExporter("k", site, "svc")).Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal(
            new[] { "https://api." + normalized + "/api/v1/series", "https://http-intake.logs." + normalized + "/api/v2/logs" },
            _handler.Requests.Select(r => r.Uri.OriginalString).OrderBy(u => u, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_log_lines_come_before_the_requests_and_each_request_logs_its_status()
    {
        var runtime = Runtime();
        var exporter = Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc"));
        var e = GoldenEvent(isError: true);

        exporter.Export(e);

        Assert.Equal(
            new[]
            {
                "DatadogExporter: Sending event immediately to Datadog",
                "DatadogExporter: Metrics URL: https://api.datadoghq.com/api/v1/series",
                "DatadogExporter: Sending 3 metric series",
            },
            ExportLines.Take(3));
        await DrainAsync(runtime);

        var rest = ExportLines.Skip(3).OrderBy(l => l, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Datadog logs success - Status: 200", "Datadog metrics success - Status: 200" }, rest);
    }

    [Fact]
    public async Task A_series_is_added_for_a_duration_and_for_an_error()
    {
        var runtime = Runtime();
        var exporter = Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc"));
        var plain = SessionlessEvent();
        var timed = SessionlessEvent();
        timed.Duration = 7;
        var failed = SessionlessEvent();
        failed.IsError = true;

        exporter.Export(plain);
        exporter.Export(timed);
        exporter.Export(failed);
        await DrainAsync(runtime);

        var counts = _handler.Requests
            .Where(r => r.Path == "/api/v1/series")
            .Select(r => JsonNode.Parse(r.Body)!["series"]!.AsArray().Select(s => (string)s!["metric"]!).ToArray())
            .OrderBy(m => m.Length)
            .ToArray();
        Assert.Equal(new[] { "mcp.events.count" }, counts[0]);
        Assert.Equal(new[] { "mcp.events.count", "mcp.event.duration" }, counts[1]);
        Assert.Equal(new[] { "mcp.events.count", "mcp.errors.count" }, counts[2]);
        Assert.Contains("DatadogExporter: Sending 2 metric series", ExportLines);
    }

    [Fact]
    public async Task A_log_failure_is_logged_on_its_own_while_the_metrics_are_sent()
    {
        _handler.Respond = (request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.AbsolutePath == "/api/v2/logs" ? HttpStatusCode.Forbidden : HttpStatusCode.Accepted));
        var runtime = Runtime();

        Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc")).Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal(2, _handler.Count);
        Assert.Equal(
            new[] { "Datadog logs failed - Status: 403", "Datadog metrics success - Status: 202" },
            ExportLines.Skip(3).OrderBy(l => l, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_metrics_failure_is_logged_on_its_own_while_the_log_is_sent()
    {
        _handler.Respond = (request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.AbsolutePath == "/api/v1/series" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        var runtime = Runtime();

        Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc")).Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal(
            new[] { "Datadog logs success - Status: 200", "Datadog metrics failed - Status: 500" },
            ExportLines.Skip(3).OrderBy(l => l, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_network_error_is_logged_per_request_with_the_exception_type_and_message()
    {
        _handler.Respond = (request, _) => request.RequestUri!.AbsolutePath == "/api/v2/logs"
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        var runtime = Runtime();

        Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc")).Export(GoldenEvent());
        await DrainAsync(runtime);

        Assert.Equal(
            new[] { "Datadog logs network error: HttpRequestException: connection refused", "Datadog metrics success - Status: 200" },
            ExportLines.Skip(3).OrderBy(l => l, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_request_that_gets_no_answer_logs_the_timeout_for_each_request()
    {
        var clock = new ManualClock();
        clock.HoldDelays();
        var handler = RecordingHttpHandler.Silent();
        var runtime = Runtime(clock: clock, handler: handler);

        Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc")).Export(GoldenEvent());
        while (handler.Count < 2)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(3, ExportLines.Length);

        clock.ReleaseDelays();
        await DrainAsync(runtime);

        Assert.Equal(
            new[]
            {
                "Datadog logs network error: Request timed out after 10000 ms",
                "Datadog metrics network error: Request timed out after 10000 ms",
            },
            ExportLines.Skip(3).OrderBy(l => l, StringComparer.Ordinal));
    }

    [Fact]
    public void A_null_tag_value_fails_the_export_before_any_request_with_the_ts_message()
    {
        var runtime = Runtime();
        var e = GoldenEvent();
        e.Tags = new Dictionary<string, string> { ["a"] = "1", ["b"] = null! };

        var error = Assert.Throws<JsError>(() => Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc")).Export(e));

        Assert.Equal("value.replace is not a function", error.Message);
        Assert.Equal(0, _handler.Count);
    }

    [Fact]
    public async Task The_manager_logs_a_null_tag_value_under_the_exporter_name_and_sends_nothing()
    {
        var runtime = Runtime();
        var manager = new TelemetryManager(
            new[] { new KeyValuePair<string, ITelemetryExporter>("dd", Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc"))) },
            _log);
        var e = GoldenEvent();
        e.Tags = new Dictionary<string, string> { ["b"] = null! };

        await manager.ExportAsync(e, Ct);
        await DrainAsync(runtime);

        Assert.Equal(0, _handler.Count);
        Assert.Contains("Telemetry export failed for dd: value.replace is not a function", ExportLines);
    }

    [Fact]
    public async Task Tag_keys_use_the_ECMAScript_whitespace_set()
    {
        var runtime = Runtime();
        var e = GoldenEvent();
        e.Tags = new Dictionary<string, string>
        {
            ["A\u0085B"] = "1",
            ["A\uFEFFB"] = "2",
            ["Mixed \t:, \u00A0Key"] = "3",
            ["\u2028Lead"] = "4",
            ["\u00C0B"] = "5",
        };

        Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc")).Export(e);
        await DrainAsync(runtime);

        var body = JsonNode.Parse(Request("https://http-intake.logs.datadoghq.com/api/v2/logs").Body)!;
        var tags = ((string)body[0]!["ddtags"]!).Split(',');
        Assert.Equal(
            new[] { "agentcat.a\u0085b:1", "agentcat.a_b:2", "agentcat.mixed_key:3", "agentcat._lead:4", "agentcat.\u00E0b:5" },
            tags.Skip(tags.Length - 5));
    }

    [Fact]
    public async Task A_string_error_is_sent_whole_and_a_falsy_error_adds_no_root_error()
    {
        var runtime = Runtime();
        var exporter = Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc"));
        var text = GoldenEvent(isError: true);
        text.Error = JsonValue.Create("plain failure");
        var empty = GoldenEvent(isError: true);
        empty.Error = JsonValue.Create("");

        exporter.Export(text);
        exporter.Export(empty);
        await DrainAsync(runtime);

        var logs = _handler.Requests.Where(r => r.Path == "/api/v2/logs").Select(r => JsonNode.Parse(r.Body)![0]!).ToArray();
        var withText = logs.Single(l => l["error"] is not null);
        Assert.Equal("plain failure", (string)withText["error"]!["message"]!);
        Assert.Equal("plain failure", (string)withText["mcp"]!["error"]!);
        var withEmpty = logs.Single(l => l["error"] is null);
        Assert.Equal("", (string)withEmpty["mcp"]!["error"]!);
    }

    [Fact]
    public async Task The_defaults_fill_the_message_and_the_event_is_left_as_it_was()
    {
        var runtime = Runtime();
        var e = new AgentCatEvent { Id = "evt_1", SessionId = "ses_1", Timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 5, 123, TimeSpan.Zero) };
        var golden = GoldenEvent();
        var before = golden.Clone();
        var exporter = Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc"));

        exporter.Export(e);
        exporter.Export(golden);
        exporter.Export(golden);
        await DrainAsync(runtime);

        var logs = _handler.Requests.Where(r => r.Path == "/api/v2/logs").ToArray();
        Assert.Contains(logs, r => (string)JsonNode.Parse(r.Body)![0]!["message"]! == "unknown - unknown");
        var same = logs.Where(r => Encoding.UTF8.GetString(r.Body).Contains("get_weather", StringComparison.Ordinal)).ToArray();
        Assert.Equal(same[0].Body, same[1].Body);
        Assert.Null(golden.Properties!.Parent);
        Assert.Equal(before.Properties!.ToJsonString(), golden.Properties.ToJsonString());
        Assert.Equal(before.Tags, golden.Tags);
    }

    [Fact]
    public async Task Concurrent_exports_each_send_their_own_log_and_metrics()
    {
        var runtime = Runtime();
        var exporter = Build(runtime, new DatadogExporter("k", "datadoghq.com", "svc"));

        Parallel.For(0, 40, i =>
        {
            var e = GoldenEvent();
            e.Id = "evt_" + i;
            exporter.Export(e);
        });
        await DrainAsync(runtime);

        Assert.Equal(80, _handler.Count);
        var spanIds = _handler.Requests
            .Where(r => r.Path == "/api/v2/logs")
            .Select(r => (string)JsonNode.Parse(r.Body)![0]!["dd"]!["span_id"]!)
            .Distinct()
            .Count();
        Assert.Equal(40, spanIds);
    }
}
