using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentCat.Internal.Lifecycle;

/// <summary>
/// Flushes the queued events after the host has stopped, within the runtime's stop window, which it shares with the
/// process-exit and signal handlers. One per service collection, whatever the number of <c>WithAgentCat</c> calls. A
/// plain service provider never runs it.
/// </summary>
/// <remarks>
/// The host runs every <c>StoppedAsync</c> after every <c>StopAsync</c>, so the MCP stdio session and Kestrel have
/// stopped and the events of calls that ended during the stop are queued. A drain in <c>StopAsync</c> would run while
/// the stdio session still runs. The flush does not close the pipeline: the process can keep another host or server.
/// </remarks>
internal sealed class AgentCatLifecycle : IHostedService, IHostedLifecycleService
{
    private readonly IServiceProvider _services;

    public AgentCatLifecycle(IServiceProvider services) => _services = services;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The host token is ignored. The host awaits this method to the end anyway, and when a slow stop has used up
    // HostOptions.ShutdownTimeout the token is already cancelled, so a flush that honored it would send nothing.
    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var runtime = AgentCatMcpServerBuilderExtensions.RuntimeOf(_services, static () => AgentCatRuntime.Current)();
            await ShutdownHandlers.DrainAsync(
                runtime.ShutdownDeadline,
                budget => runtime.Pipeline.FlushAsync(budget, CancellationToken.None),
                budget => runtime.Diagnostics.FlushAsync(budget)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
