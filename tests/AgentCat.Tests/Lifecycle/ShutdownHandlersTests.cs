using System.Diagnostics;
#if NET
using System.Runtime.InteropServices;
#endif
using AgentCat.Internal.Engine;
using AgentCat.Internal.Lifecycle;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.PublicApi;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Lifecycle;

// The registration is process-wide: these tests swap the hooks, forget the decision and run alone.
[Collection(SharedRuntime.Name)]
public sealed class ShutdownHandlersTests : IDisposable
{
    private readonly IShutdownHooks _previous;
    private RecordingShutdownHooks _hooks = new();

    public ShutdownHandlersTests()
    {
        _previous = ShutdownHandlers.Hooks;
        ShutdownHandlers.Hooks = _hooks;
        ShutdownHandlers.Reset();
    }

    public void Dispose()
    {
        ShutdownHandlers.Reset();
        ShutdownHandlers.Hooks = _previous;
        EnabledGate.ResetSkippedAnInstall();
    }

    [Fact]
    public void Process_exit_handler_is_registered_once_and_not_when_disabled()
    {
        ShutdownHandlers.EnsureRegistered(AgentCatRuntime.Shared, disabled: false, withSignals: false);
        ShutdownHandlers.EnsureRegistered(AgentCatRuntime.Shared, disabled: false, withSignals: false);
        ShutdownHandlers.EnsureRegistered(AgentCatRuntime.Shared, disabled: true, withSignals: true);

        Assert.Equal(1, _hooks.ProcessExitCount);
        Assert.Equal(0, _hooks.SignalCount);

        Restart();
        ShutdownHandlers.EnsureRegistered(AgentCatRuntime.Shared, disabled: true, withSignals: true);
        ShutdownHandlers.EnsureRegistered(AgentCatRuntime.Shared, disabled: false, withSignals: true);

        Assert.Equal(0, _hooks.ProcessExitCount);
        Assert.Equal(0, _hooks.SignalCount);
    }

    [Fact]
    public void Only_the_shared_runtime_registers()
    {
        var own = new AgentCatRuntime(new RecordingLog(), new RecordingSender());
        using (var fake = AgentCatSdk.Fake())
        {
            ShutdownHandlers.EnsureRegistered(own, disabled: false, withSignals: true);
            ShutdownHandlers.EnsureRegistered(fake.Runtime, disabled: false, withSignals: true);
        }

        Assert.Equal(0, _hooks.ProcessExitCount);
        Assert.Equal(0, _hooks.SignalCount);

        // Neither call decided: the first call on the shared runtime still does.
        ShutdownHandlers.EnsureRegistered(AgentCatRuntime.Shared, disabled: false, withSignals: false);
        Assert.Equal(1, _hooks.ProcessExitCount);
        own.Pipeline.Dispose();
    }

    [Fact]
    public void Track_adds_the_signal_handlers_and_WithAgentCat_does_not()
    {
        AgentCatSdk.Track(new McpServerOptions(), "proj_test");

        Assert.Equal(1, _hooks.ProcessExitCount);
#if NET
        Assert.Equal("SIGTERM SIGINT", string.Join(" ", _hooks.Signals));
#else
        Assert.Equal(0, _hooks.SignalCount);
#endif

        Restart();
        BuildWithAgentCat(null);

        Assert.Equal(1, _hooks.ProcessExitCount);
        Assert.Equal(0, _hooks.SignalCount);
    }

    [Fact]
    public void The_first_install_decides_for_the_process()
    {
        BuildWithAgentCat(null);
        AgentCatSdk.Track(new McpServerOptions(), "proj_test");

        Assert.Equal(1, _hooks.ProcessExitCount);
        Assert.Equal(0, _hooks.SignalCount);
    }

