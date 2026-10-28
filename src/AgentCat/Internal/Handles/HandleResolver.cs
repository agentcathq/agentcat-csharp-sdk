using System;
using System.Collections.Generic;
using System.Text.Json;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Json;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Handles;

internal static class HandleResolver
{
    internal static HandleResolution Resolve(
        IReadOnlyDictionary<string, JsonElement>? arguments,
        bool hookMode,
        bool agentTracking,
        bool sessionParamIsOurs,
        DateTimeOffset now)
    {
        string sessionId;
        SessionSource source;
        if (hookMode)
        {
            sessionId = string.Empty;
            source = SessionSource.Hook;
        }
        else if (!sessionParamIsOurs)
        {
            sessionId = string.Empty;
            source = SessionSource.Foreign;
        }
        else
        {
            var supplied = ExtractHandle(arguments, WireNames.SessionIdParam);
            if (supplied is null || IsStartSentinel(supplied))
            {
                sessionId = SessionIds.NewSessionId(now);
                source = SessionSource.Minted;
            }
            else if (SessionIds.IsValid(supplied))
            {
                sessionId = supplied;
                source = SessionSource.Supplied;
            }
            else
            {
                sessionId = string.Empty;
                source = SessionSource.Invalid;
            }
        }

        var agentId = agentTracking ? ExtractHandle(arguments, WireNames.AgentIdParam) : null;
        return new HandleResolution(sessionId, source, hookMode, agentId);
    }

    internal static (string SessionId, SessionSource Source) FromHookValue(string? value, string? projectId, DateTimeOffset now)
    {
        var trimmed = value is null ? string.Empty : JsSemantics.Trim(value);
        return trimmed.Length > 0
            ? (SessionIds.Derive(trimmed, projectId), SessionSource.Hook)
            : (SessionIds.NewSessionId(now), SessionSource.Minted);
    }

    internal static string? ExtractHandle(IReadOnlyDictionary<string, JsonElement>? arguments, string name)
    {
        if (arguments is null
            || !arguments.TryGetValue(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var trimmed = JsSemantics.Trim(JsJson.ReadString(value));
        return trimmed.Length > 0 ? trimmed : null;
    }

    private static bool IsStartSentinel(string value)
        => string.Equals(value.ToLowerInvariant(), AgentFacingText.SessionStartSentinel, StringComparison.Ordinal);
}
