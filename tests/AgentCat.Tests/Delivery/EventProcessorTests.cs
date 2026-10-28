using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Events;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Logging;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Delivery;

public class EventProcessorTests
{
    private const string Secret = "TOP_SECRET_PAYLOAD_abc123";
    private const string Prepare = "prepare";
    private const string Hook = "hook";
    private const string Strings = "strings";
    private const string Sanitize = "sanitize";
    private const string Truncate = "truncate";
    private const string Send = "send";
    private const string Export = "export";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private readonly RecordingLog _log = new();
    private readonly ManualClock _clock = new();
    private readonly RecordingSender _sender = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentCatEvent Event() => new()
    {
        SessionId = "test-session",
        ProjectId = "test-project",
        EventType = "mcp:tools/call",
        Timestamp = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
    };

    private static JsonNode Sent(string json) => JsonNode.Parse(json)!;

    private static void AssertSequence<T>(IEnumerable<T> actual, params T[] expected) => Assert.Equal(expected, actual);

    private static string RetryLine(string id, string message) => "Failed to send event " + id + ", retrying... [Error: " + message + "]";

    private static string SuccessLine(AgentCatEvent e)
        => "Successfully sent event " + e.Id + " | mcp:tools/call | session test-session | test-project | undefined ms | anonymous";

    private static async Task CompletesWithinBoundAsync(Task task)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var winner = await Task.WhenAny(task, Task.Delay(Bound, cts.Token));
        cts.Cancel();
        Assert.Same(task, winner);
        await task;
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

    private EventProcessor Processor(int maxRetries = 3, Action<AgentCatEvent>? sanitize = null, Action<AgentCatEvent>? truncate = null)
        => new(_sender, _log, _clock, maxRetries, sanitize, truncate);

    [Fact]
    public async Task Should_apply_the_redactEvent_option_from_tracking_data_to_published_events()
    {
        var e = Event();
        e.ResourceName = "do_thing";
        e.Parameters = new JsonObject { ["text"] = "raw sensitive value" };
        var calls = new List<(string? ResourceName, string? Parameters)>();
        var item = new QueuedEvent(e)
        {
            RedactEvent = (x, _) =>
            {
                calls.Add((x.ResourceName, x.Parameters?.ToJsonString()));
                x.Parameters = new JsonObject { ["text"] = "[EVENT-REDACTED]" };
                return new ValueTask<AgentCatEvent?>(x);
            },
        };

        await Processor().ProcessAsync(item, Ct);

        var call = Assert.Single(calls);
        Assert.Equal("do_thing", call.ResourceName);
        Assert.Equal("{\"text\":\"raw sensitive value\"}", call.Parameters);
        var sent = Sent(Assert.Single(_sender.SentJson));
        Assert.Equal("{\"text\":\"[EVENT-REDACTED]\"}", sent["parameters"]!.ToJsonString());
    }

    [Fact]
    public async Task Should_drop_the_event_when_the_hook_returns_null()
    {
        var item = new QueuedEvent(Event()) { RedactEvent = (_, _) => new ValueTask<AgentCatEvent?>((AgentCatEvent?)null) };

        await Processor().ProcessAsync(item, Ct);

        Assert.Empty(_sender.Sent);
        AssertSequence(_log.Lines, "Event dropped by redactEvent hook");
    }

    [Fact]
    public async Task The_success_line_is_routine_for_the_host_logger_whatever_the_actor_is_called()
    {
        var e = Event();
        e.IdentifyActorGivenId = "error_bot";

        await Processor().ProcessAsync(new QueuedEvent(e), Ct);

        var (line, severity) = Assert.Single(_log.Written);
        Assert.StartsWith("Successfully sent event ", line, StringComparison.Ordinal);
        Assert.Equal(LogSeverity.Information, severity);
    }

