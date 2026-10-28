using System.Linq.Expressions;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.TestSupport;

internal static class Mcp
{
    public static CallToolResult Result(string json)
        => (CallToolResult)JsonSerializer.Deserialize(json, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult)))!;

    public static ListToolsResult ListResult(string json)
        => (ListToolsResult)JsonSerializer.Deserialize(json, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListToolsResult)))!;

    public static ListToolsRequestParams ListParams(string? cursor)
        => (ListToolsRequestParams)JsonSerializer.Deserialize(
            cursor is null ? "{}" : "{\"cursor\":" + JsonSerializer.Serialize(cursor) + "}",
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListToolsRequestParams)))!;

    public static CallToolRequestParams CallToolParams(string json)
        => (CallToolRequestParams)JsonSerializer.Deserialize(json, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolRequestParams)))!;

    // Handlers.CallToolWithAlternateHandler exists only on ModelContextProtocol 2.x.
    public static void SetAlternateHandler(McpServerOptions options)
    {
        var property = typeof(McpServerHandlers).GetProperty("CallToolWithAlternateHandler")!;
        var invoke = property.PropertyType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var handler = Expression.Lambda(property.PropertyType, Expression.Default(invoke.ReturnType), parameters).Compile();
        property.SetValue(options.Handlers, handler);
    }
}
