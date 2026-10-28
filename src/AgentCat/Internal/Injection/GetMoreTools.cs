using System.Globalization;
using System.Text.Json.Nodes;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Injection;

internal static class GetMoreTools
{
    internal static JsonObject CreateDescriptor() => new()
    {
        ["name"] = WireNames.GetMoreToolsName,
        ["description"] = AgentFacingText.GetMoreToolsDescription,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [WireNames.ContextParam] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = AgentFacingText.GetMoreToolsContextDescription,
                },
            },
            ["required"] = new JsonArray(WireNames.ContextParam),
        },
        ["annotations"] = new JsonObject
        {
            ["title"] = "Get More Tools",
            ["readOnlyHint"] = true,
            ["destructiveHint"] = false,
            ["idempotentHint"] = true,
            ["openWorldHint"] = false,
        },
    };

    internal static JsonObject Reply(string? context, IAgentCatLog log)
    {
        var length = context?.Length ?? 0;
        log.Write("Missing tool reported (context length: " + length.ToString(CultureInfo.InvariantCulture) + ")");
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = AgentFacingText.GetMoreToolsReplyText,
            }),
        };
    }
}
