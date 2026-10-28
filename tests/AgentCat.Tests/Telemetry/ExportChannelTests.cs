using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using AgentCat.Internal;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Lifecycle;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Telemetry;

public sealed class ExportChannelTests
{
    internal static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"city\":\"Zürich\"}");

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private readonly RecordingLog _log = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static ExportRequest Request(string path, Action<ExportResponse>? completed = null)
        => new("http://exporter.test" + path, "application/json", Body) { Completed = completed };

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Dropped(int count) => "WARN: AgentCat telemetry export queue full, dropped the oldest " + count + " request(s)";

    // Fills the channel: MaxInFlight requests in flight and MaxWaiting waiting.
    private static void Fill(ExportChannel channel)
    {
        for (var i = 1; i <= ExportChannel.MaxInFlight + ExportChannel.MaxWaiting; i++)
        {
            channel.Send(Request("/" + i));
        }
    }

    private static async Task<T> WithinBoundAsync<T>(Task<T> task)
    {
        await FakeAgentCatApi.WithinAsync(task, Bound);
        return await task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold within the bound.");
            await Task.Delay(10, Ct);
        }
    }

    private static ShutdownDeadline RealTime(TimeSpan events, TimeSpan diagnostics)
    {
        var clock = Stopwatch.StartNew();
        return new ShutdownDeadline(events, diagnostics, () => clock.Elapsed);
    }

    private static AgentCatEvent Event() => new()
    {
        SessionId = "s1",
        ProjectId = "proj_test",
        EventType = "mcp:tools/call",
        Timestamp = new DateTimeOffset(2025, 6, 15, 12, 0, 5, 123, TimeSpan.Zero),
    };

    [Fact]
    public async Task Requests_go_out_with_the_user_agent_and_content_type()
    {
        using var api = new FakeAgentCatApi();
        var channel = new ExportChannel(_log, SystemPipelineClock.Instance);
        var answered = Completion<ExportResponse>();

        channel.Send(new ExportRequest(api.BaseUrl + "/v1/traces", "application/json", Body) { Completed = r => answered.TrySetResult(r) });

        var request = Assert.Single(await api.WaitForRequestsAsync(1));
        var response = await WithinBoundAsync(answered.Task);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/traces", request.Path);
        Assert.Equal(SdkInfo.UserAgent, request.Header("User-Agent"));
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.True(request.HadContentLength);
        Assert.Null(request.Header("Transfer-Encoding"));
        Assert.Null(request.Header("Expect"));
        Assert.Null(request.Header("traceparent"));
        Assert.Equal(Body, request.Body);
        Assert.Equal(202, response.Status);
        Assert.Equal("X", response.StatusText);
        Assert.True(response.Ok);
        Assert.Null(response.Error);
        Assert.Null(response.Exception);
        Assert.Null(response.Body);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task Request_headers_replace_the_user_agent_and_content_type()
    {
        using var api = new FakeAgentCatApi();
        var channel = new ExportChannel(_log, SystemPipelineClock.Instance);
        var answered = Completion<ExportResponse>();

        channel.Send(new ExportRequest(api.BaseUrl + "/v1/traces", "application/json", Body)
        {
            Headers = new[]
            {
                new KeyValuePair<string, string>("user-agent", "customer-agent/1.0"),
                new KeyValuePair<string, string>("Content-Type", "application/x-protobuf"),
                new KeyValuePair<string, string>("X-Api-Key", "secret, with a comma"),
                new KeyValuePair<string, string>("Bad Name", "left out"),
            },
            Completed = r => answered.TrySetResult(r),
        });

        var request = Assert.Single(await api.WaitForRequestsAsync(1));
        Assert.True((await WithinBoundAsync(answered.Task)).Ok);
        Assert.Single(request.Headers, h => string.Equals(h.Key, "User-Agent", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("customer-agent/1.0", request.Header("User-Agent"));
        Assert.Single(request.Headers, h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("application/x-protobuf", request.Header("Content-Type"));
        Assert.Equal("secret, with a comma", request.Header("X-Api-Key"));
        Assert.Null(request.Header("Bad Name"));
        Assert.Equal(Body, request.Body);
    }

    [Fact]
    public async Task The_body_of_a_failure_is_read_only_when_asked_for()
    {
        var handler = new RecordingHttpHandler
        {
            Respond = (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                return Task.FromResult(new HttpResponseMessage(path == "/ok" ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(path == "/large" ? new string('x', 70_000) : "{\"error\":\"bad key\"}"),
                });
            },
        };
        var channel = new ExportChannel(_log, SystemPipelineClock.Instance, handler);
        var responses = new ConcurrentDictionary<string, ExportResponse>();
        void Send(string path, bool reads)
            => channel.Send(new ExportRequest("http://exporter.test" + path, "application/json", Body)
            {
                ReadsFailureBody = reads,
                Completed = r => responses[path + " " + reads] = r,
            });

        Send("/bad", true);
        Send("/bad", false);
        Send("/ok", true);
        Send("/large", true);

        Assert.True(await WithinBoundAsync(channel.WaitIdleAsync(Bound, Ct)));
        var bad = responses["/bad True"];
        Assert.Equal(400, bad.Status);
        Assert.Equal("Bad Request", bad.StatusText);
        Assert.False(bad.Ok);
        Assert.Null(bad.Error);
        Assert.Equal("{\"error\":\"bad key\"}", bad.Body);
        Assert.Null(responses["/bad False"].Body);
        Assert.True(responses["/ok True"].Ok);
        Assert.Null(responses["/ok True"].Body);
        Assert.Equal(new string('x', ExportChannel.MaxFailureBodyBytes), responses["/large True"].Body);
    }

    [Fact]
    public async Task A_request_times_out_after_10_seconds()
    {
        var clock = new ManualClock();
        clock.HoldDelays();
        var handler = new RecordingHttpHandler
        {
            Respond = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        var channel = new ExportChannel(_log, clock, handler);
        var answered = Completion<ExportResponse>();

        channel.Send(Request("/slow", r => answered.TrySetResult(r)));
        await WaitUntilAsync(() => handler.Count == 1);

        Assert.Equal(new[] { TimeSpan.FromSeconds(10) }, clock.Delays);
        Assert.False(answered.Task.IsCompleted);

        clock.ReleaseDelays();

        var response = await WithinBoundAsync(answered.Task);
        Assert.Equal(0, response.Status);
        Assert.False(response.Ok);
        Assert.Equal("Request timed out after 10000 ms", response.Error);
        Assert.IsType<TimeoutException>(response.Exception);
        Assert.Equal("Request timed out after 10000 ms", response.Exception!.Message);
        Assert.True(handler.Requests[0].CancellationToken.IsCancellationRequested, "The timed-out request was not aborted.");
        Assert.True(await WithinBoundAsync(channel.WaitIdleAsync(Bound, Ct)));

        // An answer before the timeout stops the timer.
        handler.Respond = async (_, _) =>
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var fast = Completion<ExportResponse>();
        channel.Send(Request("/fast", r => fast.TrySetResult(r)));

        Assert.Equal(200, (await WithinBoundAsync(fast.Task)).Status);
        Assert.True(await WithinBoundAsync(channel.WaitIdleAsync(Bound, Ct)));
        Assert.Equal(0, clock.HeldCount);
    }

    [Fact]
    public async Task Exporter_requests_are_bounded_and_the_oldest_are_dropped()
    {
        var clock = new ManualClock();
        clock.HoldDelays();
        var gate = Completion<bool>();
        var handler = RecordingHttpHandler.Gated(gate);
        var channel = new ExportChannel(_log, clock, handler);
        var completed = new ConcurrentQueue<string>();
        void Send(int number)
        {
            var path = "/" + number;
            channel.Send(Request(path, _ => completed.Enqueue(path)));
        }

        for (var i = 1; i <= ExportChannel.MaxInFlight + ExportChannel.MaxWaiting; i++)
        {
            Send(i);
        }

        await WaitUntilAsync(() => handler.Count == 10);
        Assert.Equal(10, channel.InFlightCount);
        Assert.Equal(1000, channel.WaitingCount);
        Assert.Empty(_log.Lines);

        Send(1011);

        Assert.Equal(10, channel.InFlightCount);
        Assert.Equal(1000, channel.WaitingCount);
        Assert.Equal(new[] { Dropped(1) }, _log.Lines);
        await Task.Delay(50, Ct);
        Assert.Equal(10, handler.Count);
        Assert.Empty(completed);

        gate.SetResult(true);

        Assert.True(await WithinBoundAsync(channel.WaitIdleAsync(Bound, Ct)));
        var sent = handler.Requests.Select(r => r.Path).ToArray();
        Assert.Equal(1010, sent.Length);
        Assert.Equal(1010, sent.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 10).Select(i => "/" + i), sent.Take(10));
        Assert.DoesNotContain("/11", sent);
        Assert.Contains("/12", sent);
        Assert.Contains("/1011", sent);
        Assert.Equal(1010, completed.Count);
        Assert.DoesNotContain("/11", completed);
        Assert.Equal(new[] { Dropped(1) }, _log.Lines);
    }

    [Fact]
    public async Task Send_never_blocks_or_throws()
    {
        var clock = new ManualClock();
        clock.HoldDelays();
        var silent = RecordingHttpHandler.Silent();
        var full = new ExportChannel(_log, clock, silent);

        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 3000; i++)
        {
            full.Send(Request("/" + i));
        }

        elapsed.Stop();
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), "3,000 sends took " + elapsed.Elapsed);
        Assert.Equal(10, full.InFlightCount);
        Assert.Equal(1000, full.WaitingCount);

        // The burst wrote one line at its first drop; the other 1,989 drops are reported when the queue drains.
        Assert.Equal(new[] { Dropped(1) }, _log.Lines);
        silent.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        clock.ReleaseDelays();
        Assert.True(await WithinBoundAsync(full.WaitIdleAsync(Bound, Ct)));
        Assert.Equal(new[] { Dropped(1), Dropped(1989) }, _log.Lines);

        // A log that throws on the drop line.
        var throwingLog = new RecordingLog { ThrowWhen = _ => true };
        var overflowing = new ExportChannel(throwingLog, clock, RecordingHttpHandler.Silent());
        Assert.Null(Record.Exception(() =>
        {
            for (var i = 0; i < 1011; i++)
            {
                overflowing.Send(Request("/" + i));
            }
        }));
        Assert.Equal(1000, overflowing.WaitingCount);

        // No request, a URL that is not one, a handler that throws, a callback that throws.
        var errors = new ConcurrentQueue<string>();
        var throwing = new RecordingHttpHandler { Respond = (_, _) => throw new InvalidOperationException("handler boom") };
        var failing = new ExportChannel(throwingLog, SystemPipelineClock.Instance, throwing);
        Assert.Null(Record.Exception(() => failing.Send(null!)));
        Assert.Null(Record.Exception(() => failing.Send(new ExportRequest("not a url", "application/json", Body) { Completed = r => errors.Enqueue(r.Error!) })));
        Assert.Null(Record.Exception(() => failing.Send(Request("/a", r =>
        {
            errors.Enqueue(r.Error!);
            throw new InvalidOperationException("callback boom");
        }))));
        Assert.Null(Record.Exception(() => failing.Send(Request("/b", r => errors.Enqueue(r.Error!)))));

        Assert.True(await WithinBoundAsync(failing.WaitIdleAsync(Bound, Ct)));
        Assert.Equal(3, errors.Count);
        Assert.Single(errors, e => e.StartsWith("UriFormatException: ", StringComparison.Ordinal));
        Assert.Equal(2, errors.Count(e => e == "InvalidOperationException: handler boom"));
        Assert.Equal(2, throwing.Count);
    }

    [Fact]
    public void Drops_within_a_second_share_one_line()
    {
        var clock = new ManualClock();
        clock.HoldDelays();
        var channel = new ExportChannel(_log, clock, RecordingHttpHandler.Silent());
        Fill(channel);
        var start = clock.UtcNow;

        // The first drop is reported at once, the next four wait for the second to pass.
        for (var i = 0; i < 5; i++)
        {
            channel.Send(Request("/burst"));
        }

        Assert.Equal(new[] { Dropped(1) }, _log.Lines);

        clock.UtcNow = start + TimeSpan.FromMilliseconds(999);
        channel.Send(Request("/late"));
        Assert.Equal(new[] { Dropped(1) }, _log.Lines);

        clock.UtcNow = start + TimeSpan.FromSeconds(1);
        channel.Send(Request("/after a second"));
        Assert.Equal(new[] { Dropped(1), Dropped(6) }, _log.Lines);

        // A clock that steps back counts as a second passed, so the line never stalls.
        clock.UtcNow = start - TimeSpan.FromHours(1);
        channel.Send(Request("/after a step back"));
        Assert.Equal(new[] { Dropped(1), Dropped(6), Dropped(1) }, _log.Lines);

        channel.Send(Request("/same instant"));
        Assert.Equal(3, _log.Lines.Count);
        Assert.Equal(1000, channel.WaitingCount);
    }

    [Fact]
    public async Task Pending_drops_are_reported_when_the_queue_drains()
    {
        var clock = new ManualClock();
        clock.HoldDelays();
        var gate = Completion<bool>();
        var channel = new ExportChannel(_log, clock, RecordingHttpHandler.Gated(gate));
        Fill(channel);
        for (var i = 0; i < 3; i++)
        {
            channel.Send(Request("/overflow"));
        }

        Assert.Equal(new[] { Dropped(1) }, _log.Lines);

        gate.SetResult(true);

        Assert.True(await WithinBoundAsync(channel.WaitIdleAsync(Bound, Ct)));
        Assert.Equal(new[] { Dropped(1), Dropped(2) }, _log.Lines);

        // Nothing dropped since: the next time the channel drains, it writes nothing.
        channel.Send(Request("/after"));
        Assert.True(await WithinBoundAsync(channel.WaitIdleAsync(Bound, Ct)));
        Assert.Equal(new[] { Dropped(1), Dropped(2) }, _log.Lines);
    }

    [Fact]
    public async Task A_recording_handler_sees_every_header()
    {
        var handler = new RecordingHttpHandler();
        var channel = new ExportChannel(_log, SystemPipelineClock.Instance, handler);
        var answered = Completion<ExportResponse>();

        channel.Send(new ExportRequest("http://exporter.test/api/42/envelope/", "application/x-sentry-envelope", Body)
        {
            Headers = new[]
            {
                new KeyValuePair<string, string>("X-Sentry-Auth", "Sentry sentry_version=7, sentry_client=agentcat/1.0.0, sentry_key=abc"),
                new KeyValuePair<string, string>("DD-API-KEY", "dd-key"),
            },
            Completed = r => answered.TrySetResult(r),
        });

        Assert.True((await WithinBoundAsync(answered.Task)).Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(SdkInfo.UserAgent, request.Header("User-Agent"));
        Assert.Equal("application/x-sentry-envelope", request.Header("content-type"));
        Assert.Equal("Sentry sentry_version=7, sentry_client=agentcat/1.0.0, sentry_key=abc", request.Header("X-Sentry-Auth"));
        Assert.Equal("dd-key", request.Header("DD-API-KEY"));
        Assert.Null(request.Header("Authorization"));
        Assert.Contains(request.Headers, h => h.Key == "X-Sentry-Auth");
    }

    [Fact]
    public async Task Requests_start_without_the_callers_execution_context()
    {
        using var source = new ActivitySource("customer.exporter");
        using var listener = new ActivityListener
        {
            // Only the customer's source: a listener on every source makes the MCP client start its own activities and
            // add trace context to the request _meta, which changes the MCP tests that run alongside.
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var local = new AsyncLocal<string?>();
        var inHandler = Completion<(string? Activity, string? Local)>();
        var inCallback = Completion<(string? Activity, string? Local)>();

        // The request ends only after Send has returned. A request that ends before runs its callback inside Send, on
        // the caller's thread.
        var sendReturned = Completion<bool>();
        var handler = new RecordingHttpHandler
        {
            Respond = async (_, _) =>
            {
                await sendReturned.Task;
                inHandler.TrySetResult((Activity.Current?.Id, local.Value));
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        var channel = new ExportChannel(_log, SystemPipelineClock.Instance, handler);
        using var customer = source.StartActivity("customer request");
        Assert.NotNull(customer);
        local.Value = "customer";

        channel.Send(Request("/context", _ => inCallback.TrySetResult((Activity.Current?.Id, local.Value))));
        sendReturned.TrySetResult(true);

        Assert.Same(customer, Activity.Current);
        var seenByHandler = await WithinBoundAsync(inHandler.Task);
        var seenByCallback = await WithinBoundAsync(inCallback.Task);
        Assert.Null(seenByHandler.Activity);
        Assert.Null(seenByHandler.Local);
        Assert.Null(seenByCallback.Activity);
        Assert.Null(seenByCallback.Local);
    }

    // A request that ends at once (here its URL is not one) runs its callback inside Send, on the caller's thread. The
    // callback still sees no Activity of the caller.
    [Fact]
    public void A_request_that_ends_inside_Send_runs_its_callback_without_the_callers_activity()
    {
        using var source = new ActivitySource("customer.exporter.inline");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var channel = new ExportChannel(_log, SystemPipelineClock.Instance, new RecordingHttpHandler());
        using var customer = source.StartActivity("customer request");
        Assert.NotNull(customer);
        var local = new AsyncLocal<string?>();
        local.Value = "customer";
        var ran = false;
        string? seen = "unset";
        string? seenLocal = null;
        ExportResponse? seenResponse = null;
        var callerThread = Environment.CurrentManagedThreadId;
        var callbackThread = 0;

        channel.Send(new ExportRequest("not a url", "application/json", Body)
        {
            Completed = response =>
            {
                ran = true;
                seen = Activity.Current?.Id;
                seenLocal = local.Value;
                seenResponse = response;
                callbackThread = Environment.CurrentManagedThreadId;
            },
        });

        Assert.True(ran, "The callback did not run before Send returned.");
        Assert.Equal(callerThread, callbackThread);
        Assert.Null(seen);
        Assert.Equal("customer", seenLocal);
        Assert.NotNull(seenResponse);
        Assert.False(seenResponse.Ok);
        Assert.Equal(0, seenResponse.Status);
        Assert.Same(customer, Activity.Current);
        Assert.Equal(0, channel.InFlightCount);
    }

    [Fact]
    public async Task FlushAsync_waits_for_exporter_requests()
    {
        var clock = new ManualClock();
        clock.HoldDelays();
        var gate = Completion<bool>();
        var handler = RecordingHttpHandler.Gated(gate);
        var sender = new RecordingSender();
        var runtime = new AgentCatRuntime(_log, sender, clock: clock, exportHandler: handler);
        var answered = Completion<ExportResponse>();
        Assert.True(runtime.Pipeline.TrySetExporter(new ChannelExporter(runtime.Exports, r => answered.TrySetResult(r))));
        try
        {
            runtime.Pipeline.Add(new QueuedEvent(Event()));
            await WaitUntilAsync(() => handler.Count == 1 && sender.Calls == 1 && runtime.Pipeline.ActiveCount == 0);

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var flush = runtime.Pipeline.FlushAsync(TimeSpan.FromSeconds(5), cancel.Token);
            await Task.Delay(100, Ct);
            Assert.False(flush.IsCompleted, "The flush returned while an exporter request was in flight.");
            cancel.Cancel();
            Assert.False(await WithinBoundAsync(flush));

            var second = runtime.Pipeline.FlushAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.False(second.IsCompleted);
            gate.SetResult(true);

            Assert.True(await WithinBoundAsync(second));
            Assert.True(answered.Task.IsCompleted, "The flush returned before the exporter request ended.");
            Assert.Equal(200, (await answered.Task).Status);
            Assert.True(await runtime.Pipeline.FlushAsync(TimeSpan.Zero, Ct));

            // One timer per request and one per flush.
            Assert.Equal(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) }, clock.Delays);
        }
        finally
        {
            runtime.Pipeline.Dispose();
        }
    }

    [Fact]
    public async Task The_stop_deadline_covers_exporter_requests()
    {
        // A request that ends within the stop window ends before the stop returns.
        var gate = Completion<bool>();
        var answering = new AgentCatRuntime(
            new RecordingLog(),
            new RecordingSender(),
            exportHandler: RecordingHttpHandler.Gated(gate),
            shutdownDeadline: RealTime(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(200)));
        var answered = Completion<ExportResponse>();
        answering.Exports.Send(Request("/v1/traces", r => answered.TrySetResult(r)));

        var exit = Task.Run(() => ShutdownHandlers.OnProcessExit(answering), Ct);
        await Task.Delay(200, Ct);
        Assert.False(exit.IsCompleted, "The stop returned while an exporter request was in flight.");
        gate.SetResult(true);

        await FakeAgentCatApi.WithinAsync(exit, Bound);
        Assert.True(answered.Task.IsCompleted, "The stop returned before the exporter request ended.");

        // A request that never ends holds the stop until the deadline, not past it.
        var silent = new AgentCatRuntime(
            new RecordingLog(),
            new RecordingSender(),
            exportHandler: RecordingHttpHandler.Silent(),
            shutdownDeadline: RealTime(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(200)));
        silent.Exports.Send(Request("/v1/traces"));

        var elapsed = Stopwatch.StartNew();
        ShutdownHandlers.OnProcessExit(silent);
        elapsed.Stop();

        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(550), "The stop did not wait for the exporter request: " + elapsed.Elapsed);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), "The stop waited past the deadline: " + elapsed.Elapsed);
        Assert.Equal(1, silent.Exports.InFlightCount);
    }

    private sealed class ChannelExporter(ExportChannel channel, Action<ExportResponse> completed) : IEventExporter
    {
        public Task ExportAsync(AgentCatEvent e, CancellationToken cancellationToken)
        {
            channel.Send(Request("/" + e.SessionId, completed));
            return Task.CompletedTask;
        }
    }
}

// The process-wide runtimes: the shared one and a fake's.
[Collection(SharedRuntime.Name)]
public sealed class RuntimeExportChannelTests
{
    [Fact]
    public void The_shared_runtime_sends_exporter_requests_through_the_SDK_client()
    {
        Assert.Same(AgentCatHttp.Client, AgentCatRuntime.Shared.Exports.Client);
    }

    [Fact]
    public async Task A_fakes_runtime_sends_no_exporter_requests()
    {
        using var fake = AgentCatSdk.Fake();
        var completed = 0;

        fake.Runtime.Exports.Send(ExportChannelTests.Request("/v1/traces", _ => Interlocked.Increment(ref completed)));

        Assert.True(await fake.Runtime.Exports.WaitIdleAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.Equal(0, fake.Runtime.Exports.InFlightCount + fake.Runtime.Exports.WaitingCount);
        Assert.Equal(0, Volatile.Read(ref completed));
        Assert.Null(fake.Runtime.Exports.Client);
    }
}
