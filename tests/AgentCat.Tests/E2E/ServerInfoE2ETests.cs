#pragma warning disable CA1861
using System.Security.Claims;
using System.Text.Json.Nodes;
using AgentCat.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.E2E;

// The server info heartbeat on real transports: one for each process, also when every request gets new server options.
public class ServerInfoE2ETests
{
    [Theory]
    [MemberData(nameof(LaneData.HttpLanes), MemberType = typeof(LaneData))]
    public async Task V8_an_http_server_sends_one_heartbeat_for_many_requests(string lane)
    {
        var heartbeats = new RecordingServerInfoSender();
        var users = new List<string?>();
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup
        {
            ServerInfo = heartbeats,
            App = app => app.Use((http, next) =>
            {
                http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "user-1") }, "test"));
                return next(http);
            }),
            Server = mcp => mcp.Services.Configure<McpServerOptions>(o =>
            {
                o.Handlers.ListPromptsHandler = (context, _) =>
                {
                    lock (users)
                    {
                        users.Add(context.User?.FindFirst("sub")?.Value);
                    }

                    return new ValueTask<ListPromptsResult>(new ListPromptsResult());
                };
                o.Handlers.GetPromptHandler = (_, _) => throw new McpProtocolException("no such prompt", McpErrorCode.InvalidParams);
            }),
        });

        var listed = await e2e.ListToolsAsync();
        for (var i = 0; i < 3; i++)
        {
            E2EAssert.Succeeded(await e2e.CallToolAsync("strict", """{"msg":"hello","context":"say hello","session_id":"start"}"""));
        }

        await e2e.ListToolsAsync();
        var body = Assert.Single(await heartbeats.WaitForAsync(1));
        await e2e.AssertEventCountAsync(3);
        Assert.Equal(1, e2e.Runtime.ServerInfo.Guard.Begun);
        Assert.Equal(1, heartbeats.Count);

        // The prompt list handler ran for the heartbeat only, with the user of the request that started it.
        Assert.Empty(body["features"]!["prompts"]!.AsArray());
        lock (users)
        {
            Assert.Equal("user-1", Assert.Single(users));
        }

        Assert.Equal(E2EToolkit.ProjectId, (string)body["project_id"]!);
        Assert.All(e2e.Events, e => Assert.Equal(e.ServerName, (string)body["server_name"]!));
        Assert.All(e2e.Events, e => Assert.Equal(e.ServerVersion, (string)body["server_version"]!));
        Assert.Equal(
            listed.Tools.Select(t => t.Name).ToArray(),
            body["features"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToArray());
    }

    [Theory]
    [MemberData(nameof(LaneData.StdioLanes), MemberType = typeof(LaneData))]
    public async Task A_stdio_server_posts_its_heartbeat_to_the_api_after_a_tool_list_request(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults);

        var listed = await e2e.ListToolsAsync();
        E2EAssert.Succeeded(await e2e.CallToolAsync("strict", """{"msg":"hello","context":"say hello","session_id":"start"}"""));

        var deadline = DateTime.UtcNow + LaneInstance.EventBound;
        while (e2e.Api.ServerInfoRequests.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The stdio server sent no server info heartbeat.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await e2e.AssertEventCountAsync(1);
        var request = Assert.Single(e2e.Api.ServerInfoRequests);
        Assert.Equal("POST", request.Method);
        Assert.Null(request.Header("Authorization"));
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal(E2EToolkit.ProjectId, (string)body["project_id"]!);
        Assert.Equal(E2EToolkit.ServerName, (string)body["server_name"]!);
        Assert.Equal(
            listed.Tools.Select(t => t.Name).ToArray(),
            body["features"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToArray());
    }
}
