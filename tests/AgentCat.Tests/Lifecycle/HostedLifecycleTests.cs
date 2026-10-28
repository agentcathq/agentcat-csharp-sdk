using System.Collections.Concurrent;
using System.Diagnostics;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Lifecycle;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentCat.Tests.Lifecycle;

public class HostedLifecycleTests
{
    private const string Dropped = "Queue is shutting down, event dropped";

    // Long enough that a stop which does not wait for the queue returns before the send ends.
    internal static readonly TimeSpan SendTime = TimeSpan.FromMilliseconds(300);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Hosted_lifecycle_is_registered_once_for_two_WithAgentCat_calls()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithAgentCat("proj_a").WithAgentCat("proj_b");
        services.AddMcpServer().WithAgentCat("proj_c");

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AgentCatLifecycle));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IHostedService>().OfType<AgentCatLifecycle>());
    }

    [Fact]
    public async Task Stop_flushes_in_StoppedAsync_not_StopAsync()
    {
        var sender = new SlowSender(SendTime);
        var runtime = new AgentCatRuntime(new RecordingLog(), sender);
        using var host = Host(
            runtime,
            before: s => s.AddSingleton<IHostedService>(sp => new StopPublisher(sp.GetRequiredService<IAgentCat>(), "registered-before")),
            after: s => s.AddSingleton<IHostedService>(sp => new StopPublisher(sp.GetRequiredService<IAgentCat>(), "registered-after")));
        await host.StartAsync(Token);

        await host.StopAsync(Token);

        // The host stops services in reverse order: the one registered before AgentCat stops after AgentCat's StopAsync,
        // as the MCP stdio session does. Both events are delivered by the time the host has stopped.
        Assert.Equal("registered-after registered-before", string.Join(" ", sender.Delivered.Select(e => e.SessionId).OrderBy(s => s, StringComparer.Ordinal)));
        runtime.Pipeline.Dispose();
    }

    [Fact]
    public async Task StoppedAsync_drains_even_when_the_host_token_is_cancelled()
    {
        var sender = new SlowSender(SendTime);
        var runtime = new AgentCatRuntime(new RecordingLog(), sender);
        var witness = new TokenWitness();
        using var host = Host(runtime, after: s => s.AddSingleton<IHostedService>(witness));
        await host.StartAsync(Token);
        await host.Services.GetRequiredService<IAgentCat>().PublishCustomEventAsync("pending", "proj_test");

        await host.StopAsync(new CancellationToken(canceled: true));

        Assert.True(witness.StoppedTokenWasCancelled);
        Assert.Equal("pending", Assert.Single(sender.Delivered).SessionId);
        runtime.Pipeline.Dispose();
    }

    [Fact]
    public async Task Host_stop_flushes_without_closing_the_pipeline()
    {
        var log = new RecordingLog();
        var sender = new SlowSender(SendTime);
        var runtime = new AgentCatRuntime(log, sender);
        using (var first = Host(runtime))
        {
            await first.StartAsync(Token);
            await first.Services.GetRequiredService<IAgentCat>().PublishCustomEventAsync("first-host", "proj_test");
            await first.StopAsync(Token);
        }

        Assert.Equal("first-host", Assert.Single(sender.Delivered).SessionId);

        using var second = Host(runtime);
        await second.StartAsync(Token);
        await second.Services.GetRequiredService<IAgentCat>().PublishCustomEventAsync("second-host", "proj_test");
        await second.StopAsync(Token);

        Assert.Equal("first-host second-host", string.Join(" ", sender.Delivered.Select(e => e.SessionId)));
        Assert.DoesNotContain(Dropped, log.Lines);
        runtime.Pipeline.Dispose();
    }

    [Fact]
    public async Task Plain_service_provider_runs_no_lifecycle()
    {
        var log = new RecordingLog();
        var sender = new RecordingSender().Hang();
        var runtime = new AgentCatRuntime(log, sender);
        var services = new ServiceCollection();
        services.AddSingleton(runtime);
        services.AddMcpServer().WithAgentCat("proj_test");
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IAgentCat>().PublishCustomEventAsync("in-flight", "proj_test");
        await WaitUntilAsync(() => sender.HangingCount == 1);

        var dispose = Stopwatch.StartNew();
        provider.Dispose();
        dispose.Stop();

        Assert.True(dispose.Elapsed < TimeSpan.FromSeconds(1), "Disposing the provider waited " + dispose.Elapsed);
        Assert.Equal(1, sender.HangingCount);
        await new AgentCatClient(runtime).PublishCustomEventAsync("after-dispose", "proj_test");
        sender.Release();
        Assert.True(await runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, Token));
        Assert.Equal("in-flight after-dispose", string.Join(" ", sender.Sent.Select(e => e.SessionId)));
        Assert.DoesNotContain(Dropped, log.Lines);
        runtime.Pipeline.Dispose();
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true.");
            await Task.Delay(10, Token);
        }
    }

    // A Generic Host whose services resolve the given runtime. The lifetime does not touch console signals.
    internal static IHost Host(AgentCatRuntime runtime, Action<IServiceCollection>? before = null, Action<IServiceCollection>? after = null)
        => new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostLifetime>(new QuietLifetime());
                services.AddSingleton(runtime);
                before?.Invoke(services);
                services.AddMcpServer().WithAgentCat("proj_test");
                after?.Invoke(services);
            })
            .Build();

    // Records when each send ends, after a fixed delay.
    internal sealed class SlowSender(TimeSpan delay) : IEventSender
    {
        private readonly ConcurrentQueue<AgentCatEvent> _delivered = new();

        internal IReadOnlyList<AgentCatEvent> Delivered => _delivered.ToArray();

        public async Task SendAsync(AgentCatEvent e, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            _delivered.Enqueue(e);
        }
    }

    private sealed class StopPublisher(IAgentCat agentCat, string session) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => agentCat.PublishCustomEventAsync(session, "proj_stop");
    }

    private sealed class TokenWitness : IHostedService, IHostedLifecycleService
    {
        internal bool StoppedTokenWasCancelled { get; private set; }

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken)
        {
            StoppedTokenWasCancelled = cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }
    }

    private sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
