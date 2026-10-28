using System.Collections.Concurrent;
using System.Diagnostics;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Logging;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Delivery;

public sealed class EventPipelineTests : IDisposable
{
    private void AssertHostSeverity(LogSeverity expected) => Assert.All(_log.Written, w => Assert.Equal(expected, w.Severity));

    private const string QueueFullLine = "Event queue full, dropping oldest event";
    private const string ShuttingDownLine = "Queue is shutting down, event dropped";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    // Longer than Bound, so the test's own bounded waits fail first when the pool is slow.
    private static readonly TimeSpan WarmUpGateBound = TimeSpan.FromSeconds(15);
    private static readonly AsyncLocal<string?> Ambient = new();

    private readonly RecordingLog _log = new();
    private readonly ManualClock _clock = new();
    private readonly RecordingSender _sender = new();
    private readonly List<EventPipeline> _pipelines = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var pipeline in _pipelines)
        {
            pipeline.Dispose();
        }

        _sender.Release();
        _clock.ReleaseDelays();
    }

    private static AgentCatEvent Event(string? resourceName = null) => new()
    {
        SessionId = "test-session",
        ProjectId = "test-project",
        EventType = "mcp:tools/call",
        ResourceName = resourceName,
        Timestamp = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
    };

    private static QueuedEvent Item(string? resourceName = null) => new(Event(resourceName));

    private static void AssertSequence<T>(IEnumerable<T> actual, params T[] expected) => Assert.Equal(expected, actual);

    private static T Last<T>(IReadOnlyList<T> items) => items[items.Count - 1];

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

    private EventPipeline Pipeline(PipelineSettings? settings = null, Action? warmUp = null)
    {
        var pipeline = new EventPipeline(_sender, _log, settings, _clock, warmUp ?? (() => { }));
        _pipelines.Add(pipeline);
        return pipeline;
    }

    [Fact]
    public async Task Should_be_accessible_and_have_required_methods()
    {
        var pipeline = Pipeline();
        Action<QueuedEvent> add = pipeline.Add;
        Func<TimeSpan, CancellationToken, Task<bool>> flush = pipeline.FlushAsync;
        Func<Task> shutdown = pipeline.ShutdownAsync;

        add(Item());
        Assert.InRange(pipeline.QueueLength, 0, 1);
        Assert.InRange(pipeline.ActiveCount, 0, 1);
        await flush(TimeSpan.Zero, TestContext.Current.CancellationToken);
        await shutdown();
    }

    [Fact]
    public void Should_track_queue_stats_correctly()
    {
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 0 });
        Assert.Equal(0, pipeline.QueueLength);
        Assert.Equal(0, pipeline.ActiveCount);

        pipeline.Add(Item());
        pipeline.Add(Item());

        Assert.Equal(2, pipeline.QueueLength);
        Assert.Equal(0, pipeline.ActiveCount);
    }

    [Fact]
    public async Task Should_be_able_to_add_events_directly()
    {
        var pipeline = Pipeline();

        Assert.Null(Record.Exception(() => pipeline.Add(Item("test-tool"))));

        await WaitUntilAsync(() => _sender.Calls == 1);
        Assert.Equal("test-tool", _sender.Sent[0].ResourceName);
    }

    [Fact]
    public async Task Should_handle_destroy_method_without_errors()
    {
        var pipeline = Pipeline();

        Assert.Null(await Record.ExceptionAsync(pipeline.ShutdownAsync));

        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task Should_prevent_adding_events_after_destroy()
    {
        var pipeline = Pipeline();
        await pipeline.ShutdownAsync();

        pipeline.Add(Item());

        AssertSequence(_log.Lines, ShuttingDownLine);
        AssertHostSeverity(LogSeverity.Warning);
        Assert.Equal(0, pipeline.QueueLength);
        Assert.Equal(0, _sender.Calls);
    }

    [Fact]
    public async Task Should_handle_multiple_events()
    {
        var pipeline = Pipeline();

        for (var i = 0; i < 5; i++)
        {
            pipeline.Add(Item("test-tool-" + i));
        }

        await WaitUntilAsync(() => _sender.Calls == 5);
        AssertSequence(
            _sender.Sent.Select(e => e.ResourceName).OrderBy(n => n, StringComparer.Ordinal),
            "test-tool-0",
            "test-tool-1",
            "test-tool-2",
            "test-tool-3",
            "test-tool-4");
    }

    [Fact]
    public void Settles_an_event_when_queue_overflow_drops_it()
    {
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 0, MaxQueueSize = 1 });

        pipeline.Add(Item("dropped"));
        pipeline.Add(Item("retained"));

        AssertSequence(_log.Lines, QueueFullLine);
        AssertHostSeverity(LogSeverity.Warning);
        Assert.Equal(1, pipeline.QueueLength);
    }

    [Fact]
    public async Task Overflow_calls_Discard_on_the_oldest_waiting_event_only()
    {
        _sender.Hang();
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 1, MaxQueueSize = 2 });
        var discarded = new ConcurrentQueue<string>();
        QueuedEvent Tracked(string name) => new(Event(name)) { Discard = () => discarded.Enqueue(name) };
        pipeline.Add(Tracked("a"));
        await WaitUntilAsync(() => _sender.HangingCount == 1);

        pipeline.Add(Tracked("b"));
        pipeline.Add(Tracked("c"));
        pipeline.Add(Tracked("d"));

        AssertSequence(discarded, "b");
        _sender.Release();
        await WaitUntilAsync(() => _sender.Calls == 3 && pipeline.ActiveCount == 0);
        AssertSequence(discarded, "b");
    }

    [Fact]
    public async Task Events_added_after_shutdown_call_Discard()
    {
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 0 });
        await pipeline.ShutdownAsync();
        var discarded = 0;

        pipeline.Add(new QueuedEvent(Event()) { Discard = () => Interlocked.Increment(ref discarded) });

        Assert.Equal(1, Volatile.Read(ref discarded));
        AssertSequence(_log.Lines, ShuttingDownLine);
    }

    [Fact]
    public void Dispose_calls_Discard_on_every_waiting_event()
    {
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 0 });
        var discarded = new List<string>();
        pipeline.Add(new QueuedEvent(Event()) { Discard = () => discarded.Add("a") });
        pipeline.Add(new QueuedEvent(Event()) { Discard = () => discarded.Add("b") });

        pipeline.Dispose();
        pipeline.Dispose();

        AssertSequence(discarded, "a", "b");
    }

    [Fact]
    public void A_throwing_Discard_in_Dispose_still_discards_the_later_events()
    {
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 0 });
        var discarded = new List<string>();
        pipeline.Add(new QueuedEvent(Event()) { Discard = () => throw new InvalidOperationException("discard boom") });
        pipeline.Add(new QueuedEvent(Event()) { Discard = () => discarded.Add("b") });
        pipeline.Add(new QueuedEvent(Event()) { Discard = () => discarded.Add("c") });

        var thrown = Record.Exception(pipeline.Dispose);

        Assert.Null(thrown);
        AssertSequence(discarded, "b", "c");
    }

    [Fact]
    public async Task A_reader_never_prepares_an_event_that_Dispose_already_discarded()
    {
        _sender.Hang();
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 1 });
        var prepared = new ConcurrentQueue<string>();
        var discarded = new ConcurrentQueue<string>();
        QueuedEvent Tracked(string name, Action? onDiscard = null) => new(Event(name))
        {
            Prepare = (e, _) =>
            {
                prepared.Enqueue(name);
                return default;
            },
            Discard = () =>
            {
                discarded.Enqueue(name);
                onDiscard?.Invoke();
            },
        };
        pipeline.Add(new QueuedEvent(Event("a")));
        await WaitUntilAsync(() => _sender.HangingCount == 1);
        pipeline.Add(Tracked("b", () =>
        {
            _sender.Release();
            SpinWait.SpinUntil(() => prepared.Contains("b"), TimeSpan.FromMilliseconds(500));
        }));
        pipeline.Add(Tracked("c"));

        pipeline.Dispose();
        await Task.Delay(100, Ct);

        AssertSequence(discarded, "b", "c");
        Assert.Empty(prepared);
    }

    [Fact]
    public async Task A_throwing_Discard_is_contained()
    {
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 0, MaxQueueSize = 1 });
        var later = 0;
        pipeline.Add(new QueuedEvent(Event()) { Discard = () => throw new InvalidOperationException("discard boom") });

        var thrown = Record.Exception(() => pipeline.Add(Item()));
        pipeline.Add(new QueuedEvent(Event()) { Discard = () => Interlocked.Increment(ref later) });
        pipeline.Dispose();
        await pipeline.ShutdownAsync();

        Assert.Null(thrown);
        Assert.Equal(1, Volatile.Read(ref later));
    }

    [Fact]
    public async Task Events_that_are_processed_do_not_call_Discard()
    {
        _clock.HoldDelays();
        var pipeline = Pipeline();
        var discarded = 0;

        pipeline.Add(new QueuedEvent(Event()) { Discard = () => Interlocked.Increment(ref discarded) });
        Assert.True(await pipeline.FlushAsync(Bound, TestContext.Current.CancellationToken));
        pipeline.Dispose();

        Assert.Equal(1, _sender.Calls);
        Assert.Equal(0, Volatile.Read(ref discarded));
    }

    [Fact]
    public async Task Overflow_drops_the_oldest_waiting_event()
    {
        _sender.Hang();
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 1, MaxQueueSize = 2 });
        pipeline.Add(Item("a"));
        await WaitUntilAsync(() => _sender.HangingCount == 1);

        pipeline.Add(Item("b"));
        pipeline.Add(Item("c"));
        pipeline.Add(Item("d"));

        Assert.Single(_log.Lines, l => l == QueueFullLine);
        Assert.Equal(2, pipeline.QueueLength);
        _sender.Release();
        await WaitUntilAsync(() => _sender.Calls == 3 && pipeline.ActiveCount == 0);
        AssertSequence(_sender.Sent.Select(e => e.ResourceName), "a", "c", "d");
    }

    [Fact]
    public async Task Flush_waits_until_the_send_completes()
    {
        var pipeline = Pipeline();
        Assert.True(await pipeline.FlushAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        _sender.Hang();
        pipeline.Add(Item());
        Assert.False(await pipeline.FlushAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromSeconds(1), Last(_clock.Delays));

        await WaitUntilAsync(() => _sender.HangingCount == 1);
        _clock.HoldDelays();
        var flush = pipeline.FlushAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(flush.IsCompleted);
        Assert.Equal(1, _clock.HeldCount);

        _sender.Release();

        await CompletesWithinBoundAsync(flush);
        Assert.True(await flush);
        Assert.Equal(0, _clock.HeldCount);
    }

    [Theory]
    [InlineData(-TimeSpan.TicksPerMillisecond)]
    [InlineData(int.MaxValue * TimeSpan.TicksPerMillisecond)]
    [InlineData(uint.MaxValue * TimeSpan.TicksPerMillisecond)]
    [InlineData(long.MaxValue)]
    public async Task Flush_budgets_past_the_timer_range_wait_until_drained(long budgetTicks)
    {
        _sender.Hang();
        var pipeline = Pipeline();
        pipeline.Add(Item());
        await WaitUntilAsync(() => _sender.HangingCount == 1);

        var flush = pipeline.FlushAsync(TimeSpan.FromTicks(budgetTicks), TestContext.Current.CancellationToken);
        Assert.False(flush.IsCompleted);

        _sender.Release();

        await CompletesWithinBoundAsync(flush);
        Assert.True(await flush);
        Assert.Empty(_clock.Delays);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(-1L, 0L)]
    [InlineData(-2 * TimeSpan.TicksPerMillisecond, 0L)]
    [InlineData(-5 * TimeSpan.TicksPerSecond, 0L)]
    [InlineData(long.MinValue, 0L)]
    [InlineData((int.MaxValue - 1L) * TimeSpan.TicksPerMillisecond, (int.MaxValue - 1L) * TimeSpan.TicksPerMillisecond)]
    public async Task Finite_flush_budgets_reach_the_clock_and_negative_ones_count_as_zero(long budgetTicks, long expectedTicks)
    {
        _sender.Hang();
        var pipeline = Pipeline();
        pipeline.Add(Item());
        await WaitUntilAsync(() => _sender.HangingCount == 1);

        Assert.False(await pipeline.FlushAsync(TimeSpan.FromTicks(budgetTicks), TestContext.Current.CancellationToken));
        AssertSequence(_clock.Delays, TimeSpan.FromTicks(expectedTicks));
    }

    [Fact]
    public async Task The_system_clock_takes_the_largest_budgets()
    {
        _sender.Hang(2);
        var pipeline = new EventPipeline(
            _sender, _log, new PipelineSettings { ShutdownTimeout = TimeSpan.MaxValue }, SystemPipelineClock.Instance, () => { });
        _pipelines.Add(pipeline);
        pipeline.Add(Item());
        await WaitUntilAsync(() => _sender.HangingCount == 1);

        var flush = pipeline.FlushAsync(TimeSpan.MaxValue, TestContext.Current.CancellationToken);
        Assert.False(flush.IsCompleted);
        _sender.Release();
        await CompletesWithinBoundAsync(flush);
        Assert.True(await flush);

        pipeline.Add(Item());
        await WaitUntilAsync(() => _sender.HangingCount == 1);
        var shutdown = pipeline.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        _sender.Release();
        await CompletesWithinBoundAsync(shutdown);
        Assert.Equal(2, _sender.Calls);
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Shutting down with", StringComparison.Ordinal));
    }

    [Fact]
    public async Task At_most_five_events_are_in_process()
    {
        _sender.Hang(5);
        var pipeline = Pipeline();

        for (var i = 0; i < 7; i++)
        {
            pipeline.Add(Item());
        }

        await WaitUntilAsync(() => _sender.HangingCount == 5 && pipeline.ActiveCount == 5 && pipeline.QueueLength == 2);
        Assert.Equal(5, _sender.Calls);

        _sender.Release();

        await WaitUntilAsync(() => _sender.Calls == 7 && pipeline.ActiveCount == 0);
        Assert.Equal(0, pipeline.QueueLength);
    }

    [Fact]
    public async Task Add_returns_before_any_stage_runs()
    {
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var prepareThread = 0;
        var item = new QueuedEvent(Event())
        {
            Prepare = (_, token) =>
            {
                prepareThread = Environment.CurrentManagedThreadId;
                entered.Set();
                gate.Wait(Bound, token);
                return default;
            },
        };
        var pipeline = Pipeline();

        try
        {
            var watch = Stopwatch.StartNew();
            pipeline.Add(item);
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "Add took " + watch.Elapsed + ".");
            Assert.True(entered.Wait(Bound, Ct));
            Assert.NotEqual(Environment.CurrentManagedThreadId, prepareThread);
        }
        finally
        {
            gate.Set();
        }

        await WaitUntilAsync(() => _sender.Calls == 1);
    }

    [Fact]
    public async Task Pipeline_work_does_not_inherit_the_callers_execution_context()
    {
        var seen = new ConcurrentQueue<(string Where, string? Local, Activity? Activity)>();
        _sender.OnSend = _ => seen.Enqueue(("send", Ambient.Value, Activity.Current));
        QueuedEvent Tracked() => new(Event())
        {
            Prepare = (_, _) =>
            {
                seen.Enqueue(("prepare", Ambient.Value, Activity.Current));
                return default;
            },
        };
        var pipeline = Pipeline();

        Ambient.Value = "customer";
        using var activity = new Activity("customer-request").Start();
        try
        {
            pipeline.Add(Tracked());
            await WaitUntilAsync(() => _sender.Calls == 1);
            pipeline.Add(Tracked());
            await WaitUntilAsync(() => _sender.Calls == 2);

            Assert.Equal("customer", Ambient.Value);
            Assert.Same(activity, Activity.Current);
        }
        finally
        {
            activity.Stop();
            Ambient.Value = null;
        }

        AssertSequence(seen.Select(s => s.Where), "prepare", "send", "prepare", "send");
        Assert.All(seen, s =>
        {
            Assert.Null(s.Local);
            Assert.Null(s.Activity);
        });
    }

    [Fact]
    public async Task Outage_keeps_the_queue_bounded_and_Add_fast()
    {
        _sender.Hang(5);
        var pipeline = Pipeline();
        var items = Enumerable.Range(0, 20000).Select(_ => Item()).ToArray();

        var watch = Stopwatch.StartNew();
        foreach (var item in items)
        {
            pipeline.Add(item);
        }

        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "20,000 adds took " + watch.Elapsed + ".");
        await WaitUntilAsync(() => pipeline.ActiveCount == 5 && _sender.HangingCount == 5);
        var overflow = _log.Lines.Count(l => l == QueueFullLine);
        Assert.InRange(pipeline.QueueLength, 0, 10000);
        Assert.Equal(20000, pipeline.QueueLength + pipeline.ActiveCount + overflow);
        Assert.Equal(5, _sender.Calls);
    }

    [Fact]
    public async Task Shutdown_returns_within_its_budget_when_sends_hang()
    {
        _sender.Hang(5);
        var pipeline = Pipeline();
        for (var i = 0; i < 8; i++)
        {
            pipeline.Add(Item());
        }

        await WaitUntilAsync(() => pipeline.ActiveCount == 5 && pipeline.QueueLength == 3);
        _clock.HoldDelays();

        var shutdown = pipeline.ShutdownAsync();
        var again = pipeline.ShutdownAsync();

        AssertSequence(_clock.Delays, TimeSpan.FromSeconds(5));
        Assert.False(shutdown.IsCompleted);
        Assert.False(again.IsCompleted);

        _clock.ReleaseDelays();

        await CompletesWithinBoundAsync(shutdown);
        await CompletesWithinBoundAsync(again);
        AssertSequence(_log.Lines, "Shutting down with 3 events still in queue");
        AssertHostSeverity(LogSeverity.Warning);

        pipeline.Add(Item());

        Assert.Equal(ShuttingDownLine, Last(_log.Lines));
        Assert.Equal(3, pipeline.QueueLength);
        Assert.Equal(5, _sender.Calls);
    }

    [Fact]
    public async Task Shutdown_drains_waiting_events_first()
    {
        _sender.Hang();
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 1 });
        for (var i = 0; i < 3; i++)
        {
            pipeline.Add(Item());
        }

        await WaitUntilAsync(() => _sender.HangingCount == 1 && pipeline.QueueLength == 2);
        _clock.HoldDelays();

        var shutdown = pipeline.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);

        _sender.Release();

        await CompletesWithinBoundAsync(shutdown);
        Assert.Equal(3, _sender.Calls);
        Assert.Equal(0, pipeline.QueueLength);
        Assert.Equal(0, _clock.HeldCount);
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Shutting down with", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_warms_app_libraries_off_the_calling_thread()
    {
        var warming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var warmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var warmThread = 0;
        var warmCount = 0;
        _sender.OnSend = _ => sent.TrySetResult(true);
        var pipeline = Pipeline(warmUp: () =>
        {
            warmThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref warmCount);
            warming.TrySetResult(true);
            release.Wait(WarmUpGateBound);
            warmed.TrySetResult(true);
        });

        var callingThread = 0;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            callingThread = Environment.CurrentManagedThreadId;
            pipeline.Start();
            pipeline.Start();
            started.TrySetResult(true);
        })
        {
            IsBackground = true,
        }.Start();

        try
        {
            await CompletesWithinBoundAsync(started.Task);
            await CompletesWithinBoundAsync(warming.Task);
            Assert.NotEqual(callingThread, warmThread);
            pipeline.Add(Item());
            await CompletesWithinBoundAsync(sent.Task);
            Assert.False(warmed.Task.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await CompletesWithinBoundAsync(warmed.Task);
        await WaitUntilAsync(() => _sender.Calls == 1 && pipeline.ActiveCount == 0);
        Assert.Equal(1, Volatile.Read(ref warmCount));
    }

    [Fact]
    public async Task Exporter_is_first_wins()
    {
        var first = new ConcurrentQueue<string>();
        var second = new ConcurrentQueue<string>();
        var pipeline = Pipeline();

        Assert.True(pipeline.TrySetExporter(new DelegateExporter((e, _) =>
        {
            first.Enqueue(e.Id);
            return Task.CompletedTask;
        })));
        Assert.False(pipeline.TrySetExporter(new DelegateExporter((e, _) =>
        {
            second.Enqueue(e.Id);
            return Task.CompletedTask;
        })));

        pipeline.Add(Item());

        await WaitUntilAsync(() => _sender.Calls == 1);
        AssertSequence(first, _sender.Sent[0].Id);
        Assert.Empty(second);
    }

    [Fact]
    public async Task Dispose_stops_the_readers()
    {
        _sender.Fail("HTTP 503");
        _clock.HoldDelays();
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 1 });
        pipeline.Add(Item("a"));
        pipeline.Add(Item("b"));
        await WaitUntilAsync(() => _clock.HeldCount == 1 && pipeline.QueueLength == 1);

        pipeline.Dispose();

        await WaitUntilAsync(() => pipeline.ActiveCount == 0);
        Assert.Equal(0, _clock.HeldCount);
        pipeline.Add(Item("c"));
        Assert.Equal(ShuttingDownLine, Last(_log.Lines));
        Assert.Null(Record.Exception(pipeline.Dispose));
        Assert.Equal(1, pipeline.QueueLength);
        AssertSequence(_sender.Sent.Select(e => e.ResourceName), "a");
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Failed to deliver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flush_and_shutdown_after_Dispose_return_at_once()
    {
        _sender.Hang();
        _clock.HoldDelays();
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 1 });
        pipeline.Add(Item("a"));
        pipeline.Add(Item("b"));
        await WaitUntilAsync(() => _sender.HangingCount == 1 && pipeline.QueueLength == 1);

        pipeline.Dispose();
        var flush = pipeline.FlushAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var unbounded = pipeline.FlushAsync(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
        var shutdown = pipeline.ShutdownAsync();

        Assert.True(flush.IsCompleted);
        Assert.True(unbounded.IsCompleted);
        Assert.True(shutdown.IsCompleted);
        Assert.False(await flush);
        Assert.False(await unbounded);
        await shutdown;
        Assert.Empty(_clock.Delays);
        AssertSequence(_log.Lines, "Shutting down with 1 events still in queue");
    }

    [Fact]
    public async Task Dispose_completes_a_waiting_flush_and_shutdown()
    {
        _sender.Hang();
        _clock.HoldDelays();
        var pipeline = Pipeline(new PipelineSettings { Concurrency = 1 });
        pipeline.Add(Item("a"));
        pipeline.Add(Item("b"));
        await WaitUntilAsync(() => _sender.HangingCount == 1 && pipeline.QueueLength == 1);

        var flush = pipeline.FlushAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var unbounded = pipeline.FlushAsync(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);
        var shutdown = pipeline.ShutdownAsync();
        Assert.False(flush.IsCompleted);
        Assert.False(unbounded.IsCompleted);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(2, _clock.HeldCount);

        pipeline.Dispose();

        await CompletesWithinBoundAsync(flush);
        await CompletesWithinBoundAsync(unbounded);
        await CompletesWithinBoundAsync(shutdown);
        Assert.False(await flush);
        Assert.False(await unbounded);
        Assert.Equal(0, _clock.HeldCount);
        AssertSequence(_log.Lines, "Shutting down with 1 events still in queue");
        Assert.Null(Record.Exception(pipeline.Dispose));
    }

    private sealed class DelegateExporter(Func<AgentCatEvent, CancellationToken, Task> export) : IEventExporter
    {
        public Task ExportAsync(AgentCatEvent e, CancellationToken cancellationToken) => export(e, cancellationToken);
    }
}
