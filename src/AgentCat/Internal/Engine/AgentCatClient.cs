using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Lifecycle;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal sealed class AgentCatClient : IAgentCat
{
    private readonly Func<AgentCatRuntime> _runtime;

    internal AgentCatClient(AgentCatRuntime runtime)
        : this(() => runtime)
    {
    }

    internal AgentCatClient(Func<AgentCatRuntime> runtime) => _runtime = runtime;

    public Task PublishCustomEventAsync(string sessionId, string projectId, CustomEventData? data = null)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            return Task.FromException(new ArgumentException(CustomEvent.ProjectRequired));
        }

        if (sessionId is null)
        {
            return Task.FromException(new ArgumentException(CustomEvent.FirstParameter));
        }

        if (!Enabled())
        {
            return Task.CompletedTask;
        }

        AgentCatRuntime? runtime = null;
        try
        {
            runtime = _runtime();
            var session = string.IsNullOrEmpty(data?.SessionId) ? sessionId : data!.SessionId!;
            var e = CustomEvent.Build(session, data, runtime.Clock.UtcNow, runtime.Log);
            e.ProjectId = projectId;
            runtime.Pipeline.Add(new QueuedEvent(e));
            runtime.Log.WriteRoutine(CustomEvent.Published(session));
        }
        catch (Exception ex)
        {
            Warn(runtime, ex);
        }

        return Task.CompletedTask;
    }

    public Task PublishCustomEventAsync(McpServer server, string projectId, CustomEventData? data = null)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            return Task.FromException(new ArgumentException(CustomEvent.ProjectRequired));
        }

        if (server is null)
        {
            return Task.FromException(new ArgumentException(CustomEvent.FirstParameter));
        }

        // While AgentCat is off no server is tracked, so the server is not looked up.
        if (!Enabled())
        {
            return Task.CompletedTask;
        }

        AgentCatRuntime? runtime = null;
        try
        {
            var options = server.ServerOptions;
            if (!EngineInstaller.TryGetTracker(options, out var tracker))
            {
                // An install was skipped while AgentCat was off, so this server may be one of them: the line goes to
                // this client's runtime log (the current fake's for AgentCatSdk), and the caller's code keeps running.
                if (EnabledGate.SkippedAnInstall)
                {
                    runtime = _runtime();
                    runtime.Log.Write(CustomEvent.NotTrackedWhileOff);
                    return Task.CompletedTask;
                }

                return Task.FromException(new ArgumentException(CustomEvent.NotTracked));
            }

            runtime = AgentCatRuntime.EffectiveFor(tracker.Runtime);
            if (runtime is null)
            {
                return Task.CompletedTask;
            }

            // Without a SessionId, the event takes the session of the traced tool call of this server that encloses
            // the caller; in hook mode the pipeline gives it that session.
            string session;
            string published;
            Func<AgentCatEvent, CancellationToken, ValueTask>? prepare = null;
            if (!string.IsNullOrEmpty(data?.SessionId))
            {
                session = data!.SessionId!;
                published = CustomEvent.Published(session);
            }
            else if (EnclosingToolCall.Of(server) is { } call)
            {
                session = call.SessionId;
                prepare = call.Prepare;
                published = prepare is null ? CustomEvent.Published(session) : CustomEvent.PublishedForCurrentCall;
            }
            else
            {
                session = "";
                runtime.Log.Write(CustomEvent.NoSession, LogSeverity.Warning);
                published = CustomEvent.Published(session);
            }

            var e = CustomEvent.Build(session, data, runtime.Clock.UtcNow, runtime.Log);
            e.ProjectId = projectId;
            var (name, version) = ServerIdentity.Of(options);
            e.ServerName = name;
            e.ServerVersion = version;
            var client = server.ClientInfo;
            e.ClientName = client?.Name;
            e.ClientVersion = client?.Version;
            EventPublisher.Publish(runtime, tracker, e, prepare, redactStrings: false);
            runtime.Log.WriteRoutine(published);
        }
        catch (Exception ex)
        {
            Warn(runtime, ex);
        }

        return Task.CompletedTask;
    }

    public async Task<bool> FlushAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var budget = Timeout.InfiniteTimeSpan;
            if (timeout is { } given)
            {
                if (given < TimeSpan.Zero)
                {
                    budget = TimeSpan.Zero;
                }
                else if (given.TotalMilliseconds < int.MaxValue)
                {
                    budget = given;
                }
            }

            return await _runtime().Pipeline.FlushAsync(budget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runtime = _runtime();
            var shutdown = runtime.Pipeline.ShutdownAsync();
            if (!await EndsFirstAsync(shutdown, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var drained = await shutdown.ConfigureAwait(false);
            await runtime.Diagnostics.FlushAsync(ShutdownDeadline.DiagnosticsBudget).ConfigureAwait(false);
            return drained;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // A runtime that cannot be resolved leaves the event to the paths below, which handle that failure.
    private bool Enabled()
    {
        try
        {
            return _runtime().Gate.IsEnabled();
        }
        catch (Exception)
        {
            return true;
        }
    }

    // True when the task ends before the token is cancelled. The token only ends the wait.
    private static async Task<bool> EndsFirstAsync(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled);
        return await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) == task;
    }

    private static void Warn(AgentCatRuntime? runtime, Exception ex)
    {
        try
        {
            runtime?.Log.Write("Warning: Failed to publish custom event - " + ErrorText.Of(ex));
        }
        catch (Exception)
        {
        }
    }
}