    [Fact]
    public async Task Should_drop_the_event_when_the_hook_throws()
    {
        var item = new QueuedEvent(Event()) { RedactEvent = (_, _) => throw new InvalidOperationException("hook exploded") };

        await Processor().ProcessAsync(item, Ct);

        Assert.Empty(_sender.Sent);
        var line = Assert.Single(_log.Lines);
        Assert.StartsWith("Failed to redact event (event-level hook): ", line, StringComparison.Ordinal);
        Assert.Equal("Failed to redact event (event-level hook): InvalidOperationException: hook exploded", line);
    }

    [Fact]
    public async Task Should_run_the_event_hook_before_string_redaction_and_compose_both()
    {
        var e = Event();
        e.UserIntent = "raw secret";
        var seenByEventHook = new List<string?>();
        var item = new QueuedEvent(e)
        {
            RedactEvent = (x, _) =>
            {
                seenByEventHook.Add(x.UserIntent);
                x.UserIntent += " (reviewed)";
                return new ValueTask<AgentCatEvent?>(x);
            },
            RedactStrings = (text, _) => new ValueTask<string>(text.Replace("secret", "[REDACTED]")),
        };

        await Processor().ProcessAsync(item, Ct);

        AssertSequence(seenByEventHook, "raw secret");
        var sent = Sent(Assert.Single(_sender.SentJson));
        Assert.Equal("raw [REDACTED] (reviewed)", (string?)sent["userIntent"]);
    }

