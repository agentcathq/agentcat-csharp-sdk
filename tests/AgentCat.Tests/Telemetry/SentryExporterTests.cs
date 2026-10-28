#pragma warning disable CA1861
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry;
using AgentCat.Internal.Telemetry.Sentry;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Telemetry;

public sealed class SentryExporterTests : IDisposable
{
    private const string SessionId = "ses_2yXlShd4ODDcHLYliGW9VgH3m1R";
    private const string EventId = "evt_2yXlTL6bElM5Whm0HjT0Iz8LL0M";

    // TraceIds.GetSpanId(EventId): the first half of every Sentry event_id of the fixture event.
    private const string Span = "38fbea6b36e16f37";

    private const string Dsn = "https://abc@o1.ingest.sentry.io/42";
    private const string Endpoint = "https://o1.ingest.sentry.io/api/42/envelope/";
    private const string InitLine = "SentryExporter: Initialized with endpoint " + Endpoint;
    private const string SendingLog = "SentryExporter: Sending log for event " + EventId + " to Sentry";

    private const string LogItem = "{\"type\":\"log\",\"item_count\":1,\"content_type\":\"application/vnd.sentry.items.log+json\"}";
    private const string TransactionItem = "{\"type\":\"transaction\"}";
    private const string EventItem = "{\"type\":\"event\",\"content_type\":\"application/json\"}";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly RecordingLog _log = new();
    private readonly RecordingHttpHandler _handler = new();
    private readonly List<AgentCatRuntime> _runtimes = new();

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

    private static ITelemetryExporter Build(AgentCatRuntime runtime, SentryExporter config, string name = "sentry")
        => ExporterFactory.Create(name, config, runtime) ?? throw new InvalidOperationException("The Sentry exporter was not built.");

    private ITelemetryExporter Exporter(bool tracing = false, IPipelineClock? clock = null, IRandomSource? random = null, HttpMessageHandler? handler = null)
        => Build(Runtime(clock, random, handler), new SentryExporter(Dsn) { EnableTracing = tracing });

    private async Task DrainAsync()
    {
        foreach (var runtime in _runtimes)
        {
            Assert.True(await runtime.Exports.WaitIdleAsync(Bound, Ct), "The exporter requests did not finish.");
        }
    }

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

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private static string[] FixtureLines(string name)
        => Fixtures.Text("exporters/" + name).Replace("\r\n", "\n").Split('\n');

