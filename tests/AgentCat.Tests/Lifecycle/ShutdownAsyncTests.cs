using System.Diagnostics;
#if NET
using System.Runtime.InteropServices;
#endif
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Lifecycle;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.Diagnostics;
using AgentCat.Tests.PublicApi;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using SdkDiagnostics = AgentCat.Internal.Diagnostics.Diagnostics;

namespace AgentCat.Tests.Lifecycle;

public class ShutdownAsyncTests
{
    private const string Dropped = "Queue is shutting down, event dropped";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ShutdownAsync_drains_then_drops_later_events()
    {
        using var d = new CustomEventTests.Direct();
        d.Sender.Hang();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        await HostedLifecycleTests.WaitUntilAsync(() => d.Sender.HangingCount == 1);

        var shutdown = d.Client.ShutdownAsync(Token);
        await Task.Delay(100, Token);
        Assert.False(shutdown.IsCompleted, "ShutdownAsync returned before the drain ended.");
        d.Sender.Release();
        await FakeAgentCatApi.WithinAsync(shutdown, FakeAgentCatApi.WaitBound);

        Assert.True(await shutdown);
        await d.Client.PublishCustomEventAsync("s2", "proj_test");
        Assert.Contains(Dropped, d.Log.Lines);
        Assert.True(await d.Client.FlushAsync(FakeAgentCatApi.WaitBound, Token));
        Assert.Equal("s1", Assert.Single(d.Sender.Sent).SessionId);

        // A second call waits for the same drain.
        Assert.True(await d.Client.ShutdownAsync(Token));
    }

    [Fact]
    public async Task ShutdownAsync_returns_false_when_events_are_left_after_its_budget()
    {
        var log = new RecordingLog();
        var sender = new RecordingSender().Hang();
        // The manual clock ends the 5 s budget at once.
        var runtime = new AgentCatRuntime(log, sender, clock: new ManualClock());
        var client = new AgentCatClient(runtime);
        await client.PublishCustomEventAsync("s1", "proj_test");
        await HostedLifecycleTests.WaitUntilAsync(() => sender.HangingCount == 1);

        Assert.False(await client.ShutdownAsync(Token));

        sender.Release();
        var flushed = runtime.Pipeline.FlushAsync(Timeout.InfiniteTimeSpan, Token);
        await FakeAgentCatApi.WithinAsync(flushed, FakeAgentCatApi.WaitBound);
        Assert.True(await flushed);
        Assert.Single(sender.Sent);
        runtime.Pipeline.Dispose();
    }

    [Fact]
    public async Task ShutdownAsync_token_ends_the_wait_and_returns_false()
    {
        using var d = new CustomEventTests.Direct();
        d.Sender.Hang();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        await HostedLifecycleTests.WaitUntilAsync(() => d.Sender.HangingCount == 1);
        using var cts = new CancellationTokenSource();

        var shutdown = d.Client.ShutdownAsync(cts.Token);
        Assert.False(shutdown.IsCompleted);
        cts.Cancel();
        await FakeAgentCatApi.WithinAsync(shutdown, FakeAgentCatApi.WaitBound);

        Assert.False(await shutdown);
        Assert.False(await d.Client.ShutdownAsync(new CancellationToken(canceled: true)));

        // The token only ended the wait: the queue is closed and the event is still delivered.
        await d.Client.PublishCustomEventAsync("s2", "proj_test");
        Assert.Contains(Dropped, d.Log.Lines);
        Assert.Equal(1, d.Sender.HangingCount);
        d.Sender.Release();
        Assert.True(await d.Client.ShutdownAsync(Token));
        Assert.Equal("s1", Assert.Single(d.Sender.Sent).SessionId);
    }

    [Fact]
    public async Task ShutdownAsync_of_an_idle_queue_returns_true_even_with_a_cancelled_token()
    {
        using var d = new CustomEventTests.Direct();

        Assert.True(await d.Client.ShutdownAsync(new CancellationToken(canceled: true)));

        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        Assert.Contains(Dropped, d.Log.Lines);
    }

    [Fact]
    public async Task ShutdownAsync_never_throws()
    {
        var client = new AgentCatClient(() => throw new InvalidOperationException("runtime boom"));

        Assert.False(await client.ShutdownAsync(Token));
    }

    [Fact]
    public async Task IAgentCat_ShutdownAsync_closes_the_registration_runtime()
    {
        using var d = new CustomEventTests.Direct();
        var services = new ServiceCollection();
        services.AddSingleton(d.Runtime);
        services.AddMcpServer().WithAgentCat("proj_test");
        using var provider = services.BuildServiceProvider();
        var agentCat = provider.GetRequiredService<IAgentCat>();

        await agentCat.PublishCustomEventAsync("s1", "proj_test");
        Assert.True(await agentCat.ShutdownAsync(Token));
        await agentCat.PublishCustomEventAsync("s2", "proj_test");

        Assert.Equal("s1", Assert.Single(d.Sender.Sent).SessionId);
        Assert.Contains(Dropped, d.Log.Lines);
    }

    [Fact]
    public async Task FlushAsync_with_the_default_token_waits_for_pending_events()
    {
        using var d = new CustomEventTests.Direct();
        d.Sender.Hang();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        await HostedLifecycleTests.WaitUntilAsync(() => d.Sender.HangingCount == 1);

        // The default token on purpose: it cannot be cancelled.
#pragma warning disable xUnit1051
        var open = d.Client.FlushAsync();
        var bounded = d.Client.FlushAsync(TimeSpan.FromMinutes(5));
#pragma warning restore xUnit1051
        var none = d.Client.FlushAsync(null, CancellationToken.None);
        await Task.Delay(100, Token);

        Assert.False(open.IsCompleted);
        Assert.False(bounded.IsCompleted);
        Assert.False(none.IsCompleted);
        d.Sender.Release();
        await FakeAgentCatApi.WithinAsync(Task.WhenAll(open, bounded, none), FakeAgentCatApi.WaitBound);
        Assert.True(await open);
        Assert.True(await bounded);
        Assert.True(await none);
    }
}

