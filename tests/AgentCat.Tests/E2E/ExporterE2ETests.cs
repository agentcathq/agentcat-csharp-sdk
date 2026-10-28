using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AgentCat.Internal;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.E2E;

// The four exporters behind a real MCP server. On the stateless lanes, WithAgentCat configures them on a lane runtime
// that hosts the real exporters, never on AgentCatRuntime.Shared: its first-wins claim would last for the whole test run.
// The stdio server process exports through its own Shared runtime. Every exporter request goes to a local fake host.
public class ExporterE2ETests
{
    // One export with every exporter of E2EExporters, Datadog and tracing included: OTLP one span, Datadog a log and a
    // metrics series, Sentry a log then a transaction, PostHog one batch.
    private const int RequestsPerExport = 6;

    // Of those, the ones that start at once: Sentry sends its transaction only after the log's answer.
    private const int FirstRequestsPerExport = 5;

    private const string Intent = "exported everywhere";
    private const string Secret = "SECRET_TOKEN";
    private const string RedactedIntent = "intent rewritten by RedactEvent";
    private const string RedactedResource = "resource_rewritten_by_redact_event";
    private const string Hosted = "hosted";
    private const string TelemetryOnlySetupPrefix = "AgentCat setup started | project (telemetry-only) |";
    private const string NoProjectWarning = "Warning: No projectId provided and no exporters configured. Events will not be sent anywhere.";

    // The capture item, then the AI span item.
    private static readonly string[] PostHogEvents = { "mcp_tool_call", "$ai_span" };

    private static readonly string[] SetupLines =
    {
        "Initialized telemetry exporter: " + E2EExporters.Otlp,
        "Initialized telemetry exporter: " + E2EExporters.Datadog,
        "Initialized telemetry exporter: " + E2EExporters.Sentry,
        "Initialized telemetry exporter: " + E2EExporters.PostHog,
        "Initialized telemetry with 4 exporters",
    };

    // A tool call reaches every exporter once, each request carrying the event. The stateless server builds new options
    // for every POST, yet sets up one set of exporters.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task A_tool_call_reaches_every_configured_exporter(string lane)
    {
        using var host = new FakeExporterHost();
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, host.Lane());

