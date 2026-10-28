using ModelContextProtocol.Protocol;

namespace AgentCat.Tests.E2E;

// AgentCat log lines that the end-to-end tests assert on.
internal static class ExpectedLog
{
    // The start of the line a server writes after the AgentCat API accepted an event: then the event ID and " |".
    internal const string SentPrefix = "Successfully sent event ";
    internal const string SetupStartedPrefix = "AgentCat setup started | project " + E2EToolkit.ProjectId + " |";
    internal const string SetupCompletePrefix = "AgentCat setup complete | project " + E2EToolkit.ProjectId + " |";
    internal const string Rebuilt = "Rebuilt injection registries on demand (tools/call before tools/list on this instance)";
}

internal static class E2EAssert
{
    // The shape of a session ID that AgentCat issues or accepts as supplied.
    internal const string SessionIdPattern = "^ses_[0-9A-Za-z]{27}$";

    // Asserts a well-formed session ID and returns it.
    internal static string SessionId(string? value)
    {
        Assert.NotNull(value);
        Assert.Matches(SessionIdPattern, value);
        return value;
    }

    // A call that did not end in isError; the failure message carries the result's text blocks.
    internal static void Succeeded(CallToolResult result)
        => Assert.True(result.IsError is not true, "The call returned isError: " + string.Join(" | ", ToolResults.Texts(result)));
}
