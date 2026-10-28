using System;

namespace AgentCat.Internal.Delivery;

internal sealed class EventSendException(string message, Exception? inner = null, bool retryable = true) : Exception(message, inner)
{
    internal bool Retryable { get; } = retryable;
}
