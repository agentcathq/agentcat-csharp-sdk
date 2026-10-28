using System;
using System.Collections.Generic;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Handles;

internal static class HandleTags
{
    private const int MaxAgentIdTagLength = 200;

    internal static Dictionary<string, string> Build(HandleResolution resolution, string? protocolVersion)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WireNames.TagSessionSource] = resolution.SessionSource.ToWire(),
        };

        if (resolution.AgentId is { Length: > 0 } agentId)
        {
            var tag = agentId.Replace('\r', ' ').Replace('\n', ' ');
            tags[WireNames.TagAgentId] = tag.Length > MaxAgentIdTagLength ? tag.Substring(0, MaxAgentIdTagLength) : tag;
            tags[WireNames.TagAgentSource] = WireNames.AgentSourceSupplied;
        }

        if (protocolVersion is { Length: > 0 })
        {
            tags[WireNames.TagProtocolVersion] = protocolVersion;
        }

        return tags;
    }
}