    [Fact]
    public void DisableShutdownHandlers_on_the_first_install_keeps_every_handler_off()
    {
        AgentCatSdk.Track(new McpServerOptions(), "proj_test", new AgentCatOptions { DisableShutdownHandlers = true });
        AgentCatSdk.Track(new McpServerOptions(), "proj_test");
        BuildWithAgentCat(null);

        Assert.Equal(0, _hooks.ProcessExitCount);
        Assert.Equal(0, _hooks.SignalCount);

        Restart();
        BuildWithAgentCat(o => o.DisableShutdownHandlers = true);
        AgentCatSdk.Track(new McpServerOptions(), "proj_test");

        Assert.Equal(0, _hooks.ProcessExitCount);
        Assert.Equal(0, _hooks.SignalCount);
    }

    [Fact]
    public void A_throwing_configure_callback_still_decides_with_what_it_set()
    {
        BuildWithAgentCat(o =>
        {
            o.DisableShutdownHandlers = true;
            throw new InvalidOperationException("configure boom");
        });
        AgentCatSdk.Track(new McpServerOptions(), "proj_test");

        Assert.Equal(0, _hooks.ProcessExitCount);
        Assert.Equal(0, _hooks.SignalCount);
    }

    [Fact]
    public void An_install_that_the_gate_stops_registers_nothing_and_decides_nothing()
    {
        var previous = Environment.GetEnvironmentVariable(EnabledGate.Variable);
        Environment.SetEnvironmentVariable(EnabledGate.Variable, "false");
        try
        {
            AgentCatSdk.Track(new McpServerOptions(), "proj_test");
            BuildWithAgentCat(null);

            Assert.Equal(0, _hooks.ProcessExitCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnabledGate.Variable, previous);
        }

        AgentCatSdk.Track(new McpServerOptions(), "proj_test");

        Assert.Equal(1, _hooks.ProcessExitCount);
    }

#if NET
    [Fact]
    public void Registered_signal_handlers_leave_a_signal_that_a_host_took_alone()
    {
        AgentCatSdk.Track(new McpServerOptions(), "proj_test");

        Assert.Equal(2, _hooks.SignalCount);
        foreach (var (signal, handler) in _hooks.Signals.Zip(_hooks.SignalHandlers))
        {
            var context = new PosixSignalContext(signal) { Cancel = true };
            var elapsed = Stopwatch.StartNew();
            handler(context);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), signal + " handler waited " + elapsed.Elapsed);
            Assert.True(context.Cancel);
        }
    }
#endif

    private void Restart()
    {
        _hooks = new RecordingShutdownHooks();
        ShutdownHandlers.Hooks = _hooks;
        ShutdownHandlers.Reset();
    }

    // A registration on the current runtime (the shared one here) that decides when its options are built.
    private static void BuildWithAgentCat(Action<AgentCatOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithAgentCat("proj_test", configure);
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
    }
}

public class ProcessExitHandlerTests
{
    private const string Dropped = "Queue is shutting down, event dropped";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Process_exit_handler_drains_then_closes_the_pipeline()
    {
        using var d = new CustomEventTests.Direct();
        d.Sender.Hang();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        await HostedLifecycleTests.WaitUntilAsync(() => d.Sender.HangingCount == 1);

        var exit = Task.Run(() => ShutdownHandlers.OnProcessExit(d.Runtime), Token);
        await Task.Delay(200, Token);
        Assert.False(exit.IsCompleted, "The handler returned before the drain ended.");
        d.Sender.Release();
        await FakeAgentCatApi.WithinAsync(exit, FakeAgentCatApi.WaitBound);

        Assert.Contains(d.Log.Lines, l => l.StartsWith("Successfully sent event ", StringComparison.Ordinal));
        await d.Client.PublishCustomEventAsync("s2", "proj_test");
        Assert.Contains(Dropped, d.Log.Lines);
        Assert.Single(d.Sender.Sent);
    }

    [Fact]
    public void Process_exit_handler_never_throws()
    {
        Assert.Null(Record.Exception(() => ShutdownHandlers.OnProcessExit(null!)));
    }

