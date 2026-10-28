using System.Text.Json.Nodes;

namespace AgentCat.Tests.TestSupport;

internal static class Fixtures
{
    public static string Text(string relativePath)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));

    public static JsonNode Json(string relativePath)
        => JsonNode.Parse(Text(relativePath))!;
}