    [Fact]
    public async Task Logs_only_event_metadata_on_successful_send_never_the_payload()
    {
        var e = new AgentCatEvent
        {
            SessionId = "ses_test",
            ProjectId = "proj_test",
            EventType = "mcp:tools/call",
            ResourceName = "do_thing",
            Duration = 12,
            Timestamp = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
            Parameters = JsonNode.Parse("{\"request\":{\"params\":{\"arguments\":{\"apiKey\":\"" + Secret + "\"}}}}"),
            Response = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Secret }) },
            UserIntent = Secret,
        };

        await Processor().ProcessAsync(new QueuedEvent(e), Ct);

        var sent = Assert.Single(_sender.Sent);
        Assert.Matches("^evt_[0-9A-Za-z]{27}$", sent.Id);
        Assert.Contains(Secret, Assert.Single(_sender.SentJson), StringComparison.Ordinal);
        AssertSequence(
            _log.Lines,
            "Successfully sent event " + sent.Id + " | mcp:tools/call | session ses_test | proj_test | 12 ms | anonymous");
        Assert.All(_log.Lines, l => Assert.DoesNotContain("Event details", l, StringComparison.Ordinal));
        Assert.All(_log.Lines, l => Assert.DoesNotContain(Secret, l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settles_fail_open_when_AgentCat_ingestion_exhausts_retries()
    {
        _sender.Fail("ingestion unavailable");

        await CompletesWithinBoundAsync(Processor(maxRetries: 0).ProcessAsync(new QueuedEvent(Event()), Ct));

        var sent = Assert.Single(_sender.Sent);
        Assert.Empty(_clock.Delays);
        Assert.Contains(_log.Lines, l => l.StartsWith("Failed to deliver AgentCat event after retries", StringComparison.Ordinal));
        AssertSequence(
            _log.Lines,
            RetryLine(sent.Id, "ingestion unavailable"), "Failed to deliver AgentCat event after retries: ingestion unavailable");
    }

    [Fact]
    public async Task Does_not_hold_AgentCat_delivery_open_for_custom_exporters()
    {
        var telemetry = new TaskCompletionSource<bool>();
        var exports = 0;
        var processor = Processor();
        Assert.True(processor.TrySetExporter(new DelegateExporter((_, _) =>
        {
            exports++;
            return telemetry.Task;
        })));

        try
        {
            await CompletesWithinBoundAsync(processor.ProcessAsync(new QueuedEvent(Event()), Ct));

            Assert.Equal(1, exports);
            Assert.Single(_sender.Sent);
            Assert.False(telemetry.Task.IsCompleted);
            AssertSequence(_log.Lines, SuccessLine(_sender.Sent[0]));
        }
        finally
        {
            telemetry.TrySetResult(true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Telemetry_only_events_are_exported_once_and_never_sent(string? projectId)
    {
        var exported = new List<AgentCatEvent>();
        var processor = Processor();
        processor.TrySetExporter(new DelegateExporter((x, _) =>
        {
            exported.Add(x);
            return Task.CompletedTask;
        }));
        var e = Event();
        e.ProjectId = projectId;

        await processor.ProcessAsync(new QueuedEvent(e), Ct);

        var export = Assert.Single(exported);
        Assert.Same(e, export);
        Assert.Matches("^evt_[0-9A-Za-z]{27}$", export.Id);
        Assert.Empty(_sender.Sent);
        Assert.Empty(_clock.Delays);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task Retries_three_times_with_1_2_4_second_waits()
    {
        _sender.Fail("HTTP 503", times: 4);

        await Processor().ProcessAsync(new QueuedEvent(Event()), Ct);

        Assert.Equal(4, _sender.Calls);
        AssertSequence(_clock.Delays, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        var retry = RetryLine(_sender.Sent[0].Id, "HTTP 503");
        Assert.Matches("^Failed to send event evt_[0-9A-Za-z]{27}, retrying\\.\\.\\. \\[Error: HTTP 503\\]$", retry);
        AssertSequence(
            _log.Lines,
            retry, retry, retry, retry, "Failed to deliver AgentCat event after retries: HTTP 503");
    }

    [Fact]
    public async Task A_failure_that_is_not_retryable_ends_after_one_attempt()
    {
        const string Failure = "ArgumentException: The event type is not one the API accepts.";
        var exports = 0;
        var processor = Processor();
        processor.TrySetExporter(new DelegateExporter((_, _) =>
        {
            exports++;
            return Task.CompletedTask;
        }));
        _sender.Throw(() => new EventSendException(Failure, new ArgumentException("The event type is not one the API accepts."), retryable: false));

        await processor.ProcessAsync(new QueuedEvent(Event()), Ct);

        Assert.Equal(1, _sender.Calls);
        Assert.Equal(1, exports);
        Assert.Empty(_clock.Delays);
        AssertSequence(
            _log.Lines,
            RetryLine(_sender.Sent[0].Id, Failure), "Failed to deliver AgentCat event after retries: " + Failure);
    }

    [Fact]
    public async Task Exporter_runs_before_every_attempt()
    {
        var order = new List<string>();
        var processor = Processor();
        processor.TrySetExporter(new DelegateExporter((_, _) =>
        {
            order.Add(Export);
            return Task.CompletedTask;
        }));
        _sender.Fail("HTTP 503", times: 2);
        _sender.OnSend = _ => order.Add(Send);

        await processor.ProcessAsync(new QueuedEvent(Event()), Ct);

        AssertSequence(order, Export, Send, Export, Send, Export, Send);
        AssertSequence(_clock.Delays, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Assert.Equal(SuccessLine(_sender.Sent[0]), _log.Lines[_log.Lines.Count - 1]);
    }

    [Fact]
    public async Task Exporter_that_throws_synchronously_does_not_stop_delivery()
    {
        var processor = Processor();
        processor.TrySetExporter(new DelegateExporter((_, _) => throw new InvalidOperationException("exporter broke")));

        await processor.ProcessAsync(new QueuedEvent(Event()), Ct);

        var sent = Assert.Single(_sender.Sent);
        AssertSequence(
            _log.Lines,
            "Telemetry export error: InvalidOperationException: exporter broke", SuccessLine(sent));
    }

    [Fact]
    public async Task Exporter_that_returns_a_null_task_does_not_stop_delivery()
    {
        var exports = 0;
        var processor = Processor();
        processor.TrySetExporter(new DelegateExporter((_, _) =>
        {
            exports++;
            return null!;
        }));
        _sender.Fail("HTTP 503");

        await processor.ProcessAsync(new QueuedEvent(Event()), Ct);

        Assert.Equal(2, exports);
        Assert.Equal(2, _sender.Calls);
        AssertSequence(_log.Lines, RetryLine(_sender.Sent[0].Id, "HTTP 503"), SuccessLine(_sender.Sent[0]));
    }

    [Fact]
    public async Task Exporter_faults_are_logged_whenever_they_happen_and_cancellations_are_not()
    {
        var late = new TaskCompletionSource<bool>();
        var results = new Queue<Task>(new[]
        {
            Task.FromException(new InvalidOperationException("export failed")),
            late.Task,
            Task.FromCanceled(new CancellationToken(canceled: true)),
        });
        var processor = Processor();
        processor.TrySetExporter(new DelegateExporter((_, _) => results.Dequeue()));

        for (var i = 0; i < 3; i++)
        {
            await processor.ProcessAsync(new QueuedEvent(Event()), Ct);
        }

        late.SetException(new TimeoutException("export timed out"));
        await WaitUntilAsync(() => _log.Lines.Count == 5);

        Assert.Equal(3, _sender.Calls);
        AssertSequence(
            _log.Lines.Where(l => l.StartsWith("Telemetry export error: ", StringComparison.Ordinal)),
            "Telemetry export error: InvalidOperationException: export failed", "Telemetry export error: TimeoutException: export timed out");
    }

    [Fact]
    public async Task Prepare_failure_is_logged_and_processing_continues()
    {
        var item = new QueuedEvent(Event()) { Prepare = (_, _) => throw new InvalidOperationException("pending broke") };

        await Processor().ProcessAsync(item, Ct);

        var sent = Assert.Single(_sender.Sent);
        AssertSequence(
            _log.Lines,
            "Failed to resolve pending hook results: InvalidOperationException: pending broke", SuccessLine(sent));
    }

    [Fact]
    public async Task Stages_run_in_TS_order()
    {
        var order = new List<string>();
        var redactorSaw = new List<string>();
        string? hookSawResponse = null;
        var e = Event();
        e.UserIntent = "secret " + new string('x', 3000);
        var item = new QueuedEvent(e)
        {
            Prepare = (x, _) =>
            {
                order.Add(Prepare);
                x.Response = JsonNode.Parse("{\"content\":[{\"type\":\"image\",\"data\":\"AAAA\",\"mimeType\":\"image/png\"}]}");
                return default;
            },
            RedactEvent = (x, _) =>
            {
                order.Add(Hook);
                hookSawResponse = x.Response?.ToJsonString();
                x.UserIntent += " (reviewed)";
                return new ValueTask<AgentCatEvent?>(x);
            },
            RedactStrings = (text, _) =>
            {
                if (order[order.Count - 1] != Strings)
                {
                    order.Add(Strings);
                }

                redactorSaw.Add(text);
                return new ValueTask<string>(text.Replace("secret", "[R]"));
            },
        };
        _sender.OnSend = _ => order.Add(Send);

        await Processor().ProcessAsync(item, Ct);

        AssertSequence(order, Prepare, Hook, Strings, Send);
        Assert.Equal("{\"content\":[{\"type\":\"image\",\"data\":\"AAAA\",\"mimeType\":\"image/png\"}]}", hookSawResponse);
        Assert.Contains("AAAA", redactorSaw);
        Assert.All(redactorSaw, s => Assert.DoesNotContain("redacted", s, StringComparison.Ordinal));
        var sent = Sent(Assert.Single(_sender.SentJson));
        Assert.Equal(
            "{\"content\":[{\"type\":\"text\",\"text\":\"[image content redacted - not supported by AgentCat]\"}]}",
            sent["response"]!.ToJsonString());
        var intent = (string)sent["userIntent"]!;
        Assert.Equal(EventTruncator.MaxUserIntentLength + EventTruncator.Suffix.Length, intent.Length);
        Assert.StartsWith("[R] xxx", intent, StringComparison.Ordinal);
        Assert.EndsWith("xxx...", intent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Strings, "Failed to redact event: InvalidOperationException: strings broke")]
    [InlineData(Sanitize, "Failed to sanitize event: InvalidOperationException: sanitize broke")]
    [InlineData(Truncate, "Failed to truncate event: InvalidOperationException: truncate broke")]
    public async Task Failing_stages_log_their_TS_line_and_send_nothing(string stage, string expected)
    {
        var exports = 0;
        var processor = Processor(
            sanitize: stage == Sanitize ? _ => throw new InvalidOperationException("sanitize broke") : null,
            truncate: stage == Truncate ? _ => throw new InvalidOperationException("truncate broke") : null);
        processor.TrySetExporter(new DelegateExporter((_, _) =>
        {
            exports++;
            return Task.CompletedTask;
        }));
        var e = Event();
        e.UserIntent = "intent";
        var item = new QueuedEvent(e)
        {
            RedactStrings = stage == Strings ? (_, _) => throw new InvalidOperationException("strings broke") : null,
        };

        await processor.ProcessAsync(item, Ct);

        var line = Assert.Single(_log.Lines);
        Assert.StartsWith(expected.Substring(0, expected.IndexOf(": ", StringComparison.Ordinal) + 2), line, StringComparison.Ordinal);
        Assert.Equal(expected, line);
        Assert.Empty(_sender.Sent);
        Assert.Equal(0, exports);
    }

    [Fact]
    public async Task Id_is_assigned_after_truncation_and_kept_when_set()
    {
        _clock.UtcNow = new DateTimeOffset(2025, 3, 1, 10, 20, 30, 789, TimeSpan.Zero);
        var idsAtTruncation = new List<string>();
        var processor = Processor(truncate: x =>
        {
            idsAtTruncation.Add(x.Id);
            EventTruncator.Truncate(x);
        });
        var preset = Event();
        preset.Id = "evt_preset";

        await processor.ProcessAsync(new QueuedEvent(Event()), Ct);
        await processor.ProcessAsync(new QueuedEvent(preset), Ct);

        AssertSequence(idsAtTruncation, "", "evt_preset");
        var assigned = _sender.Sent[0].Id;
        Assert.Matches("^evt_[0-9A-Za-z]{27}$", assigned);
        Assert.Equal(new DateTimeOffset(2025, 3, 1, 10, 20, 30, TimeSpan.Zero), Ksuid.Parse(assigned.Substring(4)).Date);
        Assert.Equal("evt_preset", _sender.Sent[1].Id);
    }

    [Fact]
    public async Task Success_line_prints_undefined_for_a_missing_duration_and_the_actor_when_identified()
    {
        var identified = Event();
        identified.Id = "evt_1";
        identified.SessionId = "ses_1";
        identified.ProjectId = "proj_1";
        identified.IdentifyActorGivenId = "user_42";
        var emptyActor = Event();
        emptyActor.Id = "evt_2";
        emptyActor.Duration = 7;
        emptyActor.IdentifyActorGivenId = "";
        var processor = Processor();

        await processor.ProcessAsync(new QueuedEvent(identified), Ct);
        await processor.ProcessAsync(new QueuedEvent(emptyActor), Ct);

        AssertSequence(
            _log.Lines,
            "Successfully sent event evt_1 | mcp:tools/call | session ses_1 | proj_1 | undefined ms | user_42",
            "Successfully sent event evt_2 | mcp:tools/call | session test-session | test-project | 7 ms | anonymous");
    }

    [Fact]
    public async Task Cancellation_stops_the_retry_wait()
    {
        _sender.Fail("HTTP 503");
        _clock.HoldDelays();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var task = Processor().ProcessAsync(new QueuedEvent(Event()), cts.Token);
        await WaitUntilAsync(() => _clock.HeldCount == 1);

        Assert.False(task.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        AssertSequence(_clock.Delays, TimeSpan.FromSeconds(1));
        var sent = Assert.Single(_sender.Sent);
        AssertSequence(_log.Lines, RetryLine(sent.Id, "HTTP 503"));
    }

    [Theory]
    [InlineData(Prepare)]
    [InlineData(Hook)]
    [InlineData(Strings)]
    [InlineData(Send)]
    public async Task Cancellation_surfaces_from_awaited_stages_without_a_log_line(string stage)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var e = Event();
        e.UserIntent = "intent";
        var item = new QueuedEvent(e)
        {
            Prepare = stage == Prepare ? (_, token) => CancelAndThrow(cts, token) : null,
            RedactEvent = stage == Hook ? (_, token) => CancelAndThrowEvent(cts, token) : null,
            RedactStrings = stage == Strings ? (_, token) => CancelAndThrowString(cts, token) : null,
        };
        if (stage == Send)
        {
            _sender.Hang();
        }

        var task = Processor().ProcessAsync(item, cts.Token);
        if (stage == Send)
        {
            await WaitUntilAsync(() => _sender.HangingCount == 1);
            Assert.False(task.IsCompleted);
            cts.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(_log.Lines);
        Assert.Equal(stage == Send ? 1 : 0, _sender.Calls);
    }

    [Fact]
    public async Task A_send_canceled_by_its_own_timeout_is_a_failed_attempt()
    {
        _sender.Throw(() => new TaskCanceledException("A task was canceled."));

        await Processor().ProcessAsync(new QueuedEvent(Event()), Ct);

        Assert.Equal(2, _sender.Calls);
        AssertSequence(_clock.Delays, TimeSpan.FromSeconds(1));
        AssertSequence(
            _log.Lines,
            RetryLine(_sender.Sent[0].Id, "TaskCanceledException: A task was canceled."), SuccessLine(_sender.Sent[0]));
    }

    [Fact]
    public async Task A_throwing_success_line_does_not_resend_a_delivered_event()
    {
        var processor = new EventProcessor(_sender, new SuccessThrowingLog(_log), _clock);

        await CompletesWithinBoundAsync(processor.ProcessAsync(new QueuedEvent(Event()), Ct));

        var sent = Assert.Single(_sender.Sent);
        Assert.Empty(_clock.Delays);
        Assert.Single(_log.Lines, l => l == SuccessLine(sent));
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Failed to send event ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Processing_waits_for_a_hanging_send_until_it_completes()
    {
        _sender.Hang();

        var task = Processor().ProcessAsync(new QueuedEvent(Event()), Ct);
        await WaitUntilAsync(() => _sender.HangingCount == 1);

        Assert.False(task.IsCompleted);
        Assert.Empty(_log.Lines);
        _sender.Release();
        await CompletesWithinBoundAsync(task);
        AssertSequence(_log.Lines, SuccessLine(_sender.Sent[0]));
    }

    [Fact]
    public void TrySetExporter_is_first_wins()
    {
        var processor = Processor();
        var first = new DelegateExporter((_, _) => Task.CompletedTask);

        Assert.True(processor.TrySetExporter(first));
        Assert.False(processor.TrySetExporter(new DelegateExporter((_, _) => Task.CompletedTask)));
        Assert.False(processor.TrySetExporter(first));
    }

    private static ValueTask CancelAndThrow(CancellationTokenSource cts, CancellationToken token)
    {
        cts.Cancel();
        token.ThrowIfCancellationRequested();
        return default;
    }

    private static ValueTask<AgentCatEvent?> CancelAndThrowEvent(CancellationTokenSource cts, CancellationToken token)
    {
        cts.Cancel();
        token.ThrowIfCancellationRequested();
        return default;
    }

    private static ValueTask<string> CancelAndThrowString(CancellationTokenSource cts, CancellationToken token)
    {
        cts.Cancel();
        token.ThrowIfCancellationRequested();
        return default;
    }

    private sealed class DelegateExporter(Func<AgentCatEvent, CancellationToken, Task> export) : IEventExporter
    {
        public Task ExportAsync(AgentCatEvent e, CancellationToken cancellationToken) => export(e, cancellationToken);
    }

    private sealed class SuccessThrowingLog(RecordingLog inner) : IAgentCatLog
    {
        public void Write(string message)
        {
            inner.Write(message);
            if (message.StartsWith("Successfully sent event ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("log broke");
            }
        }

        public void Write(string message, LogSeverity hostSeverity) => Write(message);
    }
}