    // The (URL, HEADERS, BODY) requests of "=== {section}" in golden-output.txt. A body runs over several lines, up to the
    // next request, section or result line; the log envelope's trailing newline shows as the empty line after it.
    private static List<(string Url, JsonObject Headers, string Body)> Golden(string section)
    {
        var lines = FixtureLines("golden-output.txt");
        var at = Array.IndexOf(lines, "=== " + section);
        Assert.True(at >= 0, "Missing fixture section " + section);
        at++;
        var requests = new List<(string, JsonObject, string)>();
        while (at < lines.Length && lines[at].StartsWith("URL POST ", StringComparison.Ordinal))
        {
            var url = lines[at].Substring("URL POST ".Length);
            Assert.StartsWith("HEADERS ", lines[at + 1], StringComparison.Ordinal);
            var headers = (JsonObject)JsonNode.Parse(lines[at + 1].Substring("HEADERS ".Length))!;
            Assert.StartsWith("BODY ", lines[at + 2], StringComparison.Ordinal);
            var body = new List<string> { lines[at + 2].Substring("BODY ".Length) };
            at += 3;
            while (at < lines.Length
                && !lines[at].StartsWith("URL POST ", StringComparison.Ordinal)
                && !lines[at].StartsWith("=== ", StringComparison.Ordinal)
                && !lines[at].StartsWith("sentry ", StringComparison.Ordinal))
            {
                body.Add(lines[at]);
                at++;
            }

            requests.Add((url, headers, string.Join("\n", body)));
        }

        return requests;
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

    private static string Text(RecordedHttpRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string[] EnvelopeLines(RecordedHttpRequest request) => Text(request).Split('\n');

    private static string ItemHeader(RecordedHttpRequest request) => EnvelopeLines(request)[1];

    private static JsonNode Item(RecordedHttpRequest request) => JsonNode.Parse(EnvelopeLines(request)[2])!;

    private static string EnvelopeEventId(RecordedHttpRequest request)
    {
        var header = JsonNode.Parse(EnvelopeLines(request)[0])!.AsObject();
        Assert.Equal(new[] { "event_id", "sent_at" }, header.Select(p => p.Key));
        var id = (string)header["event_id"]!;
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", (string)header["sent_at"]!);
        return id;
    }

    // The requests in the order they were sent, by their item header.
    private string[] ItemHeaders => _handler.Requests.Select(ItemHeader).ToArray();

    // ---- Golden fixtures ----

    [Fact]
    public async Task The_golden_ok_log_envelope_matches_the_ts_exporter_byte_for_byte()
    {
        var golden = Assert.Single(Golden("sentry ok"));
        var clock = new ScriptedClock(At("2026-10-16T15:06:00.515Z"));
        var runtime = Runtime(clock, new FixedRandomSource());
        var exporter = Build(runtime, new SentryExporter("https://abc123@o1.ingest.sentry.io/42") { Environment = "production", Release = "1.2.3" });

        exporter.Export(GoldenEvent());
        await DrainAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(golden.Url, request.Uri.OriginalString);
        AssertHeaders(request, golden.Headers);
        Assert.Equal(golden.Body, Text(request));
        Assert.EndsWith("}]}\n", Text(request), StringComparison.Ordinal);
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public async Task The_golden_error_envelopes_with_tracing_match_the_ts_exporter_byte_for_byte()
    {
        var golden = Golden("sentry err tracing");
        Assert.Equal(3, golden.Count);
        var clock = new ScriptedClock(At("2026-10-16T15:06:00.516Z"), At("2026-10-16T15:06:00.517Z"), At("2026-10-16T15:06:00.517Z"));
        var random = new FixedRandomSource("2fa6effef6f49e2a", "6d550b55345aaa8f");
        var runtime = Runtime(clock, random);
        var exporter = Build(runtime, new SentryExporter("https://abc123@sentry.example.com:9000/sub/path/42") { EnableTracing = true });

        exporter.Export(GoldenEvent(isError: true));
        await DrainAsync();

        var requests = _handler.Requests;
        Assert.Equal(3, requests.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(golden[i].Url, requests[i].Uri.OriginalString);
            AssertHeaders(requests[i], golden[i].Headers);
            Assert.Equal(golden[i].Body, Text(requests[i]));
        }

        Assert.EndsWith("\n", Text(requests[0]), StringComparison.Ordinal);
        Assert.EndsWith("}", Text(requests[1]), StringComparison.Ordinal);
        Assert.EndsWith("}", Text(requests[2]), StringComparison.Ordinal);
        var trace = Item(requests[2])["contexts"]!["trace"]!;
        Assert.Equal(Span, (string)trace["span_id"]!);
        Assert.Equal(Span, (string)trace["parent_span_id"]!);
        Assert.Equal(2, random.Draws);
        Assert.Equal(3, clock.Reads);
    }

    [Fact]
    public async Task The_golden2_sessionless_log_envelope_matches_the_ts_exporter_byte_for_byte()
    {
        var lines = FixtureLines("golden2-output.txt");
        var section = Array.IndexOf(lines, "=== sentry");
        Assert.Equal(Endpoint + " ", lines[section + 1]);
        var end = Array.FindIndex(lines, section + 2, l => l.StartsWith("=== ", StringComparison.Ordinal));
        var body = string.Join("\n", lines.Skip(section + 2).Take(end - section - 2));
        Assert.StartsWith(" ", body, StringComparison.Ordinal);
        var clock = new ScriptedClock(At("2026-10-16T15:04:56.819Z"));
        var random = new FixedRandomSource("28e9c43b044f962e403ca154d3f51a05");

        Exporter(clock: clock, random: random).Export(SessionlessEvent());
        await DrainAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(Endpoint, request.Uri.OriginalString);
        Assert.Equal(body.Substring(1), Text(request));
        Assert.Equal(1, random.Draws);
    }

    // ---- PHP SentryExportTest ----

    [Fact]
    public void The_constructor_logs_the_endpoint()
    {
        Exporter();

        Assert.Equal(new[] { InitLine, "Initialized telemetry exporter: sentry" }, _log.Lines);
    }

    [Fact]
    public async Task A_success_event_without_tracing_sends_the_log_only()
    {
        var e = GoldenEvent();

        Exporter().Export(e);
        await DrainAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(Endpoint, request.Uri.OriginalString);
        Assert.Equal("Sentry sentry_version=7, sentry_client=agentcat/1.0.0, sentry_key=abc", request.Header("X-Sentry-Auth"));
        Assert.Equal("application/x-sentry-envelope", request.Header("Content-Type"));
        Assert.Equal(LogItem, ItemHeader(request));
        Assert.Equal(Span + Span, EnvelopeEventId(request));
        Assert.Equal(
            new[] { InitLine, "Initialized telemetry exporter: sentry", SendingLog, "Sentry log export success - Event: " + EventId },
            _log.Lines);
    }

    [Fact]
    public async Task An_error_event_with_tracing_sends_the_log_the_transaction_and_the_event_one_after_the_other()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = RecordingHttpHandler.Gated(gate);
        var exporter = Exporter(tracing: true, handler: handler);

        exporter.Export(GoldenEvent(isError: true));
        await FakeAgentCatApi.WithinAsync(WaitForRequestsAsync(handler, 1), Bound);
        await Task.Delay(100, Ct);
        Assert.Equal(1, handler.Count);
        gate.SetResult(true);
        await DrainAsync();

        var requests = handler.Requests;
        Assert.Equal(new[] { LogItem, TransactionItem, EventItem }, requests.Select(ItemHeader));
        Assert.All(requests, r => Assert.Equal(Endpoint, r.Uri.OriginalString));
        var transaction = EnvelopeEventId(requests[1]);
        var error = EnvelopeEventId(requests[2]);
        Assert.NotEqual(transaction, error);
        Assert.StartsWith(Span, transaction, StringComparison.Ordinal);
        Assert.StartsWith(Span, error, StringComparison.Ordinal);
        Assert.Equal(transaction, (string)Item(requests[1])["event_id"]!);
        Assert.Equal(error, (string)Item(requests[2])["event_id"]!);
        Assert.Contains("\"parent_span_id\":\"" + Span + "\"", Text(requests[2]), StringComparison.Ordinal);
        Assert.Equal(
            new[]
            {
                InitLine,
                "Initialized telemetry exporter: sentry",
                SendingLog,
                "Sentry log export success - Event: " + EventId,
                "SentryExporter: Sending transaction " + transaction + " to Sentry",
                "Sentry transaction export success - Event: " + EventId,
                "SentryExporter: Sending error event " + error + " to Sentry for Issue creation",
                "Sentry error export success - Event: " + EventId,
            },
            _log.Lines);
        Assert.Contains(_log.Written, w => w.Line.StartsWith("Sentry error export success", StringComparison.Ordinal) && w.Severity == LogSeverity.Information);
    }

    [Fact]
    public async Task An_error_event_without_tracing_sends_the_log_and_the_event()
    {
        Exporter().Export(GoldenEvent(isError: true));
        await DrainAsync();

        var requests = _handler.Requests;
        Assert.Equal(new[] { LogItem, EventItem }, requests.Select(ItemHeader));
        Assert.DoesNotContain("parent_span_id", Text(requests[1]), StringComparison.Ordinal);
        var trace = Item(requests[1])["contexts"]!["trace"]!.AsObject();
        Assert.Equal(new[] { "trace_id", "span_id", "op" }, trace.Select(p => p.Key));
        var error = EnvelopeEventId(requests[1]);
        Assert.Equal(
            new[]
            {
                InitLine,
                "Initialized telemetry exporter: sentry",
                SendingLog,
                "Sentry log export success - Event: " + EventId,
                "SentryExporter: Sending error event " + error + " to Sentry for Issue creation",
                "Sentry error export success - Event: " + EventId,
            },
            _log.Lines);
    }

    [Fact]
    public async Task A_success_event_with_tracing_sends_the_log_and_the_transaction()
    {
        Exporter(tracing: true).Export(GoldenEvent());
        await DrainAsync();

        Assert.Equal(new[] { LogItem, TransactionItem }, ItemHeaders);
        var transaction = Item(_handler.Requests[1]);
        Assert.Equal("ok", (string)transaction["contexts"]!["trace"]!["status"]!);
    }

    [Fact]
    public async Task A_network_error_on_the_log_ends_the_sequence()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("connection refused");

        Exporter(tracing: true).Export(GoldenEvent(isError: true));
        await DrainAsync();

        Assert.Equal(new[] { LogItem }, ItemHeaders);
        Assert.Equal(
            new[] { InitLine, "Initialized telemetry exporter: sentry", SendingLog, "Sentry export error: HttpRequestException: connection refused" },
            _log.Lines);
    }

    [Fact]
    public async Task A_network_error_on_the_transaction_ends_the_sequence()
    {
        var count = 0;
        _handler.Respond = (_, _) => Interlocked.Increment(ref count) == 2
            ? throw new HttpRequestException("timed out")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        Exporter(tracing: true).Export(GoldenEvent(isError: true));
        await DrainAsync();

        Assert.Equal(new[] { LogItem, TransactionItem }, ItemHeaders);
        Assert.Equal(
            new[]
            {
                InitLine,
                "Initialized telemetry exporter: sentry",
                SendingLog,
                "Sentry log export success - Event: " + EventId,
                "SentryExporter: Sending transaction " + EnvelopeEventId(_handler.Requests[1]) + " to Sentry",
                "Sentry export error: HttpRequestException: timed out",
            },
            _log.Lines);
    }

    [Fact]
    public async Task A_non_success_status_logs_the_body_and_the_sequence_goes_on()
    {
        var answers = new Queue<(HttpStatusCode Status, string? Reason, string Body)>(new[]
        {
            (HttpStatusCode.InternalServerError, (string?)null, "oops"),
            (HttpStatusCode.BadGateway, "", "bad gateway"),
            ((HttpStatusCode)429, "Too Many Requests", "slow down"),
        });
        _handler.Respond = (_, _) =>
        {
            (HttpStatusCode Status, string? Reason, string Body) answer;
            lock (answers)
            {
                answer = answers.Dequeue();
            }

            var response = new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body) };
            if (answer.Reason is not null)
            {
                response.ReasonPhrase = answer.Reason;
            }

            return Task.FromResult(response);
        };

