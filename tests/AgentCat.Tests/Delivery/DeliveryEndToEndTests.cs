using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Delivery;

public sealed class DeliveryEndToEndTests : IDisposable
{
    private readonly FakeAgentCatApi _fake = new();
    private readonly RecordingLog _log = new();
    private readonly ManualClock _clock = new();
    private readonly List<IDisposable> _disposables = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _clock.ReleaseDelays();
        _fake.Dispose();
    }

    private static AgentCatEvent ToolCall() => new()
    {
        SessionId = "ses_e2e",
        ProjectId = "proj_e2e",
        EventType = "mcp:tools/call",
        ResourceName = "lookup",
        Timestamp = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
        Parameters = new JsonObject { ["arguments"] = new JsonObject { ["query"] = "weather" } },
        Response = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "sunny" }) },
    };

    private EventPipeline Pipeline(TimeSpan? attemptTimeout = null)
    {
        var sender = new HttpEventSender(null, attemptTimeout);
        sender.SetBaseUrl(_fake.BaseUrl);
        var pipeline = new EventPipeline(sender, _log, null, _clock, () => { });
        _disposables.Add(pipeline);
        _disposables.Add(sender);
        return pipeline;
    }

    private async Task WaitForLineAsync(string prefix)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (!_log.Lines.Any(l => l.StartsWith(prefix, StringComparison.Ordinal)))
        {
            Assert.True(DateTime.UtcNow < deadline, "The log line did not appear within the bound: " + prefix);
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task A_tool_call_event_reaches_the_api_once()
    {
        _clock.HoldDelays();
        var pipeline = Pipeline();
        pipeline.Start();
        pipeline.Add(new QueuedEvent(ToolCall()));

        var flush = pipeline.FlushAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await FakeAgentCatApi.WithinAsync(flush, FakeAgentCatApi.WaitBound);

        Assert.True(await flush);
        var requests = _fake.Requests;
        var request = Assert.Single(requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/public/events/", request.Path);
        var body = JsonNode.Parse(Encoding.UTF8.GetString(request.Body))!.AsObject();
        Assert.StartsWith("evt_", (string)body["id"]!);
        Assert.Equal("mcp:tools/call", (string)body["event_type"]!);
        Assert.Contains(_log.Lines, l => l.StartsWith("Successfully sent event evt_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(503)]
    public async Task Each_failing_status_is_retried_three_times(int status)
    {
        _fake.Respond(status, status, status, status);
        var pipeline = Pipeline();
        pipeline.Start();
        pipeline.Add(new QueuedEvent(ToolCall()));

        await _fake.WaitForRequestsAsync(4);
        await WaitForLineAsync("Failed to deliver AgentCat event after retries:");

        Assert.Equal(4, _fake.Requests.Count);
        var failure = "HTTP " + status;
        var retries = _log.Lines.Where(l => l.StartsWith("Failed to send event evt_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, retries.Length);
        Assert.All(retries, l => Assert.EndsWith(", retrying... [Error: " + failure + "]", l, StringComparison.Ordinal));
        Assert.Contains("Failed to deliver AgentCat event after retries: " + failure, _log.Lines);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, _clock.Delays);
    }

    [Fact]
    public async Task An_event_type_outside_the_api_enum_is_attempted_once()
    {
        var e = ToolCall();
        e.EventType = "mcp:unknown";
        var pipeline = Pipeline();
        pipeline.Start();
        pipeline.Add(new QueuedEvent(e));

        await WaitForLineAsync("Failed to deliver AgentCat event after retries:");

        const string GaveUp = "Failed to deliver AgentCat event after retries: ";
        var gaveUp = Assert.Single(_log.Lines, l => l.StartsWith(GaveUp, StringComparison.Ordinal));
        var failure = gaveUp.Substring(GaveUp.Length);
        Assert.StartsWith("ArgumentException: ", failure, StringComparison.Ordinal);
        var attempt = Assert.Single(_log.Lines, l => l.StartsWith("Failed to send event evt_", StringComparison.Ordinal));
        Assert.EndsWith(", retrying... [Error: " + failure + "]", attempt, StringComparison.Ordinal);
        Assert.Empty(_clock.Delays);
        Assert.Empty(_fake.Requests);
    }

    [Fact]
    public async Task A_hung_api_times_out_each_attempt()
    {
        _fake.Respond(FakeAgentCatApi.Hang, FakeAgentCatApi.Hang, FakeAgentCatApi.Hang, FakeAgentCatApi.Hang);
        var pipeline = Pipeline(TimeSpan.FromMilliseconds(200));
        pipeline.Start();
        pipeline.Add(new QueuedEvent(ToolCall()));

        await WaitForLineAsync("Failed to deliver AgentCat event after retries:");

        Assert.Contains("Failed to deliver AgentCat event after retries: Request timed out after 200 ms", _log.Lines);
        var attempts = _log.Lines.Where(l => l.StartsWith("Failed to send event evt_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, attempts.Length);
        Assert.All(attempts, l => Assert.EndsWith(", retrying... [Error: Request timed out after 200 ms]", l, StringComparison.Ordinal));
        Assert.InRange(_fake.Requests.Count, 1, 4);
    }

    [Fact]
    public async Task Posts_carry_no_trace_context_and_start_no_http_activity()
    {
        var started = new List<string>();
        using var listener = new ActivityListener
        {
            // Only the sources this test observes: a listener on every source makes the MCP client start its own
            // activities and add trace context to the request _meta, which changes other tests that run alongside.
            ShouldListenTo = source => source.Name == "AgentCat.Tests.EndToEnd" || source.Name == "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                lock (started)
                {
                    started.Add(activity.OperationName);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        _clock.HoldDelays();
        var pipeline = Pipeline();
        using var source = new ActivitySource("AgentCat.Tests.EndToEnd");
        using var caller = source.StartActivity("customer-request");
        Assert.NotNull(caller);
        pipeline.Start();
        pipeline.Add(new QueuedEvent(ToolCall()));

        var flush = pipeline.FlushAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await FakeAgentCatApi.WithinAsync(flush, FakeAgentCatApi.WaitBound);
        Assert.True(await flush);

        var request = Assert.Single(_fake.Requests);
        Assert.Null(request.Header("traceparent"));
        Assert.Null(request.Header("tracestate"));
        Assert.Null(request.Header("Request-Id"));
#if NET
        lock (started)
        {
            Assert.DoesNotContain("System.Net.Http.HttpRequestOut", started);
        }
#endif
    }
}
