using System.Text.Json;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Engine;

public class RuntimeTests
{
    private static readonly Func<string, string?> NoEnvironment = _ => null;

    private static Func<string, string?> Environment(params (string Name, string Value)[] values)
        => name => values.Where(v => v.Name == name).Select(v => v.Value).FirstOrDefault();

    private static int McpMajor => typeof(McpServer).Assembly.GetName().Version!.Major;

    [Fact]
    public void Base_url_last_call_wins_and_unset_keeps_it()
    {
        using var sender = new HttpEventSender();
        var runtime = new AgentCatRuntime(NullAgentCatLog.Instance, sender);
        var initial = sender.Endpoint;

        runtime.ApplyBaseUrl(null, NoEnvironment);
        Assert.Equal(initial, sender.Endpoint);

        runtime.ApplyBaseUrl("http://one.test", NoEnvironment);
        Assert.Equal("http://one.test/v1/public/events/", sender.Endpoint);

        runtime.ApplyBaseUrl("http://two.test", NoEnvironment);
        Assert.Equal("http://two.test/v1/public/events/", sender.Endpoint);

        runtime.ApplyBaseUrl(null, NoEnvironment);
        Assert.Equal("http://two.test/v1/public/events/", sender.Endpoint);

        runtime.ApplyBaseUrl(null, Environment(("AGENTCAT_API_URL", "http://env.test")));
        Assert.Equal("http://env.test/v1/public/events/", sender.Endpoint);
    }

    [Fact]
    public void Base_url_is_ignored_for_a_sender_that_is_not_http()
    {
        var runtime = new AgentCatRuntime(NullAgentCatLog.Instance, new RecordingSender());

        runtime.ApplyBaseUrl("http://one.test", NoEnvironment);
    }

    [Fact]
    public void Shared_runtime_is_one_per_process()
    {
        var seen = new AgentCatRuntime[16];
        Parallel.For(0, seen.Length, i => seen[i] = AgentCatRuntime.Shared);

        Assert.All(seen, r => Assert.Same(AgentCatRuntime.Shared, r));
        Assert.IsType<ProcessLog>(AgentCatRuntime.Shared.Log);
        Assert.Same(SystemPipelineClock.Instance, AgentCatRuntime.Shared.Clock);
    }

    [Fact]
    public void Server_identity_uses_ServerInfo_else_the_SDK_fallback()
    {
        var info = (Implementation)JsonSerializer.Deserialize(
            """{"name":"orders","version":"4.5.6"}""",
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Implementation)))!;
        var (name, version) = ServerIdentity.Of(new McpServerOptions { ServerInfo = info });
        Assert.Equal("orders", name);
        Assert.Equal("4.5.6", version);

        var fallback = ServerIdentity.Of(new McpServerOptions());
        var assembly = (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(McpServer).Assembly).GetName();
        Assert.Equal(assembly.Name ?? "McpServer", fallback.Name);
        Assert.Equal(assembly.Version?.ToString() ?? "1.0.0", fallback.Version);
        Assert.Equal(fallback, ServerIdentity.Of(null));
    }

    [Fact]
    public void Project_id_null_reads_AGENTCAT_PROJECT_ID_and_empty_is_telemetry_only()
    {
        var runtime = new AgentCatRuntime(new RecordingLog(), new RecordingSender());

        var fromEnvironment = AgentCatTracker.Create(null, null, runtime, "options", Environment(("AGENTCAT_PROJECT_ID", "proj_env")));
        Assert.Equal("proj_env", fromEnvironment.ProjectId);

        var explicitId = AgentCatTracker.Create("proj_a", null, runtime, "options", Environment(("AGENTCAT_PROJECT_ID", "proj_env")));
        Assert.Equal("proj_a", explicitId.ProjectId);

        var telemetryOnly = AgentCatTracker.Create("", null, runtime, "options", Environment(("AGENTCAT_PROJECT_ID", "proj_env")));
        Assert.Equal("", telemetryOnly.ProjectId);

        var unset = AgentCatTracker.Create(null, null, runtime, "options", NoEnvironment);
        Assert.Equal("", unset.ProjectId);
    }

    [Fact]
    public void Setup_lines_match_TS()
    {
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender());

        var tracker = AgentCatEngine.Install(new McpServerOptions(), "proj_a", new AgentCatOptions { EnableToolCallContext = false }, runtime, "options", NoEnvironment);

        Assert.NotNull(tracker);
        Assert.Same(runtime, tracker!.Runtime);
        Assert.NotNull(tracker.State);
        Assert.False(tracker.SetupFailed);
        Assert.Equal(
            new[]
            {
                $"AgentCat setup started | project proj_a | sdk v{McpMajor}/options | signals filters",
                "AgentCat setup complete | project proj_a | tracing=true context=false reportMissing=true exporters=0",
            },
            log.Lines);
    }

    [Fact]
    public void Setup_lines_for_telemetry_only_include_the_no_project_warning()
    {
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender());

        Assert.NotNull(AgentCatEngine.Install(new McpServerOptions(), "", new AgentCatOptions { EnableTracing = false, EnableReportMissing = false }, runtime, "options", NoEnvironment));

        Assert.Equal(
            new[]
            {
                $"AgentCat setup started | project (telemetry-only) | sdk v{McpMajor}/options | signals filters",
                "Warning: No projectId provided and no exporters configured. Events will not be sent anywhere.",
                "AgentCat setup complete | project (telemetry-only) | tracing=false context=true reportMissing=false exporters=0",
            },
            log.Lines);
    }

    [Fact]
    public void Create_never_throws_and_turns_tracing_off()
    {
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender());

        var tracker = AgentCatTracker.Create("proj_a", null, runtime, "options", _ => throw new InvalidOperationException("boom"));

        Assert.False(tracker.Settings.EnableTracing);
        Assert.True(tracker.SetupFailed);
        Assert.Equal("Warning: Failed to track server - InvalidOperationException: boom", Assert.Single(log.Lines));
    }

    [Fact]
    public async Task Telemetry_only_events_are_never_sent()
    {
        var sender = new RecordingSender();
        var clock = new ManualClock();
        clock.HoldDelays();
        var runtime = new AgentCatRuntime(new RecordingLog(), sender, clock: clock);
        var exported = new TaskCompletionSource<AgentCatEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(runtime.Pipeline.TrySetExporter(new CapturingExporter(exported)));
        var tracker = AgentCatTracker.Create("", null, runtime, "options", NoEnvironment);

        EventPublisher.Publish(tracker, new AgentCatEvent { EventType = "mcp:tools/call", Timestamp = runtime.Clock.UtcNow }, null, true);

        await FakeAgentCatApi.WithinAsync(exported.Task, FakeAgentCatApi.WaitBound);
        Assert.True(await runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Equal(0, sender.Calls);
        Assert.Equal("", (await exported.Task).ProjectId);
    }

    private sealed class CapturingExporter(TaskCompletionSource<AgentCatEvent> exported) : IEventExporter
    {
        public Task ExportAsync(AgentCatEvent e, CancellationToken cancellationToken)
        {
            exported.TrySetResult(e);
            return Task.CompletedTask;
        }
    }
}
