using System.Text.Json.Nodes;

namespace AgentCat.Tests.TestSupport;

// The RedactEvent sample of the AgentCatOptions XML docs, as written there.
internal static class RedactEventSample
{
    internal static readonly Func<AgentCatEvent, CancellationToken, ValueTask<AgentCatEvent?>> Hook = (e, _) =>
    {
        if (e.Parameters is JsonObject p && p["extra"] is JsonObject x && x["requestInfo"] is JsonObject info)
        {
            if (info["headers"] is JsonObject headers)
            {
                headers.Remove("authorization");
                headers.Remove("cookie");
            }

            if (info["url"] is JsonValue url && url.TryGetValue(out string? text) && text.IndexOf('?') is var query and >= 0)
            {
                info["url"] = text.Substring(0, query);
            }
        }

        return new ValueTask<AgentCatEvent?>(e);
    };
}
