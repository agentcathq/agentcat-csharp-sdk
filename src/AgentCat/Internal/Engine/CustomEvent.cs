using System;
using System.Text.Json.Nodes;
using AgentCat.Internal.Events;
using AgentCat.Internal.Json;
using AgentCat.Internal.Logging;

namespace AgentCat.Internal.Engine;

internal static class CustomEvent
{
    internal const string EventType = "agentcat:custom";

    internal const string ProjectRequired = "projectId is required for publishCustomEvent";

    internal const string FirstParameter = "First parameter must be either an MCP server object or a session ID string";

    internal const string NotTracked = "Server is not tracked. Please call agentcat.track() first or provide a session ID string.";

    internal const string NotTrackedWhileOff =
        "Warning: Failed to publish custom event - the server is not tracked (it was set up while AgentCat was off; call AgentCatSdk.Fake() before WithAgentCat or Track)";

    internal const string NoSession =
        "publishCustomEvent: no sessionId provided; event will be published without a session. Pass eventData.sessionId to attribute it to a task.";

    internal static string Published(string sessionId)
        => sessionId.Length > 0
            ? "Published custom event for session " + sessionId + " with type '" + EventType + "'"
            : "Published custom event without a session with type '" + EventType + "'";

    // The JSON fields are copied: the pipeline reads them later on its own thread, and the caller may reuse or change
    // its objects as soon as the call returns.
    internal static AgentCatEvent Build(string sessionId, CustomEventData? data, DateTimeOffset now, IAgentCatLog log)
    {
        var e = new AgentCatEvent
        {
            SessionId = sessionId,
            EventType = EventType,
            Timestamp = now,
            ResourceName = data?.ResourceName,
            Parameters = JsonTreeCopy.Copy(data?.Parameters),
            Response = JsonTreeCopy.Copy(data?.Response),
            UserIntent = data?.Message,
            Duration = data?.Duration,
            IsError = data?.IsError,
            Error = JsonTreeCopy.Copy(data?.Error),
        };

        if (data?.Tags is not null)
        {
            e.Tags = TagValidator.Validate(data.Tags, log);
        }

        if (data?.Properties is { Count: > 0 } properties)
        {
            e.Properties = (JsonObject?)JsonTreeCopy.Copy(properties);
        }

        return e;
    }
}
