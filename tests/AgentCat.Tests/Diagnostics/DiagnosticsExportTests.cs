#pragma warning disable CA1861, CA1305, CA1872, CA1850, CA1837, CA2016

using System.Diagnostics;
using System.Net.Http;
using AgentCat.Internal;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol.Server;
using SdkDiagnostics = AgentCat.Internal.Diagnostics.Diagnostics;

namespace AgentCat.Tests.Diagnostics;

public class DiagnosticsExportTests
{
    internal static DiagnosticsHarness Forced() => new DiagnosticsHarness(inTestRun: true).ForceEnabled();

    // TS: sends the default bearer token by default
    [Fact]
    public async Task Sends_the_default_bearer_token_by_default()
    {
        var h = Forced().Started("proj_1");
        h.Write("Warning: something");

        await h.FlushAsync();

        var request = Assert.Single(h.Handler.Requests);
        Assert.Equal("Bearer dgk_sdk_diag_3f9a2c7e1b8d4065af2e9c1d7b6a4f80", request.Headers["Authorization"]);
    }

    // TS: honors a custom DIAGNOSTICS_TOKEN override (C# reads the token from AGENTCAT_DIAGNOSTICS_TOKEN)
    [Fact]
    public async Task Honors_a_custom_AGENTCAT_DIAGNOSTICS_TOKEN_override()
    {
        var h = Forced().Set("AGENTCAT_DIAGNOSTICS_TOKEN", "custom-token-123").Started("proj_1");
        h.Write("Warning: something");

        await h.FlushAsync();

        Assert.Equal("Bearer custom-token-123", Assert.Single(h.Handler.Requests).Headers["Authorization"]);
    }

    [Fact]
    public async Task Unprefixed_DIAGNOSTICS_TOKEN_is_ignored()
    {
        var h = Forced().Set("DIAGNOSTICS_TOKEN", "custom-token-123").Started("proj_1");
        h.Write("Warning: something");

        await h.FlushAsync();

        Assert.StartsWith("Bearer dgk_sdk_diag_", Assert.Single(h.Handler.Requests).Headers["Authorization"], StringComparison.Ordinal);
    }

    // TS: omits the Authorization header when token is explicitly empty (the body asserts the default is sent)
    [Fact]
    public async Task Empty_AGENTCAT_DIAGNOSTICS_TOKEN_sends_the_default_token()
    {
        var h = Forced().Set("AGENTCAT_DIAGNOSTICS_TOKEN", "").Started("proj_1");
        h.Write("Warning: something");

        await h.FlushAsync();

        Assert.StartsWith("Bearer dgk_sdk_diag_", Assert.Single(h.Handler.Requests).Headers["Authorization"], StringComparison.Ordinal);
    }

    // TS: POSTs buffered entries as OTLP logs JSON to /v1/logs
    [Fact]
    public async Task POSTs_buffered_entries_as_OTLP_logs_JSON_to_v1_logs()
    {
        var h = Forced().Started("proj_1");
        h.Write("Warning: something happened");

        await h.FlushAsync();

        var request = Assert.Single(h.Handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.EndsWith("/v1/logs", request.Url, StringComparison.Ordinal);
        Assert.Equal("https://otel.agentcat.com/v1/logs", request.Url);
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        Assert.Equal(SdkInfo.UserAgent, request.Headers["User-Agent"]);
        var record = request.Records.Single()!;
        Assert.Contains("something happened", (string)record["body"]!["stringValue"]!, StringComparison.Ordinal);
        Assert.Equal("proj_1", request.ResourceAttributes["agentcat.project_id"]);
        Assert.Equal(SdkInfo.Version, (string)request.Json["resourceLogs"]![0]!["scopeLogs"]![0]!["scope"]!["version"]!);
        Assert.Equal("agentcat-diagnostics", (string)request.Json["resourceLogs"]![0]!["scopeLogs"]![0]!["scope"]!["name"]!);
    }

    // TS: resolves even when fetch rejects (fire-and-forget isolation)
    [Fact]
    public async Task Resolves_even_when_the_POST_fails()
    {
        var h = Forced().Started("proj_1");
        h.Handler.Respond = (_, _) => throw new HttpRequestException("network down");
        h.Write("Failed to send event");

        await h.FlushAsync();

        Assert.Single(h.Handler.Requests);
    }

    // TS: does nothing when disabled
    [Fact]
    public async Task Does_nothing_when_disabled()
    {
        var h = Forced().Started("proj_1", disabled: true);
        h.Write("Warning: ignored");

        await h.FlushAsync();

        Assert.Empty(h.Handler.Requests);
        Assert.Equal(0, h.Diagnostics.BufferedCount);
    }

    [Fact]
    public async Task A_failed_post_logs_nothing_and_loses_the_batch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentcat-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "agentcat.log");
            var log = new ProcessLog(new FileLog(path));
            var h = Forced();
            var diagnostics = new SdkDiagnostics(
                name => name == "AGENTCAT_DISABLE_DIAGNOSTICS" ? "false" : null,
                () => true,
                h.Clock,
                log.SetDiagnosticsSink,
                h.Client,
                h.Timers.Start);
            diagnostics.Init("proj_1", false);
            h.Handler.Respond = (_, _) => throw new HttpRequestException("network down");
            log.Write("first");

