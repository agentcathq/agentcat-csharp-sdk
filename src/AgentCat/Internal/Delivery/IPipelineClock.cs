using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCat.Internal.Delivery;

internal interface IPipelineClock
{
    DateTimeOffset UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemPipelineClock : IPipelineClock
{
    internal static readonly SystemPipelineClock Instance = new();

    private SystemPipelineClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
