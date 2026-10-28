using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentCat.Tests.E2E;

// The stdio server as a process: its stdout, its AgentCat log file and the kill switch.
public class StdioProcessTests
{
    private static readonly string[] CalledTools = { "custom", "echo", "loose", "strict" };

    [Fact]
    public async Task Stdio_server_stdout_carries_only_mcp_frames()
    {
        using var api = new FakeAgentCatApi();
        await using var server = StdioServerProcess.Start(api);
        var sid = ToolResults.Sid("stdout");
        await server.ConnectAsync();
        var tools = await server.ListToolsAsync();
        Assert.Contains(tools.Tools, t => t.Name == WireNames.GetMoreToolsName);
        await CallAsync(server, """{"name":"echo","arguments":{"msg":"hi","context":"stdout echo"}}""");
        await CallAsync(server, """{"name":"loose","arguments":{"msg":"hi","context":"stdout loose"}}""");
        await CallAsync(server, """{"name":"strict","arguments":{"msg":"hi","context":"stdout strict"}}""");
        await CallAsync(server, """{"name":"custom","arguments":{"sid":""" + "\"" + sid + "\",\"context\":\"stdout custom\"}}");

        // Four tool calls and the custom event, before stdin closes.
        await api.WaitForRequestsAsync(5, LaneInstance.EventBound);

        await server.CloseStdinAsync();
        await server.WaitForExitAsync();

        Assert.Equal(0, server.ExitCode);
        await FakeAgentCatApi.WithinAsync(server.Stdout.Completion, StdioServerProcess.ExitBound);
        await server.Stdout.Completion;

        var frames = server.Stdout.Text.Split('\n');
        Assert.Equal("", frames[^1]);
        var messages = frames.Take(frames.Length - 1).Select(line => ParseFrame(line, server)).ToArray();
        Assert.True(messages.Count(m => m is JsonRpcResponse) >= 6, "Expected a response for the connect, the listing and four calls:\n" + server.Stdout.Text);

        // Logging went to stderr, not nowhere.
        Assert.Contains(server.Stderr, l => l.Contains("Microsoft.Hosting.Lifetime", StringComparison.Ordinal));

        var events = api.Requests.Select(WireEvent.Parse).ToArray();
        Assert.Equal(5, events.Length);
        Assert.Equal(
            CalledTools,
            events.Where(e => e.EventType == WireNames.ToolCallEventType).Select(e => e.ResourceName).OrderBy(n => n, StringComparer.Ordinal));
        var custom = Assert.Single(events, e => e.EventType == WireNames.CustomEventType);
        Assert.Equal(sid, custom.SessionId);
        Assert.Equal(E2EToolkit.ProjectId, custom.ProjectId);
        Assert.Equal(E2EToolkit.CustomResourceName, custom.ResourceName);
        Assert.Equal(E2EToolkit.CustomMessage, custom.UserIntent);
        Assert.Equal(E2EToolkit.ServerName, custom.ServerName);
        Assert.Equal(Lanes.ClientName, custom.ClientName);
    }

    // Two stdio lanes at once: each server writes its own setup lines, once each, to a file that only it writes.
    [Fact]
    public async Task Stdio_lane_logs_setup_lines_to_its_own_file()
    {
        var lane = Lanes.Get(Lanes.Stdio);
        await using var first = await lane.StartAsync(Scenarios.BaselineDefaults);
        await using var second = await lane.StartAsync(Scenarios.BaselineDefaults);
        await first.CallToolAsync("echo", """{"msg":"first","context":"first lane"}""");
        await second.CallToolAsync("echo", """{"msg":"second","context":"second lane"}""");
        await first.WaitForEventsAsync(1);
        await second.WaitForEventsAsync(1);

        Assert.NotNull(first.ServerLogPath);
        Assert.NotNull(second.ServerLogPath);
        Assert.NotEqual(first.ServerLogPath, second.ServerLogPath);
        Assert.NotEqual(Path.GetFullPath(first.ServerLogPath), Path.GetFullPath(FileLog.PathOverride!));
        AssertSetupLines(StdioServerProcess.LogMessages(first.ServerLogPath));
        AssertSetupLines(StdioServerProcess.LogMessages(second.ServerLogPath));
    }