            await diagnostics.FlushAsync(TimeSpan.FromSeconds(5));
            await diagnostics.FlushAsync(TimeSpan.FromSeconds(5));

            Assert.Single(h.Handler.Requests);
            Assert.Equal(0, diagnostics.BufferedCount);
            Assert.Single(File.ReadAllLines(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_post_that_hangs_ends_at_the_timeout()
    {
        var h = Forced().Started("proj_1");
        h.Handler.Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        };
        h.Write("Warning: slow");

        var flush = h.Diagnostics.FlushAsync(TimeSpan.FromMilliseconds(100));

        await FakeAgentCatApi.WithinAsync(flush, FakeAgentCatApi.WaitBound);
        Assert.True(flush.IsCompleted);
    }

    [Theory]
    [InlineData(null, "https://otel.agentcat.com/v1/logs")]
    [InlineData("", "https://otel.agentcat.com/v1/logs")]
    [InlineData("http://collector:4318", "http://collector:4318/v1/logs")]
    [InlineData("http://collector:4318/", "http://collector:4318/v1/logs")]
    [InlineData("http://collector:4318///", "http://collector:4318/v1/logs")]
    [InlineData("http://collector:4318/v1/logs", "http://collector:4318/v1/logs")]
    [InlineData("http://collector:4318/v1/logs/", "http://collector:4318/v1/logs")]
    public void Endpoint_trims_slashes_and_appends_v1_logs_once(string? configured, string expected)
    {
        Assert.Equal(expected, SdkDiagnostics.ResolveEndpoint(name => name == "AGENTCAT_DIAGNOSTICS_ENDPOINT" ? configured : null));
    }

    [Fact]
    public void Unprefixed_DIAGNOSTICS_ENDPOINT_is_ignored()
    {
        Assert.Equal("https://otel.agentcat.com/v1/logs", SdkDiagnostics.ResolveEndpoint(name => name == "DIAGNOSTICS_ENDPOINT" ? "http://other" : null));
    }

    [Fact]
    public async Task The_endpoint_is_read_at_each_flush()
    {
        var h = Forced().Started("proj_1");
        h.Write("one");
        await h.FlushAsync();
        h.Set("AGENTCAT_DIAGNOSTICS_ENDPOINT", "http://collector:4318/");
        h.Write("two");
        await h.FlushAsync();

        Assert.Equal(new[] { "https://otel.agentcat.com/v1/logs", "http://collector:4318/v1/logs" }, h.Handler.Requests.Select(r => r.Url).ToArray());
    }

    [Fact]
    public void Buffer_keeps_the_newest_1000()
    {
        var h = Forced().Started("proj_1");

        for (var i = 0; i < 1005; i++)
        {
            h.Write("line " + i);
        }

        var buffered = h.Diagnostics.Buffered;
        Assert.Equal(1000, buffered.Count);
        Assert.Equal("line 5", buffered[0].Body);
        Assert.Equal("line 1004", buffered[999].Body);
    }

    [Fact]
    public async Task Flush_sends_the_whole_buffer_once_and_empties_it()
    {
        var h = Forced().Started("proj_1");
        h.Write("one");
        h.Write("two");

        await h.FlushAsync();
        await h.FlushAsync();

        var request = Assert.Single(h.Handler.Requests);
        Assert.Equal(2, request.Records.Count);
        Assert.Equal(0, h.Diagnostics.BufferedCount);
    }

    [Fact]
    public async Task Timer_flushes_two_seconds_after_the_first_record()
    {
        var h = Forced().Started("proj_1");

        h.Write("one");
        h.Write("two");

        var timer = Assert.Single(h.Timers.Started);
        Assert.Equal(TimeSpan.FromSeconds(2), timer.Delay);
        Assert.Empty(h.Handler.Requests);

        timer.Callback();
        await WaitForRequestAsync(h.Handler);

        Assert.Equal(2, Assert.Single(h.Handler.Requests).Records.Count);
        h.Write("three");
        Assert.Equal(2, h.Timers.Started.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), h.Timers.Started[1].Delay);
    }

    [Fact]
    public async Task A_direct_flush_cancels_the_pending_timer()
    {
        var h = Forced().Started("proj_1");
        h.Write("one");

        await h.FlushAsync();

        Assert.True(Assert.Single(h.Timers.Started).Disposed);
    }

