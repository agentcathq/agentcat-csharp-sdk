using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCat.Internal.Delivery;

internal sealed class QueuedEvent
{
    internal QueuedEvent(AgentCatEvent e) => Event = e;

    internal AgentCatEvent Event { get; }

    // Called when the pipeline drops the event before Prepare runs.
    internal Action? Discard { get; init; }

    internal Func<AgentCatEvent, CancellationToken, ValueTask>? Prepare { get; init; }

    internal Func<AgentCatEvent, CancellationToken, ValueTask<AgentCatEvent?>>? RedactEvent { get; init; }

    internal Func<string, CancellationToken, ValueTask<string>>? RedactStrings { get; init; }
}
