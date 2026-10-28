using System;
using System.Collections.Generic;
using System.Text.Json;
using AgentCat.Internal.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentCat.Internal.Tokens;

internal static class TokenEstimator
{
    internal const double BytesPerToken = 3.5;

    private const int MaxTokenCount = int.MaxValue;

    internal static long Utf8ByteLength(string text)
    {
        long bytes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var code = text[i];
            if (code < 0x80)
            {
                bytes += 1;
            }
            else if (code < 0x800)
            {
                bytes += 2;
            }
            else if (char.IsHighSurrogate(code) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                bytes += 4;
                i++;
            }
            else
            {
                bytes += 3;
            }
        }

        return bytes;
    }

    internal static int EstimateTokens(long bytes)
    {
        if (bytes <= 0)
        {
            return 0;
        }

        var tokens = Math.Ceiling(bytes / BytesPerToken);
        return tokens >= MaxTokenCount ? MaxTokenCount : (int)tokens;
    }

    internal static int? EstimateInputTokens(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        try
        {
            return arguments is null ? null : EstimateTokens(JsJson.LengthOfObject(arguments));
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static int? EstimateOutputTokens(CallToolResult? result)
    {
        try
        {
            if (result is null)
            {
                return null;
            }

            var content = result.Content;
            if (content is null)
            {
                var typeInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult));
                return EstimateTokens(JsJson.Length(JsonSerializer.SerializeToElement(result, typeInfo)));
            }

            if (content.Count == 0)
            {
                return result.StructuredContent is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } structured
                    ? EstimateTokens(JsJson.Length(structured))
                    : 0;
            }

            long bytes = 0;
            foreach (var block in content)
            {
                bytes += BlockBytes(block);
            }

            return EstimateTokens(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long BlockBytes(ContentBlock? block) => block switch
    {
        TextContentBlock { Text: { } text } => Utf8ByteLength(text),
        EmbeddedResourceBlock { Resource: TextResourceContents { Text: { } text } } => Utf8ByteLength(text),
        _ => 0,
    };
}