    // A production server that AgentCat mistakes for a test run would send nothing, and nobody would notice. The
    // server process loads no test framework, so without AGENTCAT_ENABLED it tracks.
    [Fact]
    public async Task Stdio_server_without_the_kill_switch_tracks()
    {
        using var api = new FakeAgentCatApi();
        await using var server = StdioServerProcess.Start(api, environment => environment[EnabledGate.Variable] = null);
        await server.ConnectAsync();
        Assert.Contains((await server.ListToolsAsync()).Tools, t => t.Name == WireNames.GetMoreToolsName);
        await CallAsync(server, """{"name":"echo","arguments":{"msg":"hi","context":"no kill switch"}}""");
        await api.WaitForRequestsAsync(1, LaneInstance.EventBound);

        await server.CloseStdinAsync();
        await server.WaitForExitAsync();

        Assert.Equal(0, server.ExitCode);
        Assert.Contains(E2EToolkit.EnabledLine(null), server.Stderr);
        var sent = Assert.Single(api.Requests.Select(WireEvent.Parse));
        Assert.Equal(WireNames.ToolCallEventType, sent.EventType);
        Assert.Equal("echo", sent.ResourceName);
        Assert.Equal(E2EToolkit.ProjectId, sent.ProjectId);
        Assert.Equal("no kill switch", sent.UserIntent);
        var messages = StdioServerProcess.LogMessages(server.LogPath);
        AssertSetupLines(messages);
        Assert.Single(messages, m => m.StartsWith(ExpectedLog.SentPrefix + sent.Id + " |", StringComparison.Ordinal));

        // The variable was removed for the server only.
        Assert.Equal("true", Environment.GetEnvironmentVariable(EnabledGate.Variable));
    }

    [Fact]
    public async Task Stdio_server_with_the_kill_switch_off_sends_nothing()
    {
        using var api = new FakeAgentCatApi();
        await using var server = StdioServerProcess.Start(api, environment => environment[EnabledGate.Variable] = "false");
        await server.ConnectAsync();
        Assert.DoesNotContain((await server.ListToolsAsync()).Tools, t => t.Name == WireNames.GetMoreToolsName);
        await CallAsync(server, """{"name":"echo","arguments":{"msg":"hi"}}""");
        await Task.Delay(LaneInstance.StdioSettle, TestContext.Current.CancellationToken);

        await server.CloseStdinAsync();
        await server.WaitForExitAsync();

        Assert.Equal(0, server.ExitCode);
        Assert.Contains(E2EToolkit.EnabledLine("false"), server.Stderr);
        Assert.Empty(api.Requests);
        Assert.False(File.Exists(server.LogPath), "The server wrote an AgentCat log:\n" + string.Join("\n", StdioServerProcess.ReadLog(server.LogPath)));
    }

    // The setup lines of one WithAgentCat("proj_test") call with the baseline options, once each and in order.
    private static void AssertSetupLines(IReadOnlyList<string> messages)
    {
        var started = "AgentCat setup started | project " + E2EToolkit.ProjectId + " | sdk v" + Lanes.McpMajor + "/di | signals filters";
        var complete = "AgentCat setup complete | project " + E2EToolkit.ProjectId + " | tracing=true context=true reportMissing=true exporters=0";
        Assert.Equal(started, Assert.Single(messages, m => m.StartsWith(ExpectedLog.SetupStartedPrefix, StringComparison.Ordinal)));
        Assert.Equal(complete, Assert.Single(messages, m => m.StartsWith(ExpectedLog.SetupCompletePrefix, StringComparison.Ordinal)));
        Assert.True(
            messages.ToList().IndexOf(started) < messages.ToList().IndexOf(complete),
            "The setup complete line came before the started line:\n" + string.Join("\n", messages));
    }

    private static async Task CallAsync(StdioServerProcess server, string paramsJson)
        => E2EAssert.Succeeded(await server.CallToolAsync(paramsJson));

    private static JsonRpcMessage ParseFrame(string line, StdioServerProcess server)
    {
        JsonRpcMessage? message = null;
        Exception? error = null;
        try
        {
            if (JsonNode.Parse(line) is JsonObject frame && (string?)frame["jsonrpc"] == "2.0")
            {
                message = (JsonRpcMessage?)JsonSerializer.Deserialize(line, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage)));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            error = ex;
        }

        return message ?? throw new InvalidOperationException(
            "stdout carried a line that is not a JSON-RPC message: " + line + "\nServer stderr:\n" + server.StderrText(), error);
    }
}
