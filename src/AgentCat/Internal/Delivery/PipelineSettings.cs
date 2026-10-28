using System;

namespace AgentCat.Internal.Delivery;

internal sealed class PipelineSettings
{
    internal int MaxQueueSize { get; init; } = 10000;

    internal int Concurrency { get; init; } = 5;

    internal int MaxRetries { get; init; } = 3;

    internal TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
