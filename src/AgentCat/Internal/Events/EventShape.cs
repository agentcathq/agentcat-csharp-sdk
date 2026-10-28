using System.Globalization;
using System.Text.Json.Nodes;

namespace AgentCat.Internal.Events;

internal static class EventShape
{
    internal static JsonObject ToTsJson(AgentCatEvent e)
    {
        var o = new JsonObject
        {
            ["id"] = e.Id,
            ["sessionId"] = e.SessionId,
        };
        Add(o, "projectId", e.ProjectId);
        o["eventType"] = e.EventType;
        o["timestamp"] = e.Timestamp.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'", CultureInfo.InvariantCulture);
        Add(o, "duration", e.Duration);
        Add(o, "inputTokens", e.InputTokens);
        Add(o, "outputTokens", e.OutputTokens);
        Add(o, "ipAddress", e.IpAddress);
        Add(o, "sdkLanguage", e.SdkLanguage);
        Add(o, "agentcatVersion", e.AgentCatVersion);
        Add(o, "serverName", e.ServerName);
        Add(o, "serverVersion", e.ServerVersion);
        Add(o, "clientName", e.ClientName);
        Add(o, "clientVersion", e.ClientVersion);
        Add(o, "identifyActorGivenId", e.IdentifyActorGivenId);
        Add(o, "identifyActorName", e.IdentifyActorName);
        AddNode(o, "identifyActorData", e.IdentifyActorData);
        Add(o, "resourceName", e.ResourceName);
        AddNode(o, "parameters", e.Parameters);
        AddNode(o, "response", e.Response);
        Add(o, "userIntent", e.UserIntent);
        Add(o, "isError", e.IsError);
        AddNode(o, "error", e.Error);
        if (e.Tags is not null)
        {
            var tags = new JsonObject();
            foreach (var kv in e.Tags)
            {
                tags[kv.Key] = kv.Value;
            }

            o["tags"] = tags;
        }

        AddNode(o, "properties", e.Properties);
        return o;
    }

    internal static void CopyInto(AgentCatEvent source, AgentCatEvent target)
    {
        target.Id = source.Id;
        target.SessionId = source.SessionId;
        target.ProjectId = source.ProjectId;
        target.EventType = source.EventType;
        target.Timestamp = source.Timestamp;
        target.Duration = source.Duration;
        target.InputTokens = source.InputTokens;
        target.OutputTokens = source.OutputTokens;
        target.IpAddress = source.IpAddress;
        target.SdkLanguage = source.SdkLanguage;
        target.AgentCatVersion = source.AgentCatVersion;
        target.ServerName = source.ServerName;
        target.ServerVersion = source.ServerVersion;
        target.ClientName = source.ClientName;
        target.ClientVersion = source.ClientVersion;
        target.IdentifyActorGivenId = source.IdentifyActorGivenId;
        target.IdentifyActorName = source.IdentifyActorName;
        target.IdentifyActorData = source.IdentifyActorData;
        target.ResourceName = source.ResourceName;
        target.Parameters = source.Parameters;
        target.Response = source.Response;
        target.UserIntent = source.UserIntent;
        target.IsError = source.IsError;
        target.Error = source.Error;
        target.Tags = source.Tags;
        target.Properties = source.Properties;
    }

    private static void Add(JsonObject o, string key, string? value)
    {
        if (value is not null)
        {
            o[key] = value;
        }
    }

    private static void Add(JsonObject o, string key, int? value)
    {
        if (value is not null)
        {
            o[key] = value.Value;
        }
    }

    private static void Add(JsonObject o, string key, bool? value)
    {
        if (value is not null)
        {
            o[key] = value.Value;
        }
    }

    private static void AddNode(JsonObject o, string key, JsonNode? value)
    {
        if (value is not null)
        {
            o[key] = value.DeepClone();
        }
    }
}
