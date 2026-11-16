using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Delivery;
using SdkDiagnostics = AgentCat.Internal.Diagnostics.Diagnostics;
using AgentCat.Internal.Lifecycle;
using AgentCat.Internal.Logging;
using AgentCat.Internal.ServerInfo;
using AgentCat.Internal.Telemetry;

namespace AgentCat.Internal.Runtime;

internal sealed class AgentCatRuntime
{
    private static readonly Lazy<AgentCatRuntime> SharedInstance = new(CreateShared, true);

    // No variable and no test run: on.
    private static readonly EnabledGate AlwaysOn = new(static _ => null, static () => false, NullAgentCatLog.Instance);

    // Guards every runtime's exporter claim, so two installs never both win.
    private static readonly object ExporterClaimGate = new();

    // Recording runtimes that are current, oldest first. The last one is Current.
    private static readonly List<AgentCatRuntime> Recorders = new();
    private static AgentCatRuntime? _currentRecorder;

    private readonly IEventSender _sender;

    // Shared and the recording runtimes: their trackers publish through EffectiveFor.
    private readonly bool _followsCurrent;
    private readonly bool _recording;
    private bool _exportersClaimed;

    internal AgentCatRuntime(
        IAgentCatLog log,
        IEventSender sender,
        PipelineSettings? settings = null,
        IPipelineClock? clock = null,
        EnabledGate? gate = null,
        SdkDiagnostics? diagnostics = null,
        ShutdownDeadline? shutdownDeadline = null,
        HttpMessageHandler? exportHandler = null,
        IRandomSource? random = null,
        ExporterBuilder? exporterBuilder = null,
        IServerInfoSender? serverInfoSender = null)
        : this(log, sender, settings, clock, gate, diagnostics, followsCurrent: false, record: null, shutdownDeadline, exportHandler, random, exporterBuilder, serverInfoSender)
    {
    }

    // Records each event after its last stage instead of sending it, and gives each server info heartbeat to
    // serverInfoSender. Always on, with inert diagnostics. One reader keeps the recorded events in the order they were
    // queued.
    internal AgentCatRuntime(IAgentCatLog log, Action<AgentCatEvent> record, IServerInfoSender serverInfoSender)
        : this(log, UnusedSender.Instance, new PipelineSettings { Concurrency = 1 }, null, null, null, followsCurrent: true, record, null, null, null, null, serverInfoSender)
    {
    }

    private AgentCatRuntime(
        IAgentCatLog log,
        IEventSender sender,
        PipelineSettings? settings,
        IPipelineClock? clock,
        EnabledGate? gate,
        SdkDiagnostics? diagnostics,
        bool followsCurrent,
        Action<AgentCatEvent>? record,
        ShutdownDeadline? shutdownDeadline,
        HttpMessageHandler? exportHandler,
        IRandomSource? random,
        ExporterBuilder? exporterBuilder,
        IServerInfoSender? serverInfoSender)
    {
        _sender = sender;
        _followsCurrent = followsCurrent;
        _recording = record is not null;
        Gate = gate ?? AlwaysOn;
        Log = log;
        CallLog = followsCurrent ? new FollowingLog(this) : log;
        Diagnostics = diagnostics ?? SdkDiagnostics.Inert;
        Clock = clock ?? SystemPipelineClock.Instance;
        Random = random ?? RandomSource.Default;
        Exports = record is null ? new ExportChannel(log, Clock, exportHandler) : ExportChannel.Discarding;
        Pipeline = new EventPipeline(sender, log, settings, Clock, record: record, exports: Exports);
        ShutdownDeadline = shutdownDeadline ?? new ShutdownDeadline();
        ExporterBuilder = exporterBuilder;

        // The lines of a recorded heartbeat are dropped: when it ends is not tied to the events, so a fake's log
        // would differ from run to run.
        ServerInfo = new ServerInfoHeartbeat(serverInfoSender, record is null ? log : NullAgentCatLog.Instance, Clock, Gate);
    }

    internal static AgentCatRuntime Shared => SharedInstance.Value;

    // Asking does not create the shared runtime.
    internal bool IsShared => SharedInstance.IsValueCreated && ReferenceEquals(SharedInstance.Value, this);

    // The runtime that Track, a WithAgentCat decision and a string-form custom event use: the newest recording
    // runtime that is still current, else Shared.
    internal static AgentCatRuntime Current => Volatile.Read(ref _currentRecorder) ?? Shared;

    // Asked before every install and custom event. The shared runtime reads AGENTCAT_ENABLED, else is off in a test
    // run; a runtime built without a gate is always on.
    internal EnabledGate Gate { get; }

    internal IAgentCatLog Log { get; }

