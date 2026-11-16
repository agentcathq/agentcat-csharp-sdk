#pragma warning disable CA1861
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using AgentCat.Internal;
using AgentCat.Internal.Delivery;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.ServerInfo;

public class ServerInfoSendTests
{
    private const string ServerInfoPath = "/v1/public/server-info/";

    private static RecordedHttpRequest[] ServerInfoPosts(RecordingHttpHandler handler)
        => handler.Requests.Where(r => r.Path.EndsWith(ServerInfoPath, StringComparison.Ordinal)).ToArray();

    private static async Task<RecordedHttpRequest[]> WaitForPostsAsync(RecordingHttpHandler handler, int count)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (ServerInfoPosts(handler).Length < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Expected " + count + " server info POST(s), got " + ServerInfoPosts(handler).Length + ".");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return ServerInfoPosts(handler);
    }

    [Fact]
    public void The_server_info_endpoint_follows_the_base_url_of_the_events()
    {
        using var sender = new HttpEventSender();
        Assert.Equal("https://api.agentcat.com/v1/public/server-info/", sender.ServerInfoEndpoint);
        sender.SetBaseUrl("https://proxy.example.com/agentcat/");
        Assert.Equal("https://proxy.example.com/agentcat/v1/public/server-info/", sender.ServerInfoEndpoint);
        Assert.Equal("https://proxy.example.com/agentcat/v1/public/events/", sender.Endpoint);
    }

    [Fact]
    public async Task V14_a_custom_api_base_url_gets_the_post_with_the_headers_of_the_event_send()
    {
        var handler = new RecordingHttpHandler { Respond = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)) };
        using var http = new HttpEventSender(handler);
        await using var lab = new HeartbeatLab(http);
        var server = await lab.StartAsync(HeartbeatLab.DemoServer(), options: new AgentCatOptions { ApiBaseUrl = "https://custom.example.test/base/" });

        await server.CallToolAsync("alpha");

        var post = Assert.Single(await WaitForPostsAsync(handler, 1));
        await lab.IdleAsync();
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("https://custom.example.test/base/v1/public/server-info/", post.Uri.ToString());
        Assert.Equal("application/json", post.Header("Content-Type"));
        Assert.Equal(SdkInfo.UserAgent, post.Header("User-Agent"));
        Assert.Null(post.Header("Authorization"));
        Assert.Null(post.Header("Expect"));
        var body = JsonNode.Parse(post.Body)!;
        Assert.Equal(HeartbeatLab.Project, (string)body["project_id"]!);
        Assert.Equal(4, body["features"]!["tools"]!.AsArray().Count);
        Assert.Single(ServerInfoPosts(handler));

        Assert.True(await lab.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        var eventPost = Assert.Single(handler.Requests, r => !r.Path.EndsWith(ServerInfoPath, StringComparison.Ordinal));
        Assert.Equal("https://custom.example.test/base/v1/public/events/", eventPost.Uri.ToString());
        Assert.Equal(eventPost.Header("User-Agent"), post.Header("User-Agent"));
    }

    [Fact]
    public async Task V13_over_http_a_413_is_followed_by_one_post_without_the_tool_schemas()
    {
        var answers = new Queue<HttpStatusCode>(new[] { HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.Accepted });
        var handler = new RecordingHttpHandler();
        handler.Respond = (request, _) =>
        {
            lock (answers)
            {
                var serverInfo = request.RequestUri!.AbsolutePath.EndsWith(ServerInfoPath, StringComparison.Ordinal);
                return Task.FromResult(new HttpResponseMessage(serverInfo ? answers.Dequeue() : HttpStatusCode.Accepted));
            }
        };
        using var http = new HttpEventSender(handler);
        http.SetBaseUrl("https://custom.example.test");
        await using var lab = new HeartbeatLab(http);
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        await server.CallToolAsync("alpha");

        var posts = await WaitForPostsAsync(handler, 2);
        await lab.IdleAsync();
        Assert.Equal(2, ServerInfoPosts(handler).Length);
        var first = JsonNode.Parse(posts[0].Body)!["features"]!["tools"]!.AsArray();
        var second = JsonNode.Parse(posts[1].Body)!["features"]!["tools"]!.AsArray();
        Assert.All(first, tool => Assert.NotNull(tool!["inputSchema"]));
        Assert.All(second, tool => Assert.Null(tool!["inputSchema"]));
        Assert.All(second, tool => Assert.Null(tool!["outputSchema"]));
        Assert.Equal(first.Select(t => (string)t!["name"]!), second.Select(t => (string)t!["name"]!));
        Assert.True(posts[1].Body.Length < posts[0].Body.Length);
    }

    [Fact]
    public async Task An_api_that_cannot_be_reached_ends_the_attempt_with_one_line()
    {
        var handler = new RecordingHttpHandler { Respond = static (_, _) => throw new HttpRequestException("connection refused") };
        using var http = new HttpEventSender(handler);
        http.SetBaseUrl("https://custom.example.test");
        await using var lab = new HeartbeatLab(http);
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        await server.ListToolsAsync();
        await lab.IdleAsync();

        Assert.Single(ServerInfoPosts(handler));
        Assert.Equal(
            "Server info heartbeat not sent: the send failed: HttpRequestException",
            Assert.Single(lab.HeartbeatLines()));
    }

    [Fact]
    public async Task The_fake_api_keeps_a_heartbeat_apart_from_the_events()
    {
        using var api = new FakeAgentCatApi();
        using var http = new HttpEventSender();
        http.SetBaseUrl(api.BaseUrl);
        await using var lab = new HeartbeatLab(http);
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        await server.CallToolAsync("alpha");
        await lab.IdleAsync();
        Assert.True(await lab.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));

        var heartbeat = Assert.Single(api.ServerInfoRequests);
        Assert.Equal("POST", heartbeat.Method);
        Assert.Equal(ServerInfoPath, heartbeat.Path);
        Assert.Null(heartbeat.Header("Authorization"));
        Assert.Equal("application/json", heartbeat.Header("Content-Type"));
        Assert.Equal("demo-server", (string)JsonNode.Parse(heartbeat.Body)!["server_name"]!);
        Assert.Equal("/v1/public/events/", Assert.Single(api.Requests).Path);
        Assert.StartsWith("Sent server info heartbeat | 4 tools | ", Assert.Single(lab.HeartbeatLines()), StringComparison.Ordinal);
    }
}
