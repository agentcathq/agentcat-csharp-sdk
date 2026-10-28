using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Json;

namespace AgentCat.Internal.Events;

internal static class EventRedactor
{
    internal static async ValueTask RedactStringsAsync(
        AgentCatEvent e,
        Func<string, CancellationToken, ValueTask<string>> redact,
        CancellationToken ct)
    {
        var ipAddress = await RedactStringAsync(e.IpAddress, redact, ct).ConfigureAwait(false);
        var sdkLanguage = await RedactStringAsync(e.SdkLanguage, redact, ct).ConfigureAwait(false);
        var agentCatVersion = await RedactStringAsync(e.AgentCatVersion, redact, ct).ConfigureAwait(false);
        var identifyActorData = (JsonObject?)await RedactNodeAsync(e.IdentifyActorData, redact, ct).ConfigureAwait(false);
        var parameters = await RedactNodeAsync(e.Parameters, redact, ct).ConfigureAwait(false);
        var response = await RedactNodeAsync(e.Response, redact, ct).ConfigureAwait(false);
        var userIntent = await RedactStringAsync(e.UserIntent, redact, ct).ConfigureAwait(false);
        var error = await RedactNodeAsync(e.Error, redact, ct).ConfigureAwait(false);

        e.IpAddress = ipAddress;
        e.SdkLanguage = sdkLanguage;
        e.AgentCatVersion = agentCatVersion;
        e.IdentifyActorData = identifyActorData;
        e.Parameters = parameters;
        e.Response = response;
        e.UserIntent = userIntent;
        e.Error = error;
    }

    internal static async ValueTask<bool> ApplyEventHookAsync(
        AgentCatEvent e,
        Func<AgentCatEvent, CancellationToken, ValueTask<AgentCatEvent?>> hook,
        CancellationToken ct)
    {
        var id = e.Id;
        var sessionId = e.SessionId;
        var projectId = e.ProjectId;
        var eventType = e.EventType;
        var timestamp = e.Timestamp;

        var result = await hook(e.Clone(), ct).ConfigureAwait(false);
        if (result is null)
        {
            return false;
        }

        EventShape.CopyInto(result, e);
        e.Id = id;
        e.SessionId = sessionId;
        e.ProjectId = projectId;
        e.EventType = eventType;
        e.Timestamp = timestamp;
        return true;
    }

    private static async ValueTask<string?> RedactStringAsync(
        string? value,
        Func<string, CancellationToken, ValueTask<string>> redact,
        CancellationToken ct)
    {
        if (value is null)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        return await redact(value, ct).ConfigureAwait(false);
    }

    private static async ValueTask<JsonNode?> RedactNodeAsync(
        JsonNode? root,
        Func<string, CancellationToken, ValueTask<string>> redact,
        CancellationToken ct)
    {
        var copy = new JsonTreeCopy(root);
        while (copy.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            copy.Replace(await redact(copy.Current, ct).ConfigureAwait(false));
        }

        return copy.Result;
    }
}
