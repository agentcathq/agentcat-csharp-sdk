using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.ServerInfo;

// These tests read the shared runtime and use a fake, which is process-wide, so they run alone.
[Collection(SharedRuntime.Name)]
public class ServerInfoTestSetupTests
{
    private const string Project = "proj_abcdefghijklmnopqrstuvwxyz0";

    [Fact]
    public void V16_the_shared_runtime_of_the_test_run_records_its_heartbeats_and_sends_none()
    {
        var sender = AgentCatRuntime.Shared.ServerInfo.Sender;

        Assert.Same(TestAssemblySetup.SharedServerInfo, sender);
        Assert.IsNotType<HttpEventSender>(sender);
    }

    [Fact]
    public void V16_a_test_runtime_without_a_server_info_sender_sends_no_heartbeat()
    {
        using var http = new HttpEventSender(new RecordingHttpHandler());
        var runtime = new AgentCatRuntime(new RecordingLog(), http);
        try
        {
            Assert.Null(runtime.ServerInfo.Sender);
        }
        finally
        {
            runtime.Pipeline.Dispose();
        }
    }

    [Fact]
    public async Task V16_a_runtime_without_a_server_info_sender_starts_no_attempt()
    {
        var handler = new RecordingHttpHandler();
        using var http = new HttpEventSender(handler);
        var runtime = new AgentCatRuntime(new RecordingLog(), http);
        await using var server = HeartbeatLab.DemoServer();
        try
        {
            server.Configure(o => AgentCat.Internal.Engine.AgentCatEngine.Install(o, Project, null, runtime, "options"));
            await server.StartAsync();

            await server.ListToolsAsync();
            await server.CallToolAsync("get_more_tools", "{\"context\":\"I need a tool\"}");

            Assert.Equal(0, runtime.ServerInfo.Guard.Begun);
            Assert.True(await runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
            Assert.All(handler.Requests, r => Assert.Equal("/v1/public/events/", r.Path));
        }
        finally
        {
            runtime.Pipeline.Dispose();
        }
    }

    [Fact]
    public async Task The_fake_records_a_heartbeat_apart_from_the_events_and_writes_no_line_for_it()
    {
        var sharedBefore = TestAssemblySetup.SharedServerInfo.Count;
        using var fake = AgentCatSdk.Fake();
        await using var server = HeartbeatLab.DemoServer();
        server.Configure(o => AgentCatSdk.Track(o, Project));
        await server.StartAsync();

        await server.ListToolsAsync();

        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (fake.ServerInfoBodies.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The fake recorded no heartbeat.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var body = JsonNode.Parse(Assert.Single(fake.ServerInfoBodies))!;
        Assert.Equal(Project, (string)body["project_id"]!);
        Assert.Equal("demo-server", (string)body["server_name"]!);
        Assert.Equal(4, body["features"]!["tools"]!.AsArray().Count);
        await fake.AssertNothingTrackedAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(await fake.GetLogsAsync(TestContext.Current.CancellationToken), line => line.Contains("heartbeat", StringComparison.Ordinal));
        Assert.Equal(sharedBefore, TestAssemblySetup.SharedServerInfo.Count);
    }
}
