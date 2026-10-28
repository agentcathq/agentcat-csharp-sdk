using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace AgentCat;

/// <summary>The data of a custom event that you publish with <see cref="IAgentCat.PublishCustomEventAsync(string, string, CustomEventData?)"/>.</summary>
public sealed class CustomEventData
{
    /// <summary>
    /// The session that the event belongs to. When it is <see langword="null"/> or empty, the string form uses its
    /// first argument as the session, and the server form publishes the event without a session.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>The name of the resource that the event describes.</summary>
    public string? ResourceName { get; set; }

    /// <summary>The input of the action that the event describes.</summary>
    public JsonNode? Parameters { get; set; }

    /// <summary>The output of the action that the event describes.</summary>
    public JsonNode? Response { get; set; }

    /// <summary>Why the action happened. AgentCat shows it as the intent of the event.</summary>
    public string? Message { get; set; }

    /// <summary>The duration of the action in milliseconds.</summary>
    public int? Duration { get; set; }

    /// <summary>Whether the action failed. <see langword="false"/> is kept and sent.</summary>
    public bool? IsError { get; set; }

    /// <summary>The error of the action, sent as given.</summary>
    public JsonNode? Error { get; set; }

    /// <summary>Tags for the event. AgentCat drops invalid tags and logs each drop.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; set; }

    /// <summary>Free-form properties for the event. An object without keys is not sent.</summary>
    public JsonObject? Properties { get; set; }
}