    [Fact]
    public void Shutdown_waits_are_bounded()
    {
        var never = new TaskCompletionSource<bool>();
        var deadline = RealTime(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var elapsed = Stopwatch.StartNew();

        ShutdownHandlers.Drain(deadline, _ => never.Task, _ => never.Task);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), "The handler waited " + elapsed.Elapsed);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(150), "The handler did not wait: " + elapsed.Elapsed);
    }

    [Fact]
    public void A_failing_drain_still_flushes_diagnostics()
    {
        var flushed = 0;

        ShutdownHandlers.Drain(
            new ShutdownDeadline(),
            _ => throw new InvalidOperationException("drain boom"),
            _ =>
            {
                flushed++;
                return Task.CompletedTask;
            });
        ShutdownHandlers.Drain(
            new ShutdownDeadline(),
            _ => Task.FromException<bool>(new InvalidOperationException("drain boom")),
            _ => throw new InvalidOperationException("diagnostics boom"));

        Assert.Equal(1, flushed);
    }

    // The flush after a host stop, the signal handler and the process-exit handler share one deadline: the first sets
    // it, a later one waits for events only for what is left, and diagnostics never wait past the deadline plus 1 s.
    [Fact]
    public async Task A_later_stop_path_waits_only_for_what_is_left_of_the_deadline()
    {
        var now = TimeSpan.Zero;
        var deadline = new ShutdownDeadline(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), () => now);
        var events = new List<double>();
        var diagnostics = new List<double>();
        Func<TimeSpan, Task<bool>> TimedOut(double seconds) => budget =>
        {
            events.Add(budget.TotalSeconds);
            now += TimeSpan.FromSeconds(seconds);
            return Task.FromResult(false);
        };
        Task Flush(TimeSpan budget)
        {
            diagnostics.Add(budget.TotalSeconds);
            return Task.CompletedTask;
        }

        // The host stop flushes for 3 s; a signal at 3 s gets the 2 s left; a process exit at 5 s gets none, and its
        // diagnostics flush the rest of the window; a path after the window flushes nothing.
        await ShutdownHandlers.DrainAsync(deadline, TimedOut(3), Flush);
        ShutdownHandlers.Drain(deadline, TimedOut(2), Flush);
        ShutdownHandlers.Drain(deadline, TimedOut(0.5), Flush);
        ShutdownHandlers.Drain(deadline, TimedOut(0.6), Flush);

        Assert.Equal(new[] { 5, 2, 0, 0.0 }, events);
        Assert.Equal(new[] { 1, 1, 0.5 }, diagnostics);
    }

    // A process that stops one host and keeps running gets a new window at its next stop.
    [Fact]
    public void A_drain_that_leaves_nothing_clears_the_deadline()
    {
        var now = TimeSpan.Zero;
        var deadline = new ShutdownDeadline(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), () => now);
        var events = new List<double>();
        Func<TimeSpan, Task<bool>> Drain(double seconds, bool drained) => budget =>
        {
            events.Add(budget.TotalSeconds);
            now += TimeSpan.FromSeconds(seconds);
            return Task.FromResult(drained);
        };

        // Drained in 1 s, then a new stop at 2 s gets 5 s again.
        ShutdownHandlers.Drain(deadline, Drain(1, drained: true), _ => Task.CompletedTask);
        now += TimeSpan.FromSeconds(1);
        ShutdownHandlers.Drain(deadline, Drain(6, drained: false), _ => Task.CompletedTask);

        // That one ran out of time: the next path does not wait, but it finds nothing left and clears the deadline.
        ShutdownHandlers.Drain(deadline, Drain(0, drained: true), _ => Task.CompletedTask);
        ShutdownHandlers.Drain(deadline, Drain(0, drained: true), _ => Task.CompletedTask);

        Assert.Equal(new[] { 5, 5, 0, 5.0 }, events);
    }

    // A path joins the stop that is running, also when it starts after that stop's window has ended, until one more full
    // window (events plus diagnostics) has passed. Only a path that starts later than that is a new stop.
    [Fact]
    public void A_path_gets_a_fresh_window_only_one_full_window_after_the_old_one_ended()
    {
        var now = TimeSpan.Zero;
        var deadline = new ShutdownDeadline(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), () => now);

        deadline.Enter();
        now = TimeSpan.FromSeconds(5.5);
        var inWindow = deadline.Enter();
        now = TimeSpan.FromSeconds(6);
        var atWindowEnd = deadline.Enter();
        now = TimeSpan.FromSeconds(11.99);
        var justBefore = deadline.Enter();
        now = TimeSpan.FromSeconds(12);
        var afterWindow = deadline.Enter();

        Assert.Equal(TimeSpan.Zero, inWindow.EventsLeft);
        Assert.Equal(TimeSpan.Zero, atWindowEnd.EventsLeft);
        Assert.Equal(TimeSpan.Zero, justBefore.EventsLeft);
        Assert.Equal(TimeSpan.FromSeconds(5), afterWindow.EventsLeft);
    }

    // The paths of one stop wait for events 5 s in total, whatever the diagnostics flush or the teardown between them
    // takes: the host flush times out, its diagnostics flush uses the whole budget, and the process-exit handler
    // follows 10 ms later.
    [Fact]
    public async Task A_process_exit_right_after_a_timed_out_host_flush_with_diagnostics_waits_for_no_events()
    {
        var now = TimeSpan.Zero;
        var deadline = new ShutdownDeadline(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), () => now);
        var events = new List<double>();

        await ShutdownHandlers.DrainAsync(deadline, TimedOut(), budget =>
        {
            now += budget;
            return Task.CompletedTask;
        });
        now += TimeSpan.FromMilliseconds(10);
        ShutdownHandlers.Drain(deadline, TimedOut(), _ => Task.CompletedTask);

        Assert.Equal(new[] { 5, 0.0 }, events);

        Func<TimeSpan, Task<bool>> TimedOut() => budget =>
        {
            events.Add(budget.TotalSeconds);
            now += budget;
            return Task.FromResult(false);
        };
    }

    // The host flush times out, then the host takes 1.2 s to tear down, then the process exits.
    [Fact]
    public async Task A_process_exit_after_a_timed_out_host_flush_and_a_slow_teardown_waits_for_no_events()
    {
        var now = TimeSpan.Zero;
        var deadline = new ShutdownDeadline(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), () => now);
        var events = new List<double>();

        await ShutdownHandlers.DrainAsync(deadline, TimedOut(), _ => Task.CompletedTask);
        now += TimeSpan.FromSeconds(1.2);
        ShutdownHandlers.Drain(deadline, TimedOut(), _ => Task.CompletedTask);

        Assert.Equal(new[] { 5, 0.0 }, events);

        Func<TimeSpan, Task<bool>> TimedOut() => budget =>
        {
            events.Add(budget.TotalSeconds);
            now += budget;
            return Task.FromResult(false);
        };
    }

    // The signal handler drains with a hanging POST, then the process-exit handler that follows it on .NET 8.
    [Fact]
    public void A_process_exit_after_a_timed_out_signal_drain_waits_for_no_events()
    {
        var now = TimeSpan.Zero;
        var deadline = new ShutdownDeadline(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), () => now);
        var events = new List<double>();

        ShutdownHandlers.Drain(deadline, TimedOut(), budget =>
        {
            now += budget;
            return Task.CompletedTask;
        });
        ShutdownHandlers.Drain(deadline, TimedOut(), budget =>
        {
            now += budget;
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { 5, 0.0 }, events);

        Func<TimeSpan, Task<bool>> TimedOut() => budget =>
        {
            events.Add(budget.TotalSeconds);
            now += budget;
            return Task.FromResult(false);
        };
    }

    // The real paths of one runtime: the host's flush uses the whole window with an event that never ends, so the
    // process-exit handler does not wait for events again. It still closes the queue.
    [Fact]
    public async Task Process_exit_after_a_timed_out_host_stop_does_not_wait_again()
    {
        var log = new RecordingLog();
        var sender = new RecordingSender().Hang();
        var runtime = new AgentCatRuntime(log, sender, shutdownDeadline: RealTime(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(200)));
        using (var host = HostedLifecycleTests.Host(runtime))
        {
            await host.StartAsync(Token);
            await host.Services.GetRequiredService<IAgentCat>().PublishCustomEventAsync("never-answered", "proj_test");
            await HostedLifecycleTests.WaitUntilAsync(() => sender.HangingCount == 1);

            var hostStop = Stopwatch.StartNew();
            await host.StopAsync(Token);
            hostStop.Stop();
            Assert.True(hostStop.Elapsed >= TimeSpan.FromMilliseconds(550), "The host stop did not wait for the event: " + hostStop.Elapsed);
        }

        var exit = Stopwatch.StartNew();
        ShutdownHandlers.OnProcessExit(runtime);
        exit.Stop();

        Assert.True(exit.Elapsed < TimeSpan.FromMilliseconds(500), "The process-exit handler waited for events again: " + exit.Elapsed);
        await new AgentCatClient(runtime).PublishCustomEventAsync("after-exit", "proj_test");
        Assert.Contains(Dropped, log.Lines);
        sender.Release();
        runtime.Pipeline.Dispose();
    }

    private static ShutdownDeadline RealTime(TimeSpan events, TimeSpan diagnostics)
    {
        var clock = Stopwatch.StartNew();
        return new ShutdownDeadline(events, diagnostics, () => clock.Elapsed);
    }
}