        await e2e.ListToolsAsync();
        E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi","context":"exported everywhere"}"""));

        // The flush waits for the exporter requests too.
        await e2e.AssertEventCountAsync(1);
        var sent = e2e.Events[0];
        Assert.Equal(E2EToolkit.ProjectId, sent.ProjectId);
        AssertOneExportOf(host, sent.Id!, E2EAssert.SessionId(sent.SessionId));
        Assert.Equal(Intent, OtlpAttribute(host.Otlp[0], "mcp.user_intent"));
        Assert.Equal(E2EToolkit.ProjectId, OtlpAttribute(host.Otlp[0], "mcp.project_id"));
        Assert.Equal("echo", OtlpAttribute(host.Otlp[0], "mcp.resource_name"));
        Assert.Equal(Intent, (string?)FakeExporterHost.Json(host.DatadogLogs[0])[0]!["mcp"]!["user_intent"]);
        Assert.Equal(Intent, (string?)SentryLogAttributes(host.Sentry[0])["userIntent"]!["value"]);
        Assert.Equal(Intent, (string?)FakeExporterHost.Envelope(host.Sentry[1])[2]["extra"]!["user_intent"]);
        Assert.Equal(Intent, (string?)PostHogCapture(host.PostHog[0])["user_intent"]);

        var log = e2e.Log;
        Assert.Equal(SetupLines, log.Where(l => l.StartsWith("Initialized telemetry", StringComparison.Ordinal)));
        var complete = Assert.Single(log, l => l.StartsWith(ExpectedLog.SetupCompletePrefix, StringComparison.Ordinal));
        Assert.EndsWith(" exporters=4", complete, StringComparison.Ordinal);
        AssertSuccessLines(log, sent.Id!);
        Assert.DoesNotContain(log, l => l.StartsWith("Telemetry export", StringComparison.Ordinal));
    }

    // Without a project ID, the event goes to the exporters only: the AgentCat API never hears of it, and the exporters
    // stand in for the project, so the no-project warning stays out.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task A_telemetry_only_server_exports_and_never_calls_the_api(string lane)
    {
        using var host = new FakeExporterHost();
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, host.Lane(projectId: ""));

        E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi","context":"exported everywhere"}"""));

        await host.Server.WaitForRequestsAsync(RequestsPerExport, LaneInstance.EventBound);
        await e2e.AssertEventCountAsync(0);
        Assert.Empty(e2e.Api.Requests);
        var mcp = FakeExporterHost.Json(Assert.Single(host.DatadogLogs))[0]!["mcp"]!;
        var eventId = (string)mcp["event_id"]!;
        Assert.StartsWith("evt_", eventId, StringComparison.Ordinal);
        AssertOneExportOf(host, eventId, E2EAssert.SessionId((string?)mcp["session_id"]));
        Assert.Equal(Intent, OtlpAttribute(host.Otlp[0], "mcp.user_intent"));
        Assert.Null(OtlpAttribute(host.Otlp[0], "mcp.project_id"));
        Assert.False(SentryLogAttributes(host.Sentry[0]).ContainsKey("projectId"));
        Assert.False(PostHogCapture(host.PostHog[0]).ContainsKey("project_id"));

        var log = e2e.Log;
        Assert.Single(log, l => l.StartsWith(TelemetryOnlySetupPrefix, StringComparison.Ordinal));
        Assert.Equal(SetupLines, log.Where(l => l.StartsWith("Initialized telemetry", StringComparison.Ordinal)));
        Assert.DoesNotContain(NoProjectWarning, log);
        Assert.DoesNotContain(log, l => l.StartsWith(ExpectedLog.SentPrefix, StringComparison.Ordinal));
        AssertSuccessLines(log, eventId);
    }

    // The exporters get the event after RedactEvent, as the AgentCat API does: the hook's resource name is in every
    // request and its intent in every request that carries an intent (all but the Datadog metrics), and the secret it
    // removed is in none.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task Exports_carry_the_redacted_event(string lane)
    {
        using var host = new FakeExporterHost();
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, host.Lane(configure: o => o.RedactEvent = static (e, _) =>
        {
            e.UserIntent = RedactedIntent;
            e.ResourceName = RedactedResource;
            if (e.Parameters is { } parameters)
            {
                e.Parameters = JsonNode.Parse(parameters.ToJsonString().Replace(Secret, "[REDACTED]", StringComparison.Ordinal));
            }

            return new ValueTask<AgentCatEvent?>(e);
        }));

        E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi","context":"look up SECRET_TOKEN for the user"}"""));

        await e2e.AssertEventCountAsync(1);
        var sent = e2e.Events[0];
        Assert.Equal(RedactedIntent, sent.UserIntent);
        Assert.Equal(RedactedResource, sent.ResourceName);
        Assert.DoesNotContain(Secret, sent.ToString(), StringComparison.Ordinal);
        AssertOneExportOf(host, sent.Id!, E2EAssert.SessionId(sent.SessionId));
        Assert.All(host.Requests, r =>
        {
            var body = FakeExporterHost.Text(r);
            Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
            Assert.Contains(RedactedResource, body, StringComparison.Ordinal);
            if (r.Path != E2EExporters.DatadogMetricsPath)
            {
                Assert.Contains(RedactedIntent, body, StringComparison.Ordinal);
            }
        });
        Assert.Equal(RedactedIntent, OtlpAttribute(host.Otlp[0], "mcp.user_intent"));
        Assert.Equal(RedactedResource, OtlpAttribute(host.Otlp[0], "mcp.resource_name"));
        Assert.Contains("resource:" + RedactedResource, FakeExporterHost.Json(host.DatadogMetrics[0])["series"]![0]!["tags"]!.AsArray().Select(t => (string?)t));
        Assert.Equal(RedactedIntent, (string?)SentryLogAttributes(host.Sentry[0])["userIntent"]!["value"]);
        Assert.Equal(RedactedIntent, (string?)PostHogCapture(host.PostHog[0])["user_intent"]);
        Assert.Equal(RedactedResource, (string?)PostHogCapture(host.PostHog[0])["resource_name"]);

        // PostHog carries the parameters: the ones the hook rewrote, as the AgentCat API got them.
        var parameters = PostHogCapture(host.PostHog[0])["parameters"];
        Assert.Equal("look up [REDACTED] for the user", (string?)parameters?["request"]?["params"]?["arguments"]?["context"]);
        Assert.True(JsonNode.DeepEquals(sent.Parameters, parameters), "PostHog parameters: " + parameters?.ToJsonString());
    }

    // Review Focus 1. The API fails once, so the event is exported on both attempts (B-2), and the exporter host never
    // answers. The event is delivered while every exporter request still waits: delivery never waits for an exporter,
    // and the requests stay within the channel's bounds.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task A_slow_exporter_never_delays_delivery(string lane)
    {
        using var host = new FakeExporterHost();
        host.Server.DefaultStatus = FakeAgentCatApi.Hang;
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, host.Lane());
        e2e.Api.Respond(500);

        var clock = Stopwatch.StartNew();
        E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi","context":"exported everywhere"}"""));
        await e2e.WaitForLogAsync(l => l.StartsWith(ExpectedLog.SentPrefix, StringComparison.Ordinal), LaneInstance.EventBound);
        var delivered = clock.Elapsed;
        var log = e2e.Log;
        var inFlight = e2e.Runtime.Exports.InFlightCount;
        var waiting = e2e.Runtime.Exports.WaitingCount;

        TestContext.Current.TestOutputHelper?.WriteLine("delivered after " + delivered.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s");
        Assert.True(delivered < ExportChannel.RequestTimeout, "The event was delivered " + delivered + " after the call, past the exporter timeout of " + ExportChannel.RequestTimeout + ".");
        Assert.DoesNotContain(log, IsExportOutcome);
        Assert.Equal(2 * FirstRequestsPerExport, inFlight);
        Assert.Equal(0, waiting);
        var attempts = e2e.Api.Requests.Select(WireEvent.Parse).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(attempts[0].Id, attempts[1].Id);
        Assert.Single(log, l => l.StartsWith("Failed to send event " + attempts[0].Id + ", retrying...", StringComparison.Ordinal));

        await host.Server.WaitForRequestsAsync(2 * FirstRequestsPerExport, LaneInstance.EventBound);
        Assert.Equal(2, host.Otlp.Count);
        Assert.Equal(2, host.DatadogLogs.Count);
        Assert.Equal(2, host.DatadogMetrics.Count);
        Assert.Equal(2, host.Sentry.Count);
        Assert.Equal(2, host.PostHog.Count);

        // Closing the host ends the requests it holds, so the lane stops without waiting for them.
        host.Dispose();
    }

    // Review Focus 4. A telemetry-only stdio server stops while its exporter requests wait for their answers: stdin
    // closes once each request has reached the host, which answers only after the server's stop has started. The stop
    // waits for the answers, so every exporter writes its success line, and the process exits within the stop budget.
    // With a .NET host the flush after the host stop waits; without one, the process-exit handler does.
    [Theory]
    [InlineData(Hosted)]
    [InlineData(E2EToolkit.NoHostValue)]
    public async Task Exporter_requests_in_flight_are_sent_at_shutdown(string serverHost)
    {
        using var api = new FakeAgentCatApi();
        using var host = new FakeExporterHost();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Server.AnswerGate = answer.Task;
        await using var server = StdioServerProcess.Start(api, environment =>
        {
            environment[E2EToolkit.ProjectVariable] = E2EToolkit.TelemetryOnlyValue;
            environment[E2EExporters.HostVariable] = host.BaseUrl;
            if (serverHost == E2EToolkit.NoHostValue)
            {
                environment[E2EToolkit.HostVariable] = E2EToolkit.NoHostValue;
            }
            else
            {
                // Only the flush after the host stop can wait for the answers.
                environment[E2EToolkit.ShutdownHandlersVariable] = E2EToolkit.OffValue;
            }
        });
        await server.ConnectAsync();
        E2EAssert.Succeeded(await server.CallToolAsync("""{"name":"echo","arguments":{"msg":"hi","context":"exported at shutdown"}}"""));
        await host.Server.WaitForRequestsAsync(3, LaneInstance.EventBound);
        Assert.DoesNotContain(server.LogMessages(), IsExportOutcome);

        var stop = Stopwatch.StartNew();
        await server.CloseStdinAsync();
        await server.WaitForStderrAsync(serverHost == E2EToolkit.NoHostValue ? E2EToolkit.ProcessExitLine : E2EToolkit.HostStoppingLine, StdioServerProcess.ExitBound);
        answer.SetResult(true);
        await server.WaitForExitAsync();
        var elapsed = stop.Elapsed;

        Assert.Equal(0, server.ExitCode);
        if (!OperatingSystem.IsWindows())
        {
            ShutdownE2ETests.AssertWithinBudget(elapsed, server);
        }

        Assert.Empty(api.Requests);
        Assert.Equal(3, host.Requests.Count);
        var otlp = Assert.Single(host.Otlp);
        var sentry = Assert.Single(host.Sentry);
        var posthog = Assert.Single(host.PostHog);
        Assert.All(host.Requests, r => Assert.Equal(SdkInfo.UserAgent, r.Header("User-Agent")));
        Assert.Equal("exported at shutdown", OtlpAttribute(otlp, "mcp.user_intent"));
        Assert.Equal("exported at shutdown", (string?)SentryLogAttributes(sentry)["userIntent"]!["value"]);
        Assert.Equal("exported at shutdown", (string?)PostHogCapture(posthog)["user_intent"]);

        var messages = server.LogMessages();
        Assert.Single(messages, m => m.StartsWith(TelemetryOnlySetupPrefix, StringComparison.Ordinal));
        Assert.Single(messages, "Initialized telemetry with 3 exporters");
        var exported = Assert.Single(messages, m => m.StartsWith("Successfully exported event to OTLP: ", StringComparison.Ordinal));
        var eventId = exported.Substring("Successfully exported event to OTLP: ".Length);
        Assert.Equal(TraceIds.GetSpanId(eventId), (string?)OtlpSpan(otlp)["spanId"]);
        Assert.Single(messages, "Sentry log export success - Event: " + eventId);
        Assert.Single(messages, "PostHog export success - Event: " + eventId);
        Assert.DoesNotContain(messages, m => m.StartsWith(ExpectedLog.SentPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(NoProjectWarning, messages);
    }

    // The requests of one successful export of the event, with tracing on. Each is tied to the event by an ID that the
    // exporter derives from the event ID or the session ID, as every AgentCat SDK derives them.
    internal static void AssertOneExportOf(FakeExporterHost host, string eventId, string sessionId)
    {
        Assert.Equal(RequestsPerExport, host.Requests.Count);
        Assert.All(host.Requests, r =>
        {
            Assert.Equal("POST", r.Method);
            Assert.Equal(SdkInfo.UserAgent, r.Header("User-Agent"));
        });

        var otlp = Assert.Single(host.Otlp);
        Assert.Equal("application/json", otlp.Header("Content-Type"));
        Assert.Equal(TraceIds.GetTraceId(sessionId), (string?)OtlpSpan(otlp)["traceId"]);
        Assert.Equal(TraceIds.GetSpanId(eventId), (string?)OtlpSpan(otlp)["spanId"]);

        var datadogLog = Assert.Single(host.DatadogLogs);
        Assert.Equal(E2EExporters.DatadogLogsHost, datadogLog.Header("Host"));
        Assert.Equal(E2EExporters.DatadogApiKey, datadogLog.Header("DD-API-KEY"));
        var entry = FakeExporterHost.Json(datadogLog)[0]!;
        Assert.Equal(E2EExporters.DatadogService, (string?)entry["service"]);
        Assert.Equal(eventId, (string?)entry["mcp"]!["event_id"]);
        Assert.Equal(sessionId, (string?)entry["mcp"]!["session_id"]);
        var datadogMetrics = Assert.Single(host.DatadogMetrics);
        Assert.Equal(E2EExporters.DatadogMetricsHost, datadogMetrics.Header("Host"));
        Assert.Equal(E2EExporters.DatadogApiKey, datadogMetrics.Header("DD-API-KEY"));
        Assert.Equal("mcp.events.count", (string?)FakeExporterHost.Json(datadogMetrics)["series"]![0]!["metric"]);

        // The log envelope, then the transaction, which starts after the log's answer.
        Assert.Equal(2, host.Sentry.Count);
        Assert.All(host.Sentry, r =>
        {
            Assert.Equal("application/x-sentry-envelope", r.Header("Content-Type"));
            Assert.Equal("Sentry sentry_version=7, sentry_client=agentcat/1.0.0, sentry_key=" + E2EExporters.SentryPublicKey, r.Header("X-Sentry-Auth"));
        });
        var sentryLog = FakeExporterHost.Envelope(host.Sentry[0]);
        Assert.Equal("log", (string?)sentryLog[1]["type"]);
        var logItem = sentryLog[2]["items"]![0]!;
        Assert.Equal(TraceIds.GetSpanId(eventId) + TraceIds.GetSpanId(eventId), (string?)logItem["event_id"]);
        Assert.Equal(TraceIds.GetTraceId(sessionId), (string?)logItem["trace_id"]);
        var transaction = FakeExporterHost.Envelope(host.Sentry[1]);
        Assert.Equal("transaction", (string?)transaction[1]["type"]);
        Assert.Equal(TraceIds.GetSpanId(eventId), (string?)transaction[2]["contexts"]!["trace"]!["span_id"]);
        Assert.Equal(TraceIds.GetTraceId(sessionId), (string?)transaction[2]["contexts"]!["trace"]!["trace_id"]);

        var posthog = Assert.Single(host.PostHog);
        var batch = FakeExporterHost.Json(posthog);
        Assert.Equal(E2EExporters.PostHogApiKey, (string?)batch["api_key"]);
        var items = batch["batch"]!.AsArray();
        Assert.Equal(PostHogEvents, items.Select(i => (string?)i!["event"]));
        Assert.Equal(UuidV7.FromPrefixedId(sessionId), (string?)items[0]!["properties"]!["$session_id"]);
        Assert.Equal(UuidV7.FromPrefixedId(eventId), (string?)items[1]!["properties"]!["$ai_span_id"]);
    }

    // A line that an exporter writes when one of its requests ends, whatever the outcome.
    internal static bool IsExportOutcome(string line)
        => line.StartsWith("Successfully exported event to OTLP", StringComparison.Ordinal)
            || line.StartsWith("Telemetry export failed for ", StringComparison.Ordinal)
            || line.StartsWith("Datadog logs ", StringComparison.Ordinal)
            || line.StartsWith("Datadog metrics ", StringComparison.Ordinal)
            || line.StartsWith("Sentry log export", StringComparison.Ordinal)
            || line.StartsWith("Sentry transaction export", StringComparison.Ordinal)
            || line.StartsWith("Sentry error export", StringComparison.Ordinal)
            || line.StartsWith("Sentry export error", StringComparison.Ordinal)
            || line.StartsWith("PostHog export ", StringComparison.Ordinal);

    private static void AssertSuccessLines(IReadOnlyList<string> log, string eventId)
    {
        Assert.Single(log, "Successfully exported event to OTLP: " + eventId);
        Assert.Single(log, "Datadog logs success - Status: 202");
        Assert.Single(log, "Datadog metrics success - Status: 202");
        Assert.Single(log, "Sentry log export success - Event: " + eventId);
        Assert.Single(log, "Sentry transaction export success - Event: " + eventId);
        Assert.Single(log, "PostHog export success - Event: " + eventId);
        Assert.Equal(RequestsPerExport, log.Count(IsExportOutcome));
    }

    private static JsonNode OtlpSpan(RecordedRequest request)
        => FakeExporterHost.Json(request)["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!;

    // The string value of a span attribute; null when the span leaves the attribute out.
    private static string? OtlpAttribute(RecordedRequest request, string key)
        => (string?)OtlpSpan(request)["attributes"]!.AsArray().SingleOrDefault(a => (string?)a!["key"] == key)?["value"]?["stringValue"];

    private static JsonObject SentryLogAttributes(RecordedRequest request)
        => FakeExporterHost.Envelope(request)[2]["items"]![0]!["attributes"]!.AsObject();

    private static JsonObject PostHogCapture(RecordedRequest request)
        => FakeExporterHost.Json(request)["batch"]![0]!["properties"]!.AsObject();
}

// A stopped install sets EnabledGate.SkippedAnInstall for the whole process, so this test runs alone.
[Collection(SharedRuntime.Name)]
public sealed class ExporterTestRunE2ETests : IDisposable
{
    public ExporterTestRunE2ETests() => EnabledGate.ResetSkippedAnInstall();

    public void Dispose() => EnabledGate.ResetSkippedAnInstall();

    // In a customer's test run, the gate stops WithAgentCat before it claims the exporters. The lane's runtime hosts
    // exporters, yet the call sends no exporter request and no event, and AgentCat writes no line.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task A_test_run_sets_up_no_exporters(string lane)
    {
        using var host = new FakeExporterHost();
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, host.Lane(testRun: true));

        E2EAssert.Succeeded(await e2e.CallToolAsync("echo", """{"msg":"hi"}"""));

        await e2e.AssertEventCountAsync(0);
        Assert.True(e2e.Runtime.HostsExporters);
        Assert.False(e2e.Runtime.ExportersClaimed);
        Assert.True(EnabledGate.SkippedAnInstall);
        Assert.Empty(host.Requests);
        Assert.Empty(e2e.Log);
    }
}
