using System;

namespace AgentCat.Internal.Delivery;

internal static class ApiBaseUrl
{
    internal const string Default = "https://api.agentcat.com";

    internal const string EventsPath = "/v1/public/events/";

    internal static string? Resolve(string? option, Func<string, string?> environment)
    {
        if (!string.IsNullOrEmpty(option))
        {
            return option;
        }

        var current = environment("AGENTCAT_API_URL");
        if (!string.IsNullOrEmpty(current))
        {
            return current;
        }

        var legacy = environment("MCPCAT_API_URL");
        return string.IsNullOrEmpty(legacy) ? null : legacy;
    }

    internal static string EventsEndpoint(string baseUrl)
        => (baseUrl.Length > 0 && baseUrl[baseUrl.Length - 1] == '/' ? baseUrl.Substring(0, baseUrl.Length - 1) : baseUrl) + EventsPath;
}
