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
    public async Task Under_the_public_fake_no_heartbeat_starts_and_no_list_handler_runs_for_one()
    {
        var sharedBefore = TestAssemblySetup.SharedServerInfo.Count;
        var sharedBegunBefore = AgentCatRuntime.Shared.ServerInfo.Guard.Begun;
        var toolLists = 0;
        var resourceLists = 0;
        var promptLists = 0;
        using var fake = AgentCatSdk.Fake();
        await using var server = HeartbeatLab.DemoServer();
        server.Configure(o =>
        {
            var tools = o.Handlers.ListToolsHandler!;
            o.Handlers.ListToolsHandler = (context, token) =>
            {
                Interlocked.Increment(ref toolLists);
                return tools(context, token);
            };
            var resources = o.Handlers.ListResourcesHandler!;
            o.Handlers.ListResourcesHandler = (context, token) =>
            {
                Interlocked.Increment(ref resourceLists);
                return resources(context, token);
            };
            HeartbeatLab.SetPrompts(o, (_, _) =>
            {
                Interlocked.Increment(ref promptLists);
                return new ValueTask<ModelContextProtocol.Protocol.ListPromptsResult>(new ModelContextProtocol.Protocol.ListPromptsResult());
            });
            AgentCatSdk.Track(o, Project);
        });
        await server.StartAsync();
        fake.Runtime.ServerInfo.Guard.StartMemory(DateTimeOffset.UtcNow.AddHours(-1));

        await server.ListToolsAsync();
        await server.CallToolAsync("alpha");
        await server.CallToolAsync("get_more_tools", "{\"context\":\"I need a tool\"}");
        await fake.AssertTrackedCountAsync(2, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Null(fake.Runtime.ServerInfo.Sender);
        Assert.Equal(0, fake.Runtime.ServerInfo.Guard.Begun);
        Assert.Equal(sharedBegunBefore, AgentCatRuntime.Shared.ServerInfo.Guard.Begun);
        Assert.Equal(sharedBefore, TestAssemblySetup.SharedServerInfo.Count);
        Assert.Equal(1, Volatile.Read(ref toolLists));
        Assert.Equal(0, Volatile.Read(ref resourceLists));
        Assert.Equal(0, Volatile.Read(ref promptLists));
        Assert.DoesNotContain(await fake.GetLogsAsync(TestContext.Current.CancellationToken), line => line.Contains("heartbeat", StringComparison.Ordinal));
    }
}
