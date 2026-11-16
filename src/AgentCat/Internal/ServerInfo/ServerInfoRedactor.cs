using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Json;

namespace AgentCat.Internal.ServerInfo;

internal static class ServerInfoRedactor
{
    // Runs the redaction function on the title, the instructions and every string value of the lists. The name, the URI
    // and the URI template of a list item stay as they are, and so does every key. A function that throws or returns
    // no string fails the whole redaction.
    internal static async Task<ServerInfoBody> RedactAsync(
        ServerInfoBody body,
        Func<string, CancellationToken, ValueTask<string>> redact,
        CancellationToken cancellationToken)
    {
        var title = await RedactStringAsync(body.ServerTitle, redact, cancellationToken).ConfigureAwait(false);
        var instructions = await RedactStringAsync(body.Instructions, redact, cancellationToken).ConfigureAwait(false);
        var features = new JsonObject();
        foreach (var list in body.Features)
        {
            if (list.Value is not JsonArray items)
            {
                features[list.Key] = await RedactNodeAsync(list.Value, redact, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var redacted = new JsonArray();
            foreach (var item in items)
            {
                redacted.Add(item is JsonObject fields
                    ? await RedactItemAsync(fields, redact, cancellationToken).ConfigureAwait(false)
                    : await RedactNodeAsync(item, redact, cancellationToken).ConfigureAwait(false));
            }

            features[list.Key] = redacted;
        }

        return new ServerInfoBody(body.ProjectId, body.ServerName, body.ServerVersion, title, instructions, features);
    }

    private static async Task<JsonObject> RedactItemAsync(
        JsonObject item,
        Func<string, CancellationToken, ValueTask<string>> redact,
        CancellationToken cancellationToken)
    {
        var redacted = new JsonObject();
        foreach (var field in item)
        {
            redacted[field.Key] = field.Key is "name" or "uri" or "uriTemplate"
                ? JsonTreeCopy.Copy(field.Value)
                : await RedactNodeAsync(field.Value, redact, cancellationToken).ConfigureAwait(false);
        }

        return redacted;
    }

    private static async Task<JsonNode?> RedactNodeAsync(
        JsonNode? root,
        Func<string, CancellationToken, ValueTask<string>> redact,
        CancellationToken cancellationToken)
    {
        var copy = new JsonTreeCopy(root);
        while (copy.MoveNext())
        {
            copy.Replace(await RedactStringAsync(copy.Current, redact, cancellationToken).ConfigureAwait(false));
        }

        return copy.Result;
    }

    private static async Task<string> RedactStringAsync(
        string value,
        Func<string, CancellationToken, ValueTask<string>> redact,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? redacted;
        try
        {
            redacted = await redact(value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HeartbeatFailure("the redaction function failed: " + HeartbeatFailure.ReasonOf(ex));
        }

        return redacted ?? throw new HeartbeatFailure("the redaction function returned no string");
    }
}
