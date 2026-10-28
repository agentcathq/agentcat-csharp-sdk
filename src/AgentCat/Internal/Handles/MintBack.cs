using System;
using System.Text.Json.Nodes;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Handles;

internal static class MintBack
{
    internal static string? BuildText(HandleResolution resolution)
    {
        if (resolution.HookMode)
        {
            return null;
        }

        return resolution.SessionSource switch
        {
            SessionSource.Minted => AgentFacingText.MintBackHeaderIssued + "\n"
                + AgentFacingText.MintBackSessionLine(resolution.SessionId) + "\n"
                + AgentFacingText.MintBackIssuedBody,
            SessionSource.Invalid => AgentFacingText.MintBackHeaderUnrecognized + "\n" + AgentFacingText.MintBackUnrecognizedBody,
            _ => null,
        };
    }

    internal static JsonObject? BuildStructured(HandleResolution resolution)
    {
        var sessionOurs = !resolution.HookMode && resolution.SessionSource != SessionSource.Foreign;
        var mint = new JsonObject();
        if (sessionOurs && resolution.SessionSource != SessionSource.Invalid)
        {
            mint[WireNames.SessionIdParam] = resolution.SessionId;
        }

        if (resolution.AgentId is { Length: > 0 } agentId)
        {
            mint[WireNames.AgentIdParam] = agentId;
        }

        if (sessionOurs)
        {
            mint[WireNames.McpSessionStatusKey] = resolution.SessionSource switch
            {
                SessionSource.Minted => WireNames.McpSessionStatusIssued,
                SessionSource.Invalid => WireNames.McpSessionStatusUnrecognized,
                _ => WireNames.McpSessionStatusActive,
            };
        }

        return mint.Count > 0 ? mint : null;
    }

    internal static JsonObject? MirrorStructured(JsonObject structuredContent, JsonObject mint)
    {
        foreach (var property in structuredContent)
        {
            if (string.Equals(property.Key, WireNames.McpSessionKey, StringComparison.Ordinal))
            {
                return null;
            }
        }

        var mirrored = new JsonObject { [WireNames.McpSessionKey] = mint.DeepClone() };
        foreach (var property in structuredContent)
        {
            mirrored.Add(property.Key, property.Value?.DeepClone());
        }

        return mirrored;
    }
}
