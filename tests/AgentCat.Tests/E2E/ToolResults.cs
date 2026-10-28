using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentCat.Tests.E2E;

// Reads of tool results, as the TS e2e helpers do them.
internal static class ToolResults
{
    // content[0] when it is the session text block (TS mintBackOf).
    internal static string? MintBackOf(CallToolResult result)
        => result.Content.Count > 0 && result.Content[0] is TextContentBlock block && block.Text.StartsWith("[session_id", StringComparison.Ordinal)
            ? block.Text
            : null;

    // The value after "name: " (TS handleFrom).
    internal static string? HandleFrom(string text, string name)
        => Regex.Match(text, Regex.Escape(name) + @": (\S+)", RegexOptions.CultureInvariant) is { Success: true } match ? match.Groups[1].Value : null;

    internal static IReadOnlyList<string> Texts(CallToolResult result)
        => result.Content.OfType<TextContentBlock>().Select(b => b.Text).ToArray();

    // The tool's own text: the last text block, after any session text block.
    internal static string ToolText(CallToolResult result)
    {
        var texts = Texts(result);
        return texts.Count > 0 ? texts[texts.Count - 1] : throw new InvalidOperationException("The result has no text block.");
    }

    // A well-formed ses_ value that AgentCat never issued (TS sid()).
    internal static string Sid(string label)
        => string.Concat("ses_", (new string(label.Where(char.IsAsciiLetterOrDigit).ToArray()) + new string('0', 27)).AsSpan(0, 27));

    internal static JsonObject InputSchema(Tool tool) => JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();

    internal static JsonObject? OutputSchema(Tool tool)
        => tool.OutputSchema is { } schema ? JsonNode.Parse(schema.GetRawText())!.AsObject() : null;

    // The names in a schema's "required" array; empty when the schema has none.
    internal static IReadOnlyList<string?> Required(JsonObject schema)
        => schema["required"] is JsonArray required ? required.Select(n => (string?)n).ToArray() : Array.Empty<string?>();

    // The result as the client received it, read as JSON (the member types differ between MCP versions).
    internal static JsonObject Json(CallToolResult result)
        => JsonNode.Parse(JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult))))!.AsObject();

    internal static JsonObject? StructuredContent(CallToolResult result) => Json(result)["structuredContent"] as JsonObject;
}
