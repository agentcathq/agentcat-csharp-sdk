using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using AgentCat.Internal.Json;

namespace AgentCat;

/// <summary>
/// A tool-call or custom event before delivery. Customer <c>RedactEvent</c> hooks receive and return this type.
/// </summary>
public sealed class AgentCatEvent
{
    /// <summary>Unique identifier of the event.</summary>
    /// <remarks>A <c>RedactEvent</c> hook cannot change this value: the original is restored after the hook returns.</remarks>
    public string Id { get; set; } = "";

    /// <summary>Identifier of the session the event belongs to.</summary>
    /// <remarks>A <c>RedactEvent</c> hook cannot change this value: the original is restored after the hook returns.</remarks>
    public string SessionId { get; set; } = "";

    /// <summary>Identifier of the project the event is published under, if any.</summary>
    /// <remarks>A <c>RedactEvent</c> hook cannot change this value: the original is restored after the hook returns.</remarks>
    public string? ProjectId { get; set; }

    /// <summary>Event type, for example <c>mcp:tools/call</c> or <c>agentcat:custom</c>.</summary>
    /// <remarks>A <c>RedactEvent</c> hook cannot change this value: the original is restored after the hook returns.</remarks>
    public string EventType { get; set; } = "";

    /// <summary>Time at which the event occurred.</summary>
    /// <remarks>Sent as UTC. A <c>RedactEvent</c> hook cannot change this value: the original is restored after the hook returns.</remarks>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Duration of the operation in milliseconds.</summary>
    public int? Duration { get; set; }

    /// <summary>Estimated token count of the request payload.</summary>
    public int? InputTokens { get; set; }

    /// <summary>Estimated token count of the response payload.</summary>
    public int? OutputTokens { get; set; }

    /// <summary>IP address of the client, if known.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Language of the SDK that captured the event.</summary>
    public string? SdkLanguage { get; set; }

    /// <summary>Version of the AgentCat SDK that captured the event.</summary>
    public string? AgentCatVersion { get; set; }

    /// <summary>Name of the MCP server.</summary>
    public string? ServerName { get; set; }

    /// <summary>Version of the MCP server.</summary>
    public string? ServerVersion { get; set; }

    /// <summary>Name of the MCP client.</summary>
    public string? ClientName { get; set; }

    /// <summary>Version of the MCP client.</summary>
    public string? ClientVersion { get; set; }

    /// <summary>Customer-supplied identifier of the identified actor.</summary>
    public string? IdentifyActorGivenId { get; set; }

    /// <summary>Display name of the identified actor.</summary>
    public string? IdentifyActorName { get; set; }

    /// <summary>Customer-supplied data about the identified actor.</summary>
    public JsonObject? IdentifyActorData { get; set; }

    /// <summary>Name of the tool or resource the event concerns.</summary>
    public string? ResourceName { get; set; }

    /// <summary>Request parameters of the call.</summary>
    public JsonNode? Parameters { get; set; }

    /// <summary>Response of the call.</summary>
    public JsonNode? Response { get; set; }

    /// <summary>The actor's stated reason for the call.</summary>
    public string? UserIntent { get; set; }

    /// <summary>Whether the call ended in an error.</summary>
    public bool? IsError { get; set; }

    /// <summary>Captured error details, if the call failed.</summary>
    public JsonNode? Error { get; set; }

    /// <summary>Customer-defined string tags.</summary>
    public IDictionary<string, string>? Tags { get; set; }

    /// <summary>Customer-defined properties attached to the event.</summary>
    public JsonObject? Properties { get; set; }

    /// <summary>Creates a deep copy: JSON nodes are cloned and tags are copied into a new dictionary.</summary>
    /// <returns>An independent copy of this event.</returns>
    public AgentCatEvent Clone() => new()
    {
        Id = Id,
        SessionId = SessionId,
        ProjectId = ProjectId,
        EventType = EventType,
        Timestamp = Timestamp,
        Duration = Duration,
        InputTokens = InputTokens,
        OutputTokens = OutputTokens,
        IpAddress = IpAddress,
        SdkLanguage = SdkLanguage,
        AgentCatVersion = AgentCatVersion,
        ServerName = ServerName,
        ServerVersion = ServerVersion,
        ClientName = ClientName,
        ClientVersion = ClientVersion,
        IdentifyActorGivenId = IdentifyActorGivenId,
        IdentifyActorName = IdentifyActorName,
        IdentifyActorData = (JsonObject?)JsonTreeCopy.Copy(IdentifyActorData),
        ResourceName = ResourceName,
        Parameters = JsonTreeCopy.Copy(Parameters),
        Response = JsonTreeCopy.Copy(Response),
        UserIntent = UserIntent,
        IsError = IsError,
        Error = JsonTreeCopy.Copy(Error),
        Tags = Tags is null ? null : new Dictionary<string, string>(Tags),
        Properties = (JsonObject?)JsonTreeCopy.Copy(Properties),
    };
}