    [Fact]
    public async Task Flush_posts_no_traceparent_and_no_customer_activity()
    {
        using var api = new FakeAgentCatApi();
        using var source = new ActivitySource("customer.app");
        var seen = new List<Activity>();
        using var listener = Sample(source, seen);
        var h = new DiagnosticsHarness(
            inTestRun: false,
            useFakeTimers: false,
            batchDelay: TimeSpan.FromMilliseconds(50),
            client: AgentCatHttp.Client).Set("AGENTCAT_DIAGNOSTICS_ENDPOINT", api.BaseUrl).Started("proj_1");
        using var customer = source.StartActivity("customer request");
        Assert.NotNull(customer);

        h.Write("Warning: from a request thread");
        var requests = await api.WaitForRequestsAsync(1);

        var request = Assert.Single(requests);
        Assert.Equal("/v1/logs", request.Path);
        Assert.Null(request.Header("traceparent"));
        Assert.Null(request.Header("tracestate"));
        lock (seen)
        {
            Assert.DoesNotContain(seen, a => a != customer && a.Parent == customer);
            Assert.DoesNotContain(seen, a => a != customer && a.ParentId == customer.Id);
        }
    }

    [Fact]
    public async Task A_direct_flush_from_a_customer_context_posts_without_that_context()
    {
        using var api = new FakeAgentCatApi();
        using var source = new ActivitySource("customer.direct");
        var seen = new List<Activity>();
        using var listener = Sample(source, seen);
        var h = new DiagnosticsHarness(inTestRun: false, client: AgentCatHttp.Client).Set("AGENTCAT_DIAGNOSTICS_ENDPOINT", api.BaseUrl).Started("proj_1");
        using var customer = source.StartActivity("customer request");
        Assert.NotNull(customer);
        h.Write("Warning: shutdown");

        await h.Diagnostics.FlushAsync(TimeSpan.FromSeconds(5));

        var request = Assert.Single(await api.WaitForRequestsAsync(1));
        Assert.Null(request.Header("traceparent"));
        Assert.Same(customer, Activity.Current);
        lock (seen)
        {
            Assert.DoesNotContain(seen, a => a != customer && a.ParentId == customer.Id);
        }
    }

    [Fact]
    public async Task The_timer_flush_runs_without_the_context_of_the_first_line()
    {
        using var source = new ActivitySource("customer.timer");
        using var listener = Sample(source);
        var local = new AsyncLocal<string?>();
        var h = new DiagnosticsHarness(inTestRun: false, useFakeTimers: false, batchDelay: TimeSpan.FromMilliseconds(50)).ForceEnabled().Started("proj_1");
        var seen = new TaskCompletionSource<(string? Activity, string? Local)>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Handler.Respond = (_, _) =>
        {
            seen.TrySetResult((Activity.Current?.Id, local.Value));
            return Task.FromResult(new HttpResponseMessage());
        };
        using var customer = source.StartActivity("customer request");
        Assert.NotNull(customer);
        local.Value = "customer-async-local";

        h.Write("Warning: from a request thread");
        await FakeAgentCatApi.WithinAsync(seen.Task, FakeAgentCatApi.WaitBound);

        var (activity, localValue) = await seen.Task;
        Assert.Null(activity);
        Assert.Null(localValue);
    }

    [Fact]
    public async Task A_direct_flush_posts_without_the_current_activity_and_restores_it()
    {
        using var source = new ActivitySource("customer.shutdown");
        using var listener = Sample(source);
        var h = Forced().Started("proj_1");
        string? activityInPost = "unset";
        h.Handler.Respond = (_, _) =>
        {
            activityInPost = Activity.Current?.Id;
            return Task.FromResult(new HttpResponseMessage());
        };
        using var customer = source.StartActivity("customer request");
        Assert.NotNull(customer);
        h.Write("Warning: shutdown");

        await h.Diagnostics.FlushAsync(TimeSpan.FromSeconds(5));

        Assert.Null(activityInPost);
        Assert.Same(customer, Activity.Current);
    }

