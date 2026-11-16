using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Injection;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.ServerInfo;

// Sends what a server offers to agents (name, version, title, instructions and its lists) to AgentCat, about once an
// hour for each project and server name. One per runtime. A request only asks whether a heartbeat is due; the list
// calls, the redaction and the POST run on a timer thread, and no failure of them reaches the server.
internal sealed class ServerInfoHeartbeat
{
    internal static readonly TimeSpan DefaultListTimeout = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan DefaultBuildTimeout = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(10);

    private const string NotSentPrefix = "Server info heartbeat not sent: ";

    private readonly IServerInfoSender? _sender;
    private readonly IAgentCatLog _log;
    private readonly IPipelineClock _clock;
    private readonly EnabledGate _gate;

    internal ServerInfoHeartbeat(IServerInfoSender? sender, IAgentCatLog log, IPipelineClock clock, EnabledGate gate)
    {
        _sender = sender;
        _log = log;
        _clock = clock;
        _gate = gate;
    }

    // Test seam: when set before the shared runtime is created, that runtime sends its heartbeats here.
    internal static IServerInfoSender? SharedSenderOverride { get; set; }

    internal IServerInfoSender? Sender => _sender;

    internal HeartbeatGuard Guard { get; } = new();

    internal TimeSpan ListTimeout { get; set; } = DefaultListTimeout;

    internal TimeSpan BuildTimeout { get; set; } = DefaultBuildTimeout;

    internal TimeSpan SendTimeout { get; set; } = DefaultSendTimeout;

    // Runs on the request path of tools/list and tools/call: it reads memory only and never throws. A tool call that
    // is no feedback note starts a heartbeat only in a memory that has settled.
    internal static void OnRequest<TParams>(AgentCatTracker tracker, RequestContext<TParams> context, bool needsSettledMemory)
    {
        try
        {
            if (!tracker.Settings.EnableTracing || tracker.ProjectId.Length == 0)
            {
                return;
            }

            AgentCatRuntime.EffectiveFor(tracker.Runtime)?.ServerInfo.Check(tracker, context, needsSettledMemory);
        }
        catch (Exception)
        {
        }
    }

    private void Check<TParams>(AgentCatTracker tracker, RequestContext<TParams> context, bool needsSettledMemory)
    {
        if (_sender is not { } sender || context.Server is not { } server)
        {
            return;
        }

        var identity = ServerIdentity.Of(server.ServerOptions);
        if (!Guard.TryBegin(tracker.ProjectId, identity.Name, needsSettledMemory, _clock.UtcNow))
        {
            return;
        }

        var origin = new HeartbeatOrigin(server, context.JsonRpcRequest.Id, context.User, context.Services);
        new Work(this, sender, tracker.ProjectId, identity, tracker.Settings, origin).Start();
    }

    private void Skipped(string reason) => Write(NotSentPrefix + reason);

    // Every line of the heartbeat is routine for the host logger, whatever its text says.
    private void Write(string line)
    {
        try
        {
            _log.WriteRoutine(line);
        }
        catch (Exception)
        {
        }
    }

    // One attempt. The timer that starts it holds it until the callback runs.
    private sealed class Work
    {
        private readonly ServerInfoHeartbeat _owner;
        private readonly IServerInfoSender _sender;
        private readonly string _projectId;
        private readonly (string Name, string Version) _identity;
        private readonly InjectionSettings _injection;
        private readonly Func<string, CancellationToken, ValueTask<string>>? _redact;
        private readonly HeartbeatOrigin _origin;
        private Timer? _timer;

        internal Work(
            ServerInfoHeartbeat owner,
            IServerInfoSender sender,
            string projectId,
            (string Name, string Version) identity,
            TrackerSettings settings,
            HeartbeatOrigin origin)
        {
            _owner = owner;
            _sender = sender;
            _projectId = projectId;
            _identity = identity;
            _injection = settings.Injection;
            _redact = settings.RedactSensitiveInformation;
            _origin = origin;
        }

        // The timer does not carry the execution context of the request.
        internal void Start()
        {
            try
            {
                var suppress = !ExecutionContext.IsFlowSuppressed();
                var flow = suppress ? ExecutionContext.SuppressFlow() : default;
                try
                {
                    var timer = new Timer(OnTimer, this, Timeout.Infinite, Timeout.Infinite);
                    Volatile.Write(ref _timer, timer);
                    timer.Change(0, Timeout.Infinite);
                }
                finally
                {
                    if (suppress)
                    {
                        flow.Undo();
                    }
                }
            }
            catch (Exception ex)
            {
                _owner.Skipped(ErrorText.Of(ex));
            }
        }

        private static void OnTimer(object? state)
        {
            try
            {
                var work = (Work)state!;
                Interlocked.Exchange(ref work._timer, null)?.Dispose();
                _ = work.RunAsync();
            }
            catch (Exception)
            {
            }
        }

        private async Task RunAsync()
        {
            try
            {
                if (!_owner._gate.IsEnabled())
                {
                    _owner.Skipped("AgentCat is off");
                    return;
                }

                var limit = _owner.BuildTimeout;
                var body = await Bounded.RunAsync(
                    BuildAsync,
                    limit,
                    "the lists and the redaction did not finish within " + Milliseconds(limit) + " ms",
                    CancellationToken.None).ConfigureAwait(false);

                var bytes = body.ToUtf8();
                var status = await PostAsync(bytes).ConfigureAwait(false);
                if (status == 413)
                {
                    bytes = body.WithoutToolSchemas().ToUtf8();
                    status = await PostAsync(bytes).ConfigureAwait(false);
                }

                if (status == 202)
                {
                    _owner.Write(
                        "Sent server info heartbeat | " + body.ToolCount.ToString(CultureInfo.InvariantCulture) + " tools | "
                        + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes");
                }
                else
                {
                    _owner.Skipped("HTTP " + status.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                _owner.Skipped(ErrorText.Of(ex));
            }
        }

        private async Task<ServerInfoBody> BuildAsync(CancellationToken cancellationToken)
        {
            var features = await ServerInfoLists.ReadAsync(_origin, _injection, _owner.ListTimeout, ListSkipped, cancellationToken)
                .ConfigureAwait(false);
            var options = _origin.Server.ServerOptions;
            var body = new ServerInfoBody(
                _projectId,
                _identity.Name,
                _identity.Version,
                options.ServerInfo?.Title ?? string.Empty,
                options.ServerInstructions ?? string.Empty,
                features);
            return _redact is { } redact
                ? await ServerInfoRedactor.RedactAsync(body, redact, cancellationToken).ConfigureAwait(false)
                : body;
        }

        private Task<int> PostAsync(byte[] bytes)
        {
            var limit = _owner.SendTimeout;
            return Bounded.RunAsync(
                token => _sender.PostServerInfoAsync(bytes, token),
                limit,
                "the API did not answer within " + Milliseconds(limit) + " ms",
                CancellationToken.None);
        }

        private void ListSkipped(string list, Exception error)
            => _owner.Write("Server info heartbeat leaves out the " + list + " list: " + ErrorText.Of(error));

        private static string Milliseconds(TimeSpan time) => ((long)time.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
    }
}