#if NET
// TS signal-shutdown.test.ts, for PosixSignalRegistration: the handler never re-raises a signal. It leaves Cancel false,
// so the default termination follows the handler, unless an earlier handler (a host's, or the customer's) set it.
public class SignalShutdownTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // TS: re-raises the signal after the flush settles so default termination proceeds
    [Fact]
    public async Task Re_raises_the_signal_after_the_flush_settles_so_default_termination_proceeds()
    {
        var order = new List<string>();
        var drain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new PosixSignalContext(PosixSignal.SIGTERM);

        var handler = Task.Run(
            () => ShutdownHandlers.OnSignal(
                context,
                new ShutdownDeadline(),
                _ =>
                {
                    Note(order, "drain");
                    return drain.Task;
                },
                _ =>
                {
                    Note(order, "diagnostics");
                    return Task.CompletedTask;
                }),
            Token);
        await Task.Delay(200, Token);

        Assert.False(handler.IsCompleted, "The handler returned before the flush settled.");
        drain.SetResult(true);
        await FakeAgentCatApi.WithinAsync(handler, FakeAgentCatApi.WaitBound);
        Assert.False(context.Cancel);
        Assert.Equal("drain diagnostics", string.Join(" ", Snapshot(order)));
    }

    // TS: does not re-raise when the customer has their own listener for the signal
    [Fact]
    public void Does_not_re_raise_when_the_customer_has_their_own_listener_for_the_signal()
    {
        var calls = 0;
        var context = new PosixSignalContext(PosixSignal.SIGINT) { Cancel = true };

        ShutdownHandlers.OnSignal(
            context,
            new ShutdownDeadline(),
            _ =>
            {
                calls++;
                return Task.FromResult(true);
            },
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            });

        Assert.True(context.Cancel);
        Assert.Equal(0, calls);
    }

    // TS: re-raises even when the flush itself fails
    [Fact]
    public void Re_raises_even_when_the_flush_itself_fails()
    {
        var diagnostics = 0;
        Func<TimeSpan, Task> flushDiagnostics = _ =>
        {
            diagnostics++;
            return Task.CompletedTask;
        };
        var thrown = new PosixSignalContext(PosixSignal.SIGTERM);
        var faulted = new PosixSignalContext(PosixSignal.SIGTERM);

        Assert.Null(Record.Exception(() => ShutdownHandlers.OnSignal(thrown, new ShutdownDeadline(), _ => throw new InvalidOperationException("drain boom"), flushDiagnostics)));
        Assert.Null(Record.Exception(() => ShutdownHandlers.OnSignal(faulted, new ShutdownDeadline(), _ => Task.FromException<bool>(new InvalidOperationException("drain boom")), flushDiagnostics)));

        Assert.False(thrown.Cancel);
        Assert.False(faulted.Cancel);
        Assert.Equal(2, diagnostics);
    }

    // TS: never throws even when re-raise fails
    [Fact]
    public async Task Never_throws_even_when_re_raise_fails()
    {
        var log = new RecordingLog { ThrowWhen = _ => true };
        var sender = new RecordingSender();
        var runtime = new AgentCatRuntime(log, sender);
        await new Internal.Engine.AgentCatClient(runtime).PublishCustomEventAsync("s1", "proj_test");
        var context = new PosixSignalContext(PosixSignal.SIGTERM);

        Assert.Null(Record.Exception(() => ShutdownHandlers.OnSignal(context, runtime)));
        Assert.Null(Record.Exception(() => ShutdownHandlers.OnSignal(
            new PosixSignalContext(PosixSignal.SIGINT),
            new ShutdownDeadline(),
            _ => Task.FromResult(true),
            _ => throw new InvalidOperationException("diagnostics boom"))));

        Assert.False(context.Cancel);
        Assert.Single(sender.Sent);
        runtime.Pipeline.Dispose();
    }

    [Fact]
    public async Task Signal_handler_skips_when_a_host_already_cancelled()
    {
        using var d = new CustomEventTests.Direct();
        d.Sender.Hang();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        await HostedLifecycleTests.WaitUntilAsync(() => d.Sender.HangingCount == 1);
        var context = new PosixSignalContext(PosixSignal.SIGTERM) { Cancel = true };

        var elapsed = Stopwatch.StartNew();
        ShutdownHandlers.OnSignal(context, d.Runtime);
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), "The handler waited " + elapsed.Elapsed);
        Assert.True(context.Cancel);
        Assert.Equal(1, d.Sender.HangingCount);
        d.Sender.Release();
    }

    [Theory]
    [InlineData(PosixSignal.SIGTERM)]
    [InlineData(PosixSignal.SIGINT)]
    public async Task Signal_handler_flushes_and_leaves_Cancel_false(PosixSignal signal)
    {
        using var d = new CustomEventTests.Direct();
        d.Sender.Hang();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        await HostedLifecycleTests.WaitUntilAsync(() => d.Sender.HangingCount == 1);
        var context = new PosixSignalContext(signal);

        var handler = Task.Run(() => ShutdownHandlers.OnSignal(context, d.Runtime), Token);
        await Task.Delay(200, Token);
        Assert.False(handler.IsCompleted, "The handler returned before the flush ended.");
        d.Sender.Release();
        await FakeAgentCatApi.WithinAsync(handler, FakeAgentCatApi.WaitBound);

        Assert.False(context.Cancel);
        Assert.Contains(d.Log.Lines, l => l.StartsWith("Successfully sent event ", StringComparison.Ordinal));

        // A flush, not a shutdown: if a later handler keeps the process alive, the queue still takes events.
        await d.Client.PublishCustomEventAsync("s2", "proj_test");
        Assert.True(await d.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, Token));
        Assert.Equal("s1 s2", string.Join(" ", d.Sender.Sent.Select(e => e.SessionId)));
        Assert.DoesNotContain("Queue is shutting down, event dropped", d.Log.Lines);
    }

    private static void Note(List<string> order, string step)
    {
        lock (order)
        {
            order.Add(step);
        }
    }

    private static string[] Snapshot(List<string> order)
    {
        lock (order)
        {
            return order.ToArray();
        }
    }
}
#endif