    // Samples the customer's source and HttpClient's, and records what starts there. Only those: a listener on every
    // source makes the MCP client start its own activities and add trace context to the request _meta, which changes
    // the MCP tests that run alongside.
    private static ActivityListener Sample(ActivitySource customer, List<Activity>? started = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == customer.Name || source.Name == "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (started is not null)
                {
                    lock (started)
                    {
                        started.Add(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // TS: captures and ships diagnostics when track() runs on an invalid server
    [Fact]
    public async Task Captures_and_ships_diagnostics_when_an_install_fails()
    {
        var h = Forced();
        var log = new ProcessLog(new FileLog(FileLog.DefaultPath()));
        var diagnostics = new SdkDiagnostics(
            name => name == "AGENTCAT_DISABLE_DIAGNOSTICS" ? "false" : null,
            () => true,
            h.Clock,
            log.SetDiagnosticsSink,
            h.Client,
            h.Timers.Start);
        var runtime = new AgentCatRuntime(log, new RecordingSender(), diagnostics: diagnostics);

        AgentCatEngine.Install(null, "proj_int", null, runtime, "options", _ => null);
        await diagnostics.FlushAsync(TimeSpan.FromSeconds(5));

        var request = Assert.Single(h.Handler.Requests);
        Assert.NotEmpty(request.Records);
        Assert.Contains("Failed to track server", (string)request.Records[0]!["body"]!["stringValue"]!, StringComparison.Ordinal);
        Assert.Equal("proj_int", request.ResourceAttributes["agentcat.project_id"]);
    }

    // TS: ships nothing when disabled via option
    [Fact]
    public async Task Ships_nothing_when_disabled_via_option()
    {
        var h = Forced();
        var runtime = RuntimeOf(h, out var log);

        AgentCatEngine.Install(null, "proj_int", new AgentCatOptions { DisableDiagnostics = true }, runtime, "options", _ => null);
        await runtime.Diagnostics.FlushAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(h.Handler.Requests);
        Assert.False(runtime.Diagnostics.IsEnabled);
        GC.KeepAlive(log);
    }

    [Fact]
    public void The_project_ID_comes_from_the_environment_when_the_argument_is_null()
    {
        var h = Forced();
        var runtime = RuntimeOf(h, out _);

        AgentCatEngine.Install(null, null, null, runtime, "options", name => name == "AGENTCAT_PROJECT_ID" ? "proj_env" : null);

        Assert.Equal("proj_env", runtime.Diagnostics.Resource.Single(a => a.Key == "agentcat.project_id").Value);
    }

    [Fact]
    public void An_install_that_passes_the_gate_starts_diagnostics()
    {
        var h = Forced();
        var runtime = RuntimeOf(h, out _, gate: name => name == "AGENTCAT_ENABLED" ? "true" : null);

        AgentCatEngine.Install(new McpServerOptions(), "proj_1", null, runtime, "options", _ => null);

        Assert.True(runtime.Diagnostics.IsEnabled);
    }

    [Fact]
    public async Task A_throwing_configure_callback_is_still_captured()
    {
        var h = Forced();
        var runtime = RuntimeOf(h, out _);
        var configure = new AgentCatPostConfigure(
            "proj_di",
            _ => throw new InvalidOperationException("configure boom"),
            () => runtime);

        configure.PostConfigure(null, new McpServerOptions());
        await runtime.Diagnostics.FlushAsync(TimeSpan.FromSeconds(5));

        var request = Assert.Single(h.Handler.Requests);
        Assert.Contains("Failed to track server - InvalidOperationException: configure boom", (string)request.Records[0]!["body"]!["stringValue"]!, StringComparison.Ordinal);
    }

    internal static AgentCatRuntime RuntimeOf(DiagnosticsHarness h, out ProcessLog log, Func<string, string?>? gate = null)
    {
        log = new ProcessLog(new FileLog(FileLog.DefaultPath()));
        var diagnostics = new SdkDiagnostics(
            name => name == "AGENTCAT_DISABLE_DIAGNOSTICS" ? "false" : null,
            () => true,
            h.Clock,
            log.SetDiagnosticsSink,
            h.Client,
            h.Timers.Start);
        var enabledGate = gate is null ? null : new EnabledGate(gate, () => false, log);
        return new AgentCatRuntime(log, new RecordingSender(), gate: enabledGate, diagnostics: diagnostics);
    }

    private static async Task WaitForRequestAsync(RecordingHandler handler)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (handler.Requests.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}

// A stopped install sets EnabledGate.SkippedAnInstall for the whole process, so this test runs alone.
[Collection(SharedRuntime.Name)]
public sealed class DiagnosticsGateTests : IDisposable
{
    public DiagnosticsGateTests() => EnabledGate.ResetSkippedAnInstall();

    public void Dispose() => EnabledGate.ResetSkippedAnInstall();

    [Fact]
    public void An_install_stopped_by_the_gate_starts_no_diagnostics()
    {
        var h = DiagnosticsExportTests.Forced();
        var runtime = DiagnosticsExportTests.RuntimeOf(h, out _, gate: name => name == "AGENTCAT_ENABLED" ? "false" : null);

        var tracker = AgentCatEngine.Install(new McpServerOptions(), "proj_1", null, runtime, "options", _ => null);

        Assert.Null(tracker);
        runtime.Log.Write("Warning: after the stopped install");
        Assert.False(runtime.Diagnostics.IsEnabled);
        Assert.Equal(0, runtime.Diagnostics.BufferedCount);
    }
}
