using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Events;
using AgentCat.Internal.Runtime;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.TestSupport;

internal sealed class EngineHarness : McpHarness
{
    private readonly string? _projectId;
    private readonly AgentCatOptions? _options;
    private bool _installed;

    internal EngineHarness(string? projectId = "proj_test", AgentCatOptions? options = null, PipelineSettings? pipeline = null)
    {
        _projectId = projectId;
        _options = options;
        Clock.HoldDelays();
        Runtime = new AgentCatRuntime(Log, Sender, pipeline, Clock);
    }

    internal RecordingLog Log { get; } = new();

    internal RecordingSender Sender { get; } = new();

    internal ManualClock Clock { get; } = new();

    internal AgentCatRuntime Runtime { get; }

    internal AgentCatTracker? Tracker { get; private set; }

    // Runs once per harness: from a Configure step that calls it, else right before McpServer.Create
    // (PostConfigure with DI).
    internal AgentCatTracker? Install(McpServerOptions options)
    {
        _installed = true;
        var tracker = AgentCatEngine.Install(options, _projectId, _options, Runtime, "options");
        Tracker ??= tracker;
        return tracker;
    }

    internal bool InstallShared(McpServerOptions options, AgentCatTracker tracker)
    {
        _installed = true;
        Tracker ??= tracker;
        return EngineInstaller.Install(options, tracker);
    }

    internal async Task<IReadOnlyList<JsonObject>> FlushAsync()
    {
        var flushed = Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound);
        await FakeAgentCatApi.WithinAsync(flushed, FakeAgentCatApi.WaitBound);
        Assert.True(await flushed, "The event pipeline did not drain.");
        return Sender.Sent.Select(EventShape.ToTsJson).ToArray();
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            Runtime.Pipeline.Dispose();
        }
    }

    protected override void BeforeServerCreate(McpServerOptions options)
    {
        if (!_installed)
        {
            Install(options);
        }
    }
}
