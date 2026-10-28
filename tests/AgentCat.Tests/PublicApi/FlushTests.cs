using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.PublicApi;

public class FlushTests
{
    private static async Task WaitUntilSendingAsync(CustomEventTests.Direct d)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (d.Sender.HangingCount == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The sender did not start.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Flush_returns_true_when_everything_is_sent()
    {
        using var d = new CustomEventTests.Direct();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");

        Assert.True(await d.Client.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Single(d.Sender.Sent);
    }

    [Fact]
    public async Task Flush_returns_false_when_the_timeout_ends_first()
    {
        using var d = new CustomEventTests.Direct();
        d.Sender.Hang();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        await WaitUntilSendingAsync(d);

        Assert.False(await d.Client.FlushAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));

        d.Sender.Release();
        Assert.True(await d.Client.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Flush_clamps_negative_and_huge_timeouts_without_throwing()
    {
        using var d = new CustomEventTests.Direct();

        foreach (var timeout in new TimeSpan?[]
        {
            null,
            TimeSpan.FromSeconds(-5),
            TimeSpan.MinValue,
            TimeSpan.FromMilliseconds(int.MaxValue),
            TimeSpan.MaxValue,
            Timeout.InfiniteTimeSpan,
        })
        {
            Assert.True(await d.Client.FlushAsync(timeout, TestContext.Current.CancellationToken), "idle flush with " + timeout);
        }

        d.Sender.Hang();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        await WaitUntilSendingAsync(d);

        Assert.False(await d.Client.FlushAsync(TimeSpan.FromSeconds(-5), TestContext.Current.CancellationToken));
        Assert.False(await d.Client.FlushAsync(TimeSpan.MinValue, TestContext.Current.CancellationToken));

        var unlimited = d.Client.FlushAsync(TimeSpan.MaxValue, TestContext.Current.CancellationToken);
        var huge = d.Client.FlushAsync(TimeSpan.FromMilliseconds(int.MaxValue), TestContext.Current.CancellationToken);
        var open = d.Client.FlushAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(unlimited.IsCompleted);
        d.Sender.Release();
        Assert.True(await unlimited);
        Assert.True(await huge);
        Assert.True(await open);
    }

    [Fact]
    public async Task Flush_returns_false_promptly_when_its_token_is_cancelled_and_the_event_is_still_sent()
    {
        using var d = new CustomEventTests.Direct();
        d.Sender.Hang();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        await WaitUntilSendingAsync(d);
        using var cts = new CancellationTokenSource();

        var unbounded = d.Client.FlushAsync(cancellationToken: cts.Token);
        var bounded = d.Client.FlushAsync(TimeSpan.FromMinutes(5), cts.Token);
        Assert.False(unbounded.IsCompleted);
        Assert.False(bounded.IsCompleted);
        cts.Cancel();

        await FakeAgentCatApi.WithinAsync(Task.WhenAll(unbounded, bounded), FakeAgentCatApi.WaitBound);
        Assert.False(await unbounded);
        Assert.False(await bounded);
        Assert.Equal(1, d.Sender.HangingCount);
        d.Sender.Release();
        Assert.True(await d.Client.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Single(d.Sender.Sent);
        Assert.Contains(d.Log.Lines, l => l.StartsWith("Successfully sent event ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flush_with_an_already_cancelled_token_returns_false_without_waiting()
    {
        using var d = new CustomEventTests.Direct();
        var cancelled = new CancellationToken(canceled: true);

        // Nothing to wait for: like a zero timeout, an idle flush reports that everything is delivered.
        Assert.True(await d.Client.FlushAsync(cancellationToken: cancelled));

        d.Sender.Hang();
        await d.Client.PublishCustomEventAsync("s1", "proj_test");
        await WaitUntilSendingAsync(d);

        foreach (var timeout in new TimeSpan?[] { null, TimeSpan.FromMinutes(5), TimeSpan.Zero })
        {
            var flush = d.Client.FlushAsync(timeout, cancelled);
            Assert.True(flush.IsCompleted, "flush with " + timeout + " waited");
            Assert.False(await flush);
        }

        d.Sender.Release();
        Assert.True(await d.Client.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Single(d.Sender.Sent);
    }
}

[Collection(SharedRuntime.Name)]
public class SharedRuntimeCustomEventTests
{
    [Fact]
    public async Task Public_statics_publish_through_the_shared_runtime()
    {
        using var api = new FakeAgentCatApi();
        AgentCatRuntime.Shared.ApplyBaseUrl(api.BaseUrl, _ => null);

        await AgentCatSdk.PublishCustomEventAsync("sess-1", "proj_static", new CustomEventData { ResourceName = "static-action", Message = "why" });

        Assert.True(await AgentCatSdk.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        var request = Assert.Single(api.Requests);
        var body = JsonNode.Parse(Encoding.UTF8.GetString(request.Body))!.AsObject();
        Assert.Equal("proj_static", (string?)body["project_id"]);
        Assert.Equal("sess-1", (string?)body["session_id"]);
        Assert.Equal("agentcat:custom", (string?)body["event_type"]);
        Assert.Equal("static-action", (string?)body["resource_name"]);
        Assert.Equal("why", (string?)body["user_intent"]);
    }

    [Fact]
    public async Task Public_static_server_form_called_inside_a_tool_carries_the_tracked_project()
    {
        using var api = new FakeAgentCatApi();
        await using var h = new McpHarness();
        h.AddTool(
            async (McpServer server) =>
            {
                await AgentCatSdk.PublishCustomEventAsync(server, "ignored_project", new CustomEventData { SessionId = "from-tool", ResourceName = "inside-tool" });
                return "done";
            },
            "emit");
        h.Configure(o => AgentCatSdk.Track(o, "proj_tracked", new AgentCatOptions { ApiBaseUrl = api.BaseUrl }));
        await h.StartAsync();

        await h.CallToolAsync("emit");

        Assert.True(await AgentCatSdk.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        var bodies = (await api.WaitForRequestsAsync(2))
            .Select(r => JsonNode.Parse(Encoding.UTF8.GetString(r.Body))!.AsObject())
            .ToArray();
        var custom = Assert.Single(bodies, b => (string?)b["event_type"] == "agentcat:custom");
        Assert.Equal("proj_tracked", (string?)custom["project_id"]);
        Assert.Equal("from-tool", (string?)custom["session_id"]);
        Assert.Equal("inside-tool", (string?)custom["resource_name"]);
    }
}
