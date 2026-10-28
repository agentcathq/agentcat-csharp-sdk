using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Api.Model;
using AgentCat.Internal.Json;

namespace AgentCat.Internal.Delivery;

internal static class WireBody
{
    private static readonly PublishEventRequestJsonConverter Converter = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = AgentCatJsonContext.Default,
    };

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static byte[] Build(AgentCatEvent e)
    {
        var projectId = e.ProjectId;
        if (projectId is null || projectId.Length == 0)
        {
            throw new ArgumentException("The event has no project ID.", nameof(e));
        }

        if (string.IsNullOrEmpty(e.Id))
        {
            throw new ArgumentException("The event has no ID.", nameof(e));
        }

        var eventType = PublishEventRequest.EventTypeEnumFromStringOrDefault(e.EventType ?? "")
            ?? throw new ArgumentException("The event type is not one the API accepts.", nameof(e));

        var request = new PublishEventRequest(projectId: projectId)
        {
            Id = e.Id,
            SessionId = string.IsNullOrEmpty(e.SessionId) ? null : e.SessionId,
            Timestamp = e.Timestamp.UtcDateTime,
            EventType = eventType,
        };

        if (e.Duration is { } duration)
        {
            request.Duration = duration;
        }

        if (e.InputTokens is { } inputTokens)
        {
            request.InputTokens = inputTokens;
        }

        if (e.OutputTokens is { } outputTokens)
        {
            request.OutputTokens = outputTokens;
        }

        if (e.IsError is { } isError)
        {
            request.IsError = isError;
        }

        if (e.ResourceName is not null)
        {
            request.ResourceName = e.ResourceName;
        }

        if (e.UserIntent is not null)
        {
            request.UserIntent = e.UserIntent;
        }

        if (e.IdentifyActorGivenId is not null)
        {
            request.IdentifyActorGivenId = e.IdentifyActorGivenId;
            request.ActorId = e.IdentifyActorGivenId;
        }

        if (e.IdentifyActorName is not null)
        {
            request.IdentifyActorName = e.IdentifyActorName;
        }

        if (e.IpAddress is not null)
        {
            request.IpAddress = e.IpAddress;
        }

        if (e.SdkLanguage is not null)
        {
            request.SdkLanguage = e.SdkLanguage;
        }

        if (e.AgentCatVersion is not null)
        {
            request.AgentcatVersion = e.AgentCatVersion;
        }

        if (e.ServerName is not null)
        {
            request.ServerName = e.ServerName;
        }

        if (e.ServerVersion is not null)
        {
            request.ServerVersion = e.ServerVersion;
        }

        if (e.ClientName is not null)
        {
            request.ClientName = e.ClientName;
        }

        if (e.ClientVersion is not null)
        {
            request.ClientVersion = e.ClientVersion;
        }

        if (e.Parameters is not null)
        {
            request.Parameters = Raw(e.Parameters);
        }

        if (e.Response is not null)
        {
            request.Response = Raw(e.Response);
        }

        if (e.Error is not null)
        {
            request.Error = Raw(e.Error);
        }

        if (e.IdentifyActorData is not null)
        {
            request.IdentifyData = Raw(e.IdentifyActorData);
        }

        if (e.Properties is not null)
        {
            request.Properties = Raw(e.Properties);
        }

        if (e.Tags is not null)
        {
            var tags = new Dictionary<string, string>(e.Tags.Count);
            foreach (var tag in e.Tags)
            {
                tags[tag.Key] = tag.Value;
            }

            request.Tags = tags;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Converter.Write(writer, request, SerializerOptions);
        }

        return buffer.ToArray();
    }

    private static RawJson Raw(JsonNode node) => new(JsJson.ToUtf8(node));
}