        Exporter(tracing: true).Export(GoldenEvent(isError: true));
        await DrainAsync();

        var requests = _handler.Requests;
        Assert.Equal(new[] { LogItem, TransactionItem, EventItem }, requests.Select(ItemHeader));
        Assert.Equal(
            new[]
            {
                InitLine,
                "Initialized telemetry exporter: sentry",
                SendingLog,
                "Sentry log export failed - Status: 500, Body: oops",
                "SentryExporter: Sending transaction " + EnvelopeEventId(requests[1]) + " to Sentry",
                "Sentry transaction export failed - Status: 502, Body: bad gateway",
                "SentryExporter: Sending error event " + EnvelopeEventId(requests[2]) + " to Sentry for Issue creation",
                "Sentry error export failed - Status: 429, Body: slow down",
            },
            _log.Lines);
    }

    [Fact]
    public async Task A_non_success_status_without_a_body_logs_an_empty_body()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));

        Exporter().Export(GoldenEvent());
        await DrainAsync();

        Assert.Equal("Sentry log export failed - Status: 403, Body: ", _log.Lines[^1]);
    }

    // PHP testATransactionThatCannotBeEncodedEndsTheSequenceWithTheCatchLine: only the transaction and the error event
    // carry properties, so the log is sent and its answer logged first, as TS does.
    [Fact]
    public async Task A_transaction_that_cannot_be_built_ends_the_sequence_with_the_catch_line()
    {
        var e = GoldenEvent(isError: true);
        e.Properties = DeepObject();

        Exporter(tracing: true).Export(e);
        await DrainAsync();

        Assert.Equal(new[] { LogItem }, ItemHeaders);
        Assert.Equal(
            new[]
            {
                InitLine,
                "Initialized telemetry exporter: sentry",
                SendingLog,
                "Sentry log export success - Event: " + EventId,
                "Sentry export error: InvalidOperationException: JSON nesting exceeds 1000 levels.",
            },
            _log.Lines);
    }

    // PHP testAnErrorEventThatCannotBeEncodedEndsTheSequenceWithTheCatchLine.
    [Fact]
    public async Task An_error_event_that_cannot_be_built_ends_the_sequence_with_the_catch_line()
    {
        var e = GoldenEvent(isError: true);
        e.Properties = DeepObject();

        Exporter().Export(e);
        await DrainAsync();

        Assert.Equal(new[] { LogItem }, ItemHeaders);
        Assert.Equal(
            new[]
            {
                InitLine,
                "Initialized telemetry exporter: sentry",
                SendingLog,
                "Sentry log export success - Event: " + EventId,
                "Sentry export error: InvalidOperationException: JSON nesting exceeds 1000 levels.",
            },
            _log.Lines);
    }

    [Fact]
    public async Task A_network_error_before_a_step_that_cannot_be_built_logs_only_the_network_error()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("connection refused");
        var e = GoldenEvent(isError: true);
        e.Properties = DeepObject();

        Exporter(tracing: true).Export(e);
        await DrainAsync();

        Assert.Equal(new[] { LogItem }, ItemHeaders);
        Assert.Equal("Sentry export error: HttpRequestException: connection refused", Assert.Single(_log.Lines, l => l.StartsWith("Sentry export error", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_request_that_gets_no_answer_ends_the_sequence_with_the_timeout()
    {
        var clock = new ManualClock();
        clock.HoldDelays();
        var handler = RecordingHttpHandler.Silent();

        Exporter(tracing: true, clock: clock, handler: handler).Export(GoldenEvent(isError: true));
        await FakeAgentCatApi.WithinAsync(WaitForRequestsAsync(handler, 1), Bound);
        Assert.Equal(new[] { InitLine, "Initialized telemetry exporter: sentry", SendingLog }, _log.Lines);

        clock.ReleaseDelays();
        await DrainAsync();

        Assert.Equal(1, handler.Count);
        Assert.Equal("Sentry export error: Request timed out after 10000 ms", _log.Lines[^1]);
        Assert.Equal(4, _log.Lines.Count);
    }

    // ---- DSN ----

    // PHP masks the user-info of the DSN (TS writes it whole), so neither key nor secret reaches a line; the other
    // exporters are built and export.
    [Theory]
    [InlineData("https://ABC123@o1.ingest.sentry.io/42", "ABC123", "https://***@o1.ingest.sentry.io/42")]
    [InlineData("https://abc123:s3cr3t@o1.ingest.sentry.io/42", "s3cr3t", "https://***@o1.ingest.sentry.io/42")]
    [InlineData("https://abc123@o1.ingest.sentry.io/proj", "abc123", "https://***@o1.ingest.sentry.io/proj")]
    public async Task An_invalid_DSN_logs_a_masked_DSN_and_skips_only_Sentry(string dsn, string secret, string masked)
    {
        var runtime = Runtime();

        TelemetryManager.Install(runtime, new[]
        {
            new KeyValuePair<string, AgentCatExporter?>("otlp", new OtlpExporter("http://otlp.test")),
            new KeyValuePair<string, AgentCatExporter?>("sentry", new SentryExporter(dsn)),
            new KeyValuePair<string, AgentCatExporter?>("dd", new DatadogExporter("k", "datadoghq.com", "svc")),
        });
        runtime.Pipeline.Add(new QueuedEvent(GoldenEvent(isError: true)));
        Assert.True(await runtime.Pipeline.FlushAsync(Bound, Ct), "The event pipeline did not drain.");

        Assert.Equal(
            new[]
            {
                "Initialized telemetry exporter: otlp",
                "Failed to initialize exporter sentry: Error: Invalid Sentry DSN: " + masked,
                "Initialized telemetry exporter: dd",
                "Initialized telemetry with 3 exporters",
            },
            _log.Lines.Take(4));
        Assert.Equal(
            new[] { "api.datadoghq.com", "http-intake.logs.datadoghq.com", "otlp.test" },
            _handler.Requests.Select(r => r.Uri.Host).OrderBy(h => h, StringComparer.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains(dsn, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_valid_DSN_key_appears_in_no_line()
    {
        _handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad") });
        var runtime = Runtime();

        Build(runtime, new SentryExporter("https://5ec12e7@o1.ingest.sentry.io/42") { EnableTracing = true }).Export(GoldenEvent(isError: true));
        await DrainAsync();

        Assert.Equal(3, _handler.Count);
        Assert.Equal(8, _log.Lines.Count);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("5ec12e7", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://abc123@o1.ingest.sentry.io/42", "https://o1.ingest.sentry.io/api/42/envelope/", "abc123")]
    [InlineData("https://abc123@sentry.example.com:9000/sub/path/42", "https://sentry.example.com:9000/sub/path/api/42/envelope/", "abc123")]
    [InlineData("http://0f@localhost:0/1", "http://localhost:0/api/1/envelope/", "0f")]
    [InlineData("https://abc@h/a/1/2", "https://h/a/1/api/2/envelope/", "abc")]
    [InlineData("https://abc@h//1", "https://h//api/1/envelope/", "abc")]
    [InlineData("https://abc@my_host-1.x/007", "https://my_host-1.x/api/007/envelope/", "abc")]
    [InlineData("https://abc@h/p q/1", "https://h/p q/api/1/envelope/", "abc")]
    [InlineData("https://abc@h/a\u0085b/1", "https://h/a\u0085b/api/1/envelope/", "abc")]
    public void A_DSN_gives_the_ts_endpoint_and_key(string dsn, string endpoint, string key)
    {
        var parsed = SentryDsn.Parse(dsn);

        Assert.Equal(endpoint, parsed.Endpoint);
        Assert.Equal(key, parsed.PublicKey);
    }

    // The TS regex /^(https?):\/\/([a-f0-9]+)@([\w.-]+)(:\d+)?(\/.*)?\/(\d+)$/: \w and \d are ASCII only, "." stops at
    // \n, \r, U+2028 and U+2029, and $ is the very end.
    [Theory]
    [InlineData("https://ABC@o1.ingest.sentry.io/42")]
    [InlineData("https://abc@o1.ingest.sentry.io/proj")]
    [InlineData("https://abc:secret@o1.ingest.sentry.io/42")]
    [InlineData("https://abc@o1.ingest.sentry.io")]
    [InlineData("https://abc@o1.ingest.sentry.io/")]
    [InlineData("https://abc@o1.ingest.sentry.io/42\n")]
    [InlineData("https://abc@o1.ingest.sentry.io/42/")]
    [InlineData("https://abc@h/\u0664\u0662")]
    [InlineData("https://abc@h\u00F4st/42")]
    [InlineData("https://abc@h/a\nb/42")]
    [InlineData("https://abc@h/a\rb/42")]
    [InlineData("https://abc@h/a\u2028b/42")]
    [InlineData("https://abc@h/a\u2029b/42")]
    [InlineData("https://abc@h:/42")]
    [InlineData("https://abc@h:9x/42")]
    [InlineData("HTTPS://abc@h/42")]
    [InlineData("ftp://abc@h/42")]
    [InlineData("https://abg@h/42")]
    [InlineData("https://@h/42")]
    [InlineData(" https://abc@h/42")]
    [InlineData("")]
    public void A_DSN_the_ts_regex_rejects_throws_the_ts_error(string dsn)
    {
        var error = Assert.Throws<JsError>(() => SentryDsn.Parse(dsn));

        Assert.Equal("Error", error.Name);
        Assert.StartsWith("Invalid Sentry DSN: ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://ABC@o1.ingest.sentry.io/42", "https://***@o1.ingest.sentry.io/42")]
    [InlineData("https://pub:secret@o1.ingest.sentry.io/42", "https://***@o1.ingest.sentry.io/42")]
    [InlineData("https://pub:se@cret@host/1", "https://***@host/1")]
    [InlineData("pub@host/1", "***@host/1")]
    [InlineData("https://@host/1", "https://@host/1")]
    [InlineData("not a dsn", "not a dsn")]
    [InlineData("", "")]
    public void The_user_info_of_an_invalid_DSN_is_masked(string dsn, string masked)
    {
        var error = Assert.Throws<JsError>(() => SentryDsn.Parse(dsn));

        Assert.Equal("Invalid Sentry DSN: " + masked, error.Message);
    }

    // ---- Envelope contents ----

    // TS buildLogAttributes: strings when truthy; duration_ms and isError whenever set, so 0 and false are sent.
    [Fact]
    public async Task Log_attributes_follow_the_ts_presence_rules()
    {
        var exporter = Exporter();
        var full = GoldenEvent();
        full.Duration = 0;
        var sparse = new AgentCatEvent
        {
            Id = "evt_sparse",
            SessionId = "ses_1",
            ProjectId = "",
            EventType = "",
            Timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 5, 0, TimeSpan.Zero),
            ServerName = "",
            ClientName = null,
            UserIntent = "",
        };

        exporter.Export(full);
        exporter.Export(sparse);
        await DrainAsync();

        var logs = _handler.Requests.Select(r => Item(r)["items"]![0]!.AsObject()).ToArray();
        var first = logs.Single(l => (string)l["body"]! == "MCP mcp:tools/call: get_weather");
        Assert.Equal(
            new[] { "eventType", "resourceName", "serverName", "clientName", "sessionId", "projectId", "duration_ms", "actorId", "actorName", "userIntent", "serverVersion", "clientVersion", "isError" },
            first["attributes"]!.AsObject().Select(p => p.Key));
        Assert.Equal("{\"value\":0,\"type\":\"double\"}", first["attributes"]!["duration_ms"]!.ToJsonString());
        Assert.Equal("{\"value\":false,\"type\":\"boolean\"}", first["attributes"]!["isError"]!.ToJsonString());
        Assert.Equal("info", (string)first["level"]!);
        var second = logs.Single(l => (string)l["body"]! == "MCP event");
        Assert.Equal(new[] { "sessionId" }, second["attributes"]!.AsObject().Select(p => p.Key));
        Assert.Equal("1749988805", second["timestamp"]!.ToJsonString());
        Assert.Equal(new[] { "timestamp", "trace_id", "event_id", "level", "body", "attributes" }, second.Select(p => p.Key));
    }

    public static TheoryData<string?, string, string> ErrorValues => new()
    {
        { "{\"message\":\"boom\",\"type\":\"TypeError\"}", "TypeError", "boom" },
        { "{\"message\":\"\"}", "ToolCallError", "" },
        { "{\"message\":null}", "ToolCallError", "null" },
        { "{\"type\":null,\"message\":\"m\"}", "null", "m" },
        { "{\"message\":\"m\",\"type\":7}", "7", "m" },
        { "{\"code\":5,\"type\":\"E\"}", "E", "{\"code\":5,\"type\":\"E\"}" },
        { "{\"message\":{\"a\":1}}", "ToolCallError", "[object Object]" },
        { "{\"message\":[1,[2,null],\"x\"]}", "ToolCallError", "1,2,,x" },
        { "{\"Message\":\"m\"}", "ToolCallError", "{\"Message\":\"m\"}" },
        { "[1,{\"message\":\"m\"}]", "ToolCallError", "[1,{\"message\":\"m\"}]" },
        { "\"plain failure\"", "ToolCallError", "plain failure" },
        { "\"\"", "ToolCallError", "Unknown error" },
        { "0", "ToolCallError", "Unknown error" },
        { "42", "ToolCallError", "Unknown error" },
        { "true", "ToolCallError", "Unknown error" },
        { "false", "ToolCallError", "Unknown error" },
        { null, "ToolCallError", "Unknown error" },
    };

    // TS eventToErrorEvent, exactly: "message" in / "type" in, String(), JSON.stringify, with no fallback for an empty
    // or null member.
    [Theory]
    [MemberData(nameof(ErrorValues))]
    public async Task The_exception_type_and_value_follow_the_ts_rules(string? error, string type, string value)
    {
        var e = GoldenEvent(isError: true);
        e.Error = error is null ? null : JsonNode.Parse(error);

        Exporter().Export(e);
        await DrainAsync();

        var exception = Item(_handler.Requests[1])["exception"]!["values"]![0]!;
        Assert.Equal(type, (string)exception["type"]!);
        Assert.Equal(value, (string)exception["value"]!);
        Assert.Equal("{\"type\":\"mcp_tool_call\",\"handled\":false}", exception["mechanism"]!.ToJsonString());
    }

    [Fact]
    public async Task A_falsy_error_is_left_out_of_extra_and_a_truthy_one_is_sent_whole()
    {
        var exporter = Exporter();
        var empty = GoldenEvent(isError: true);
        empty.Error = JsonValue.Create("");
        var zero = GoldenEvent(isError: true);
        zero.Error = JsonValue.Create(0);
        var text = GoldenEvent(isError: true);
        text.Error = JsonValue.Create("plain");

        exporter.Export(empty);
        exporter.Export(zero);
        exporter.Export(text);
        await DrainAsync();

        var extras = _handler.Requests
            .Where(r => ItemHeader(r) == EventItem)
            .Select(r => Item(r)["extra"]!.AsObject())
            .ToArray();
        Assert.Equal(3, extras.Length);
        Assert.Equal(2, extras.Count(x => !x.ContainsKey("error")));
        Assert.Equal("plain", (string)Assert.Single(extras, x => x.ContainsKey("error"))["error"]!);
    }

    [Fact]
    public async Task Environment_and_release_are_tags_and_a_null_tag_value_is_sent_as_null()
    {
        var e = GoldenEvent();
        e.Tags = new Dictionary<string, string> { ["a"] = "1", ["b"] = null!, ["c"] = "" };
        var runtime = Runtime();

        Build(runtime, new SentryExporter(Dsn) { Environment = "production", Release = "1.2.3", EnableTracing = true }).Export(e);
        await DrainAsync();

        var tags = Item(_handler.Requests[1])["tags"]!.ToJsonString();
        Assert.Equal(
            "{\"source\":\"agentcat\",\"environment\":\"production\",\"release\":\"1.2.3\",\"event_type\":\"mcp:tools/call\",\"resource\":\"get_weather\",\"server_name\":\"weather-server\",\"client_name\":\"claude-desktop\",\"actor_id\":\"user_abc\",\"agentcat.a\":\"1\",\"agentcat.b\":null,\"agentcat.c\":\"\"}",
            tags);
    }

    [Fact]
    public async Task A_minimal_event_gets_the_ts_defaults()
    {
        var e = new AgentCatEvent
        {
            Id = EventId,
            SessionId = SessionId,
            Timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 5, 123, TimeSpan.Zero),
            IsError = true,
        };

        Exporter(tracing: true).Export(e);
        await DrainAsync();

        var requests = _handler.Requests;
        Assert.Equal("MCP event", (string)Item(requests[0])["items"]![0]!["body"]!);
        Assert.Equal("error", (string)Item(requests[0])["items"]![0]!["level"]!);
        var transaction = Item(requests[1]);
        Assert.Equal("mcp.event", (string)transaction["transaction"]!);
        Assert.Equal("1749988805.123", transaction["start_timestamp"]!.ToJsonString());
        Assert.Equal(
            "{\"trace\":{\"trace_id\":\"b17987c6b00767670c45519276a327c3\",\"span_id\":\"" + Span + "\",\"op\":\"mcp.event\",\"status\":\"internal_error\"}}",
            transaction["contexts"]!.ToJsonString());
        Assert.Equal("{\"source\":\"agentcat\"}", transaction["tags"]!.ToJsonString());
        Assert.Equal("{\"session_id\":\"" + SessionId + "\"}", transaction["extra"]!.ToJsonString());
        var error = Item(requests[2]);
        Assert.Equal("mcp.event", (string)error["transaction"]!);
        Assert.Equal(
            "{\"trace\":{\"trace_id\":\"b17987c6b00767670c45519276a327c3\",\"span_id\":\"" + Span + "\",\"parent_span_id\":\"" + Span + "\",\"op\":\"mcp.event\"},\"mcp\":{\"session_id\":\"" + SessionId + "\",\"event_type\":\"\"}}",
            error["contexts"]!.ToJsonString());
        Assert.Equal("{\"type\":\"ToolCallError\",\"value\":\"Unknown error\",\"mechanism\":{\"type\":\"mcp_tool_call\",\"handled\":false}}", error["exception"]!["values"]![0]!.ToJsonString());
    }

    [Fact]
    public async Task A_resource_without_an_event_type_names_the_transaction_mcp()
    {
        var e = SessionlessEvent();
        e.SessionId = SessionId;
        e.EventType = "";
        e.ResourceName = "r";
        e.Duration = 2;

        Exporter(tracing: true).Export(e);
        await DrainAsync();

        Assert.Equal("MCP event: r", (string)Item(_handler.Requests[0])["items"]![0]!["body"]!);
        var transaction = Item(_handler.Requests[1]);
        Assert.Equal("mcp - r", (string)transaction["transaction"]!);
        Assert.Equal("1749988805.1209998", transaction["start_timestamp"]!.ToJsonString());
    }

    [Fact]
    public async Task Empty_properties_are_sent_as_the_agentcat_context()
    {
        var e = GoldenEvent();
        e.Properties = new JsonObject();

        Exporter(tracing: true).Export(e);
        await DrainAsync();

        Assert.Equal("{}", Item(_handler.Requests[1])["contexts"]!["agentcat"]!.ToJsonString());
    }

    // TS quirk, kept: a sessionless event with tracing gets a new random trace ID in the log, the transaction and the
    // error event. The transaction TS builds again for the error event gives only its trace ID, so its unused event-ID
    // half is not drawn.
    [Fact]
    public async Task A_sessionless_error_with_tracing_draws_a_trace_id_for_each_envelope()
    {
        var random = new FixedRandomSource(
            "11111111111111111111111111111111",
            "22222222222222222222222222222222",
            "aaaaaaaaaaaaaaaa",
            "33333333333333333333333333333333",
            "bbbbbbbbbbbbbbbb");
        var e = SessionlessEvent();
        e.IsError = true;

        Exporter(tracing: true, random: random).Export(e);
        await DrainAsync();

        var requests = _handler.Requests;
        Assert.Equal(5, random.Draws);
        Assert.Equal("11111111111111111111111111111111", (string)Item(requests[0])["items"]![0]!["trace_id"]!);
        Assert.Equal("22222222222222222222222222222222", (string)Item(requests[1])["contexts"]!["trace"]!["trace_id"]!);
        Assert.Equal(Span + "aaaaaaaaaaaaaaaa", EnvelopeEventId(requests[1]));
        Assert.Equal("33333333333333333333333333333333", (string)Item(requests[2])["contexts"]!["trace"]!["trace_id"]!);
        Assert.Equal(Span + "bbbbbbbbbbbbbbbb", EnvelopeEventId(requests[2]));
        Assert.Equal("{\"session_id\":\"\",\"event_type\":\"mcp:tools/list\"}", Item(requests[2])["contexts"]!["mcp"]!.ToJsonString());
    }

    // TS B-2: every AgentCat attempt exports the same event again. Properties and error go into two envelopes each time.
    [Fact]
    public async Task Four_exports_of_one_event_send_identical_bytes_and_leave_it_unchanged()
    {
        var clock = new ScriptedClock(At("2026-10-16T15:06:00.516Z"));
        var random = new FixedRandomSource(Enumerable.Repeat("2fa6effef6f49e2a", 8).ToArray());
        var exporter = Exporter(tracing: true, clock: clock, random: random);
        var e = GoldenEvent(isError: true);
        var before = e.Clone();

        for (var i = 0; i < 4; i++)
        {
            exporter.Export(e);
        }

        await DrainAsync();

        var requests = _handler.Requests;
        Assert.Equal(12, requests.Count);
        foreach (var kind in new[] { LogItem, TransactionItem, EventItem })
        {
            var bodies = requests.Where(r => ItemHeader(r) == kind).Select(Text).ToArray();
            Assert.Equal(4, bodies.Length);
            Assert.Single(bodies.Distinct(StringComparer.Ordinal));
        }

        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Sentry export error", StringComparison.Ordinal));
        Assert.Null(e.Properties!.Parent);
        Assert.Null(e.Error!.Parent);
        Assert.Equal(before.Properties!.ToJsonString(), e.Properties.ToJsonString());
        Assert.Equal(before.Error!.ToJsonString(), e.Error.ToJsonString());
        Assert.Equal(before.Parameters!.ToJsonString(), e.Parameters!.ToJsonString());
        Assert.Equal(before.Response!.ToJsonString(), e.Response!.ToJsonString());
        Assert.Equal(before.IdentifyActorData!.ToJsonString(), e.IdentifyActorData!.ToJsonString());
        Assert.Equal(before.Tags, e.Tags);
        Assert.Equal(before.Id, e.Id);
        Assert.Equal(before.SessionId, e.SessionId);
        Assert.Equal(before.Duration, e.Duration);
        Assert.Equal(before.IsError, e.IsError);
    }

    [Fact]
    public async Task Concurrent_exports_each_send_their_own_envelopes()
    {
        var exporter = Exporter(tracing: true);

        Parallel.For(0, 40, i =>
        {
            var e = GoldenEvent(isError: true);
            e.Id = "evt_" + i;
            exporter.Export(e);
        });
        await DrainAsync();

        var requests = _handler.Requests;
        Assert.Equal(120, requests.Count);
        Assert.Equal(40, requests.Where(r => ItemHeader(r) == LogItem).Select(EnvelopeEventId).Distinct().Count());
        Assert.Equal(40, requests.Where(r => ItemHeader(r) == TransactionItem).Select(EnvelopeEventId).Distinct().Count());
        Assert.Equal(40, requests.Where(r => ItemHeader(r) == EventItem).Select(EnvelopeEventId).Distinct().Count());
        Assert.Equal(40, _log.Lines.Count(l => l.StartsWith("Sentry error export success - Event: evt_", StringComparison.Ordinal)));
    }

    private static JsonObject DeepObject()
    {
        JsonNode deep = new JsonObject();
        for (var i = 0; i < 1100; i++)
        {
            deep = new JsonObject { ["a"] = deep };
        }

        return (JsonObject)deep;
    }

    private static async Task WaitForRequestsAsync(RecordingHttpHandler handler, int count)
    {
        while (handler.Count < count)
        {
            await Task.Delay(10, Ct);
        }
    }

    // UtcNow gives the scripted times in turn, then keeps the last one; delays are real.
    private sealed class ScriptedClock : IPipelineClock
    {
        private readonly object _gate = new();
        private readonly Queue<DateTimeOffset> _times;
        private DateTimeOffset _last;

        internal ScriptedClock(params DateTimeOffset[] times)
        {
            _times = new Queue<DateTimeOffset>(times);
            _last = times[0];
        }

        internal int Reads { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_gate)
                {
                    Reads++;
                    if (_times.Count > 0)
                    {
                        _last = _times.Dequeue();
                    }

                    return _last;
                }
            }
        }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }
}
