using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Injection;

internal static class ArgumentStripper
{
    private static readonly string[] AllInjectable =
    {
        WireNames.SessionIdParam,
        WireNames.AgentIdParam,
        WireNames.ContextParam,
    };

    private static readonly string[] GetMoreToolsInjectable =
    {
        WireNames.SessionIdParam,
        WireNames.AgentIdParam,
    };

    internal static IDictionary<string, JsonElement>? Strip(
        IDictionary<string, JsonElement>? arguments,
        string? toolName,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? registry)
    {
        if (arguments is null)
        {
            return null;
        }

        IReadOnlyCollection<string>? recorded = null;
        if (toolName is not null && registry is not null)
        {
            registry.TryGetValue(toolName, out recorded);
        }

        IReadOnlyCollection<string> names = recorded
            ?? (toolName == WireNames.GetMoreToolsName ? GetMoreToolsInjectable : AllInjectable);

        var present = false;
        foreach (var name in names)
        {
            if (arguments.ContainsKey(name))
            {
                present = true;
                break;
            }
        }
        if (!present)
        {
            return arguments;
        }

        var cleaned = new Dictionary<string, JsonElement>();
        foreach (var pair in arguments)
        {
            if (!names.Contains(pair.Key))
            {
                cleaned[pair.Key] = pair.Value;
            }
        }
        return cleaned;
    }
}