// The fake is process-wide, so this test runs alone. It never closes the shared queue.
[Collection(SharedRuntime.Name)]
public sealed class SharedRuntimeShutdownAsyncTests
{
    [Fact]
    public async Task AgentCatSdk_ShutdownAsync_closes_the_queue_that_FlushAsync_uses()
    {
        using var fake = AgentCatSdk.Fake();
        await AgentCatSdk.PublishCustomEventAsync("s1", "proj_test");

        Assert.True(await AgentCatSdk.ShutdownAsync(TestContext.Current.CancellationToken));
        await AgentCatSdk.PublishCustomEventAsync("s2", "proj_test");

        Assert.Equal("s1", Assert.Single(await fake.GetEventsAsync(TestContext.Current.CancellationToken)).SessionId);
        Assert.Contains("Queue is shutting down, event dropped", await fake.GetLogsAsync(TestContext.Current.CancellationToken));
        Assert.True(await AgentCatSdk.FlushAsync(TimeSpan.Zero, TestContext.Current.CancellationToken));
    }
}

// The last diagnostics batch goes out after the event drain, so it carries the drain's own lines, as in PHP.
public class ShutdownDiagnosticsTests
{
    private const string Sent = "Successfully sent event ";

    public static TheoryData<string> Paths
    {
        get
        {
            var paths = new TheoryData<string> { "ProcessExit", "HostStopped", "ShutdownAsync" };
#if NET
            paths.Add("Signal");
#endif
            return paths;
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Diagnostics_flush_runs_after_the_event_drain(string path)
    {
        var token = TestContext.Current.CancellationToken;
        var h = new DiagnosticsHarness(inTestRun: true).ForceEnabled();
        var log = new ProcessLog(new FileLog(FileLog.DefaultPath()));
        var diagnostics = new SdkDiagnostics(
            name => name == "AGENTCAT_DISABLE_DIAGNOSTICS" ? "false" : null,
            () => true,
            h.Clock,
            log.SetDiagnosticsSink,
            h.Client,
            h.Timers.Start);
        var sender = new RecordingSender().Hang();
        var runtime = new AgentCatRuntime(log, sender, diagnostics: diagnostics);
        diagnostics.Init("proj_diag", disabled: false);
        var client = new AgentCatClient(runtime);
        await client.PublishCustomEventAsync("s1", "proj_diag");
        await HostedLifecycleTests.WaitUntilAsync(() => sender.HangingCount == 1);
        var services = new ServiceCollection().AddSingleton(runtime).BuildServiceProvider();

        Task shutdown = path switch
        {
            "ProcessExit" => Task.Run(() => ShutdownHandlers.OnProcessExit(runtime), token),
            "HostStopped" => new AgentCatLifecycle(services).StoppedAsync(new CancellationToken(canceled: true)),
            "ShutdownAsync" => client.ShutdownAsync(token),
#if NET
            "Signal" => Task.Run(() => ShutdownHandlers.OnSignal(new PosixSignalContext(PosixSignal.SIGTERM), runtime), token),
#endif
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
        await Task.Delay(200, token);
        Assert.Empty(h.Handler.Requests);
        sender.Release();
        await FakeAgentCatApi.WithinAsync(shutdown, FakeAgentCatApi.WaitBound);

        var batch = Assert.Single(h.Handler.Requests);
        var bodies = batch.Records.Select(r => (string)r!["body"]!["stringValue"]!).ToArray();
        Assert.Contains(bodies, b => b.Contains(Sent));
        Assert.Equal(0, diagnostics.BufferedCount);
        await services.DisposeAsync();
        runtime.Pipeline.Dispose();
    }

    // The process-exit drain ends at the deadline with one event in flight and one waiting. It writes its line about
    // the waiting event before the diagnostics flush starts, so the last batch carries that line.
    [Fact]
    public async Task The_shutting_down_line_of_the_process_exit_drain_is_in_the_last_batch()
    {
        var h = new DiagnosticsHarness(inTestRun: true).ForceEnabled();
        var log = new ProcessLog(new FileLog(FileLog.DefaultPath()));
        var diagnostics = new SdkDiagnostics(
            name => name == "AGENTCAT_DISABLE_DIAGNOSTICS" ? "false" : null,
            () => true,
            h.Clock,
            log.SetDiagnosticsSink,
            h.Client,
            h.Timers.Start);
        var sender = new RecordingSender().Hang();
        var clock = Stopwatch.StartNew();
        var runtime = new AgentCatRuntime(
            log,
            sender,
            new PipelineSettings { Concurrency = 1 },
            diagnostics: diagnostics,
            shutdownDeadline: new ShutdownDeadline(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), () => clock.Elapsed));
        diagnostics.Init("proj_diag", disabled: false);
        var client = new AgentCatClient(runtime);
        await client.PublishCustomEventAsync("in-flight", "proj_diag");
        await client.PublishCustomEventAsync("waiting", "proj_diag");
        await HostedLifecycleTests.WaitUntilAsync(() => sender.HangingCount == 1);

        ShutdownHandlers.OnProcessExit(runtime);

        var batch = Assert.Single(h.Handler.Requests);
        var bodies = batch.Records.Select(r => (string)r!["body"]!["stringValue"]!).ToArray();
        Assert.Contains(bodies, b => b.EndsWith(" Shutting down with 1 events still in queue", StringComparison.Ordinal));
        sender.Release();
        runtime.Pipeline.Dispose();
    }
}
