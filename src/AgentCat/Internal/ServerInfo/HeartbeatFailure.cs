using System;

namespace AgentCat.Internal.ServerInfo;

// A failure of the heartbeat whose message is written by the SDK, so a log line can hold it. The message of any other
// exception can quote text of the customer, and no log line of the heartbeat holds it.
internal sealed class HeartbeatFailure : Exception
{
    internal HeartbeatFailure(string message)
        : base(message)
    {
    }

    // The reason for a log line: the SDK's own message, or only the type name of an exception from other code.
    internal static string ReasonOf(Exception error) => error is HeartbeatFailure ? error.Message : error.GetType().Name;
}
