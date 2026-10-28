using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using AgentCat.Internal.Logging;

namespace AgentCat.Internal.Events;

internal static class TagValidator
{
    private const int MaxKeyLength = 32;
    private const int MaxValueLength = 200;
    private const int MaxEntries = 50;
    private const string Dash = "—";

    internal static Dictionary<string, string>? Validate(IEnumerable<KeyValuePair<string, string>>? tags, IAgentCatLog log)
    {
        if (tags is null)
        {
            return null;
        }

        var valid = new List<KeyValuePair<string, string>>();
        var seen = new Dictionary<string, int>(System.StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            string? key = tag.Key;
            string? value = tag.Value;

            if (!IsValidKey(key))
            {
                log.Write($"Dropping invalid tag: \"{key}\" {Dash} key contains invalid characters or is empty", LogSeverity.Warning);
                continue;
            }

            if (key.Length > MaxKeyLength)
            {
                log.Write($"Dropping invalid tag: \"{key}\" {Dash} key exceeds max length of {MaxKeyLength}", LogSeverity.Warning);
                continue;
            }

            if (value is null)
            {
                log.Write($"Dropping invalid tag: \"{key}\" {Dash} non-string value (got object)", LogSeverity.Warning);
                continue;
            }

            if (value.Length > MaxValueLength)
            {
                log.Write($"Dropping invalid tag: \"{key}\" {Dash} value exceeds max length of {MaxValueLength}", LogSeverity.Warning);
                continue;
            }

            if (ContainsNewline(value))
            {
                log.Write($"Dropping invalid tag: \"{key}\" {Dash} value contains newline character", LogSeverity.Warning);
                continue;
            }

            if (seen.TryGetValue(key, out var index))
            {
                valid[index] = new KeyValuePair<string, string>(key, value);
                continue;
            }

            seen[key] = valid.Count;
            valid.Add(new KeyValuePair<string, string>(key, value));
        }

        if (valid.Count == 0)
        {
            return null;
        }

        if (valid.Count > MaxEntries)
        {
            log.Write($"Dropping {valid.Count - MaxEntries} tag(s) {Dash} exceeds maximum of {MaxEntries} entries per event", LogSeverity.Warning);
            valid.RemoveRange(MaxEntries, valid.Count - MaxEntries);
        }

        var result = new Dictionary<string, string>(valid.Count, System.StringComparer.Ordinal);
        foreach (var pair in valid)
        {
            result.Add(pair.Key, pair.Value);
        }

        return result;
    }

    private static bool ContainsNewline(string value)
    {
#if NET
        return value.Contains('\n');
#else
        return value.IndexOf('\n') >= 0;
#endif
    }

    private static bool IsValidKey([NotNullWhen(true)] string? key)
    {
        if (key is null || key.Length == 0)
        {
            return false;
        }

        foreach (var c in key)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c == '$' || c == '_' || c == '.' || c == ':' || c == '-' || c == ' ';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
