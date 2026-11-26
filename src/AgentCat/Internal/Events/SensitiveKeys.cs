using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Json;

namespace AgentCat.Internal.Events;

// Masks the values of keys that name a secret (credential headers, tokens, passwords) in an event payload, at any
// depth. A port of the reference implementation that every AgentCat SDK shares; the shared vectors in
// tests/AgentCat.Tests/Fixtures/sensitive-keys/vectors.json check it.
internal static class SensitiveKeys
{
    internal const string Mask = "[redacted]";

    // A key matches a name when its canonical form equals the name or ends with "-" + name.
    private static readonly string[] Exact =
    {
        "accesskey", "cb-access-sign", "cf-access-client-id", "dpop", "fastly-key",
        "jenkins-crumb", "paypal-transmission-sig", "plaid-verification",
        "sec-websocket-protocol", "x-airtable-content-mac", "x-amzn-ava-user-context",
        "x-amzn-oidc-data", "x-firebase-appcheck", "x-forge-oauth-system",
        "x-forge-oauth-user", "x-honeycomb-team", "x-insert-key", "x-jfrog-art-api",
        "x-nf-sign", "x-registry-config", "x-requestdigest", "x-sfdc-session",
        "x-trello-webhook", "x-vault-mfa", "x-vercel-protection-bypass", "x-wp-nonce",
    };

    // A key matches when its canonical form contains the word.
    private static readonly string[] Contains =
    {
        "authorization", "secret", "password", "passwd", "passphrase", "cookie", "signature",
        "credential", "jwt", "assertion", "bearer", "csrf", "xsrf", "hmac", "saml",
        "api-key", "apikey", "access-key", "private-key", "privatekey", "subscription-key",
        "encryption-key", "signing-key", "shared-key", "sharedkey", "master-key",
        "account-key", "app-key", "appkey", "application-key", "functions-key", "sas-key",
        "license-key", "consumer-key", "client-key", "customer-key",
    };

    // A key matches when one hyphen-separated part of its canonical form equals the word.
    private static readonly string[] Segment = { "auth", "token", "otp" };

    // A key matches when its canonical form ends with the word.
    private static readonly string[] Suffix = { "token" };

    // The pair rule: a map that names a key in one entry and holds its value in another.
    private static readonly string[] PairNameKeys = { "name", "key", "header" };
    private static readonly string[] PairValueKeys = { "value", "values" };

    // The children of a map stored under this exact key are JSON Schema property names.
    private const string SchemaPropertiesKey = "properties";

    internal static string CamelSplit(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (i > 0 && ch >= 'A' && ch <= 'Z')
            {
                var prev = text[i - 1];
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if ((prev >= 'a' && prev <= 'z') || (prev >= '0' && prev <= '9'))
                {
                    result.Append('-');
                }
                else if (prev >= 'A' && prev <= 'Z' && next >= 'a' && next <= 'z')
                {
                    result.Append('-');
                }
            }

            result.Append(ch);
        }

        return result.ToString();
    }

    internal static List<string> CanonicalParts(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch >= 'A' && ch <= 'Z')
            {
                result.Append((char)(ch + 32));
            }
            else if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                result.Append(ch);
            }
            else
            {
                result.Append('-');
            }
        }

        var parts = result.ToString().Split('-').Where(p => p.Length > 0).ToList();
        while (parts.Count > 0 && parts[parts.Count - 1].All(c => c >= '0' && c <= '9'))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts;
    }

    internal static bool PartsMatch(List<string> parts)
    {
        if (parts.Count == 0)
        {
            return false;
        }

        var joined = string.Join("-", parts);
        foreach (var name in Exact)
        {
            if (joined == name || joined.EndsWith("-" + name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var word in Contains)
        {
            if (joined.Contains(word))
            {
                return true;
            }
        }

        foreach (var part in parts)
        {
            if (Segment.Contains(part))
            {
                return true;
            }
        }

        foreach (var word in Suffix)
        {
            if (joined.EndsWith(word, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsSensitiveKey(string key)
        => PartsMatch(CanonicalParts(key)) || PartsMatch(CanonicalParts(CamelSplit(key)));

    internal static string Canonical(string key) => string.Join("-", CanonicalParts(key));

    private static JsonNode? MaskValue(JsonNode? value, bool underProperties, int depth)
    {
        var node = value is null ? null : JsonTreeCopy.Expand(value);
        if (node is null || node.GetValueKind() is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False)
        {
            return JsonTreeCopy.Copy(node);
        }

        if (node is JsonObject && underProperties)
        {
            return MaskSensitiveKeys(node, false, depth);
        }

        return JsonValue.Create(Mask);
    }

    // RuntimeHelpers.TryEnsureSufficientExecutionStack is not in netstandard2.0.
    private static bool StackHasRoom()
    {
        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            return true;
        }
        catch (InsufficientExecutionStackException)
        {
            return false;
        }
    }

    // A masked copy of value; value itself never changes. underProperties is true when value is the map stored under
    // the key "properties". As in JsJson, a map or list deeper than JsJson.MaxDepth (which the SDK cannot serialize)
    // becomes the mask. So does a map or list that the thread stack has no room to walk: the stack of a secondary
    // thread on macOS is too small for JsJson.MaxDepth levels.
    internal static JsonNode? MaskSensitiveKeys(JsonNode? value, bool underProperties = false, int depth = 0)
    {
        var node = value is null ? null : JsonTreeCopy.Expand(value);
        if (node is JsonObject or JsonArray && (depth >= JsJson.MaxDepth || !StackHasRoom()))
        {
            return JsonValue.Create(Mask);
        }

        if (node is JsonArray list)
        {
            var items = new JsonArray(new JsonNodeOptions());
            foreach (var item in list)
            {
                items.Add(MaskSensitiveKeys(item, false, depth + 1));
            }

            return items;
        }

        if (node is not JsonObject map)
        {
            return JsonTreeCopy.Copy(node);
        }

        var entries = map.ToArray();
        var result = new JsonObject(new JsonNodeOptions());
        foreach (var entry in entries)
        {
            if (IsSensitiveKey(entry.Key))
            {
                result[entry.Key] = MaskValue(entry.Value, underProperties, depth + 1);
            }
            else
            {
                result[entry.Key] = MaskSensitiveKeys(entry.Value, entry.Key == SchemaPropertiesKey, depth + 1);
            }
        }

        var namesASensitiveKey = false;
        foreach (var entry in entries)
        {
            if (PairNameKeys.Contains(Canonical(entry.Key)))
            {
                var child = entry.Value is null ? null : JsonTreeCopy.Expand(entry.Value);
                if (child is JsonValue text && text.GetValueKind() == JsonValueKind.String && IsSensitiveKey(JsonTreeCopy.StringOf(text)))
                {
                    namesASensitiveKey = true;
                }
            }
        }

        if (namesASensitiveKey)
        {
            foreach (var entry in entries)
            {
                if (PairValueKeys.Contains(Canonical(entry.Key)))
                {
                    result[entry.Key] = MaskValue(entry.Value, false, depth + 1);
                }
            }
        }

        return result;
    }
}