    // For the lines a tracker installed on this runtime writes while it serves calls: they go where its events go.
    internal IAgentCatLog CallLog { get; }

    // The process-wide SDK diagnostics. A runtime built without one has an inert instance.
    internal SdkDiagnostics Diagnostics { get; }

    internal EventPipeline Pipeline { get; }

    internal IPipelineClock Clock { get; }

    // The exporters' requests: through AgentCatHttp.Client unless a test gives a handler. A recording runtime's channel
    // drops every request.
    internal ExportChannel Exports { get; }

    // The random bytes of the IDs that exporters cannot derive from the event.
    internal IRandomSource Random { get; }

    // Builds the exporters of the first install that configures them. Null on a runtime that hosts no exporters: a
    // fake's, and a test runtime that does not ask for them. Shared builds the real ones.
    internal ExporterBuilder? ExporterBuilder { get; }

    internal bool HostsExporters => ExporterBuilder is not null;

    // The server info heartbeat of the servers that publish through this runtime. A runtime built without a server
    // info sender sends none.
    internal ServerInfoHeartbeat ServerInfo { get; }

    internal bool ExportersClaimed
    {
        get
        {
            lock (ExporterClaimGate)
            {
                return _exportersClaimed;
            }
        }
    }

    // The stop window that the flush after a host stop, the signal handler and the process-exit handler share. The
    // shared runtime's is the process's.
    internal ShutdownDeadline ShutdownDeadline { get; }

    private static AgentCatRuntime CreateShared()
    {
        var log = new ProcessLog(new FileLog(FileLog.DefaultPath()));
        var gate = new EnabledGate(Environment.GetEnvironmentVariable, TestEnvironment.IsTestRun, log);
        var diagnostics = new SdkDiagnostics(
            Environment.GetEnvironmentVariable,
            TestEnvironment.IsTestRun,
            SystemPipelineClock.Instance,
            log.SetDiagnosticsSink,
            AgentCatHttp.Client);
        var sender = new HttpEventSender();
        return new AgentCatRuntime(log, sender, null, null, gate, diagnostics, followsCurrent: true, record: null, shutdownDeadline: null, exportHandler: null, random: null, exporterBuilder: ExporterFactory.Build, ServerInfoHeartbeat.SharedSenderOverride ?? sender);
    }

    // Where a tracker installed on this runtime publishes. A tracker on Shared or on a fake's runtime follows the
    // current fake; without one, Shared keeps its own events and a fake's tracker gets null: its events and lines are
    // dropped, never sent through Shared. A tracker on any other runtime keeps that runtime.
    internal static AgentCatRuntime? EffectiveFor(AgentCatRuntime installed)
    {
        if (!installed._followsCurrent)
        {
            return installed;
        }

        return Volatile.Read(ref _currentRecorder) ?? (installed._recording ? null : installed);
    }

    internal static void MakeCurrent(AgentCatRuntime recorder)
    {
        lock (Recorders)
        {
            Recorders.Add(recorder);
            Volatile.Write(ref _currentRecorder, recorder);
        }
    }

    // Harmless for a runtime that is not current.
    internal static void RemoveCurrent(AgentCatRuntime recorder)
    {
        lock (Recorders)
        {
            Recorders.Remove(recorder);
            Volatile.Write(ref _currentRecorder, Recorders.Count == 0 ? null : Recorders[Recorders.Count - 1]);
        }
    }

    // True for the first caller only, and never on a runtime that hosts no exporters. The winner builds the exporters;
    // a caller that loses writes no line about them.
    internal bool TryClaimExporters()
    {
        if (!HostsExporters)
        {
            return false;
        }

        lock (ExporterClaimGate)
        {
            if (_exportersClaimed)
            {
                return false;
            }

            _exportersClaimed = true;
            return true;
        }
    }

    internal void ApplyBaseUrl(string? option, Func<string, string?> environment)
    {
        var baseUrl = ApiBaseUrl.Resolve(option, environment);
        if (baseUrl is not null && _sender is HttpEventSender http)
        {
            http.SetBaseUrl(baseUrl);
        }
    }

    private sealed class UnusedSender : IEventSender
    {
        internal static readonly UnusedSender Instance = new();

        public Task SendAsync(AgentCatEvent e, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FollowingLog : IAgentCatLog
    {
        private readonly AgentCatRuntime _installed;

        internal FollowingLog(AgentCatRuntime installed) => _installed = installed;

        public void Write(string message) => EffectiveFor(_installed)?.Log.Write(message);

        public void Write(string message, LogSeverity hostSeverity) => EffectiveFor(_installed)?.Log.Write(message, hostSeverity);
    }
}
