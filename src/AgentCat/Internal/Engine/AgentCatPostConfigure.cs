using System;
using AgentCat.Internal.Lifecycle;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

// One per WithAgentCat call. Stateless HTTP runs it for every POST, concurrently: the first options instance that the
// runtime's gate lets through decides for the whole registration, and every later instance only gets the decided
// tracker's cached filters, unless that tracker was installed under a fake that is gone. An instance that the gate
// stops stays untracked and leaves the decision to a later one.
internal sealed class AgentCatPostConfigure : IPostConfigureOptions<McpServerOptions>
{
    private readonly string? _projectId;
    private Action<AgentCatOptions>? _configure;
    private readonly Func<AgentCatRuntime> _runtime;
    private readonly Func<AgentCatRuntime> _fallback;
    private ILoggerFactory? _loggerFactory;
    private readonly object _lock = new();
    private AgentCatTracker? _tracker;
    private bool _deciding;
    private volatile bool _decided;

    internal AgentCatPostConfigure(
        string? projectId,
        Action<AgentCatOptions>? configure,
        Func<AgentCatRuntime> runtime,
        Func<AgentCatRuntime>? fallback = null,
        ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _projectId = projectId;
        _configure = configure;
        _runtime = runtime;
        _fallback = fallback ?? (static () => AgentCatRuntime.Current);
    }

    public void PostConfigure(string? name, McpServerOptions options)
    {
        if (options is null)
        {
            return;
        }

        try
        {
            if (!_decided)
            {
                lock (_lock)
                {
                    if (!_decided)
                    {
                        // The lock is re-entrant: a configure callback that builds server options on this thread
                        // comes back here, and that nested instance gets nothing.
                        if (_deciding)
                        {
                            return;
                        }

                        _deciding = true;
                        try
                        {
                            Decide(options);
                        }
                        finally
                        {
                            _deciding = false;
                        }

                        return;
                    }
                }
            }

            // A tracker installed under a fake that is gone has nowhere to publish: this instance stays the
            // customer's own. One volatile read, and never null for a tracker on the shared runtime.
            var tracker = _tracker;
            if (tracker is not null && AgentCatRuntime.EffectiveFor(tracker.Runtime) is not null)
            {
                EngineInstaller.Install(options, tracker);
            }
        }
        catch (Exception)
        {
        }
    }

    // Leaves the registration undecided, with its configure callback and logger factory, when the gate says off.
    private void Decide(McpServerOptions options)
    {
        AgentCatRuntime? runtime = null;
        try
        {
            runtime = _runtime();
        }
        catch (Exception ex)
        {
            WriteFailure(_fallback, ex);
        }

        if (runtime is not null && !runtime.Gate.AllowsInstall())
        {
            return;
        }

        var configure = _configure;
        _configure = null;
        try
        {
            _tracker = runtime is null ? null : Decide(options, runtime, configure);
        }
        finally
        {
            _decided = true;
        }
    }

    private AgentCatTracker? Decide(McpServerOptions options, AgentCatRuntime runtime, Action<AgentCatOptions>? configure)
    {
        AgentCatOptions? agentCatOptions = null;
        if (configure is not null)
        {
            agentCatOptions = new AgentCatOptions();
            try
            {
                configure(agentCatOptions);
            }
            catch (Exception ex)
            {
                runtime.Diagnostics.Init(
                    AgentCatTracker.ResolveProjectId(_projectId, Environment.GetEnvironmentVariable),
                    agentCatOptions.DisableDiagnostics);
                ShutdownHandlers.EnsureRegistered(runtime, agentCatOptions.DisableShutdownHandlers, withSignals: false);
                runtime.Log.Write("Warning: Failed to track server - " + ErrorText.Of(ex));
                return null;
            }
        }

        AdoptHostLogger(runtime, agentCatOptions);

        var installedBefore = EngineInstaller.IsInstalled(options);
        AgentCatTracker? tracker = null;
        try
        {
            tracker = AgentCatEngine.InstallEnabled(options, _projectId, agentCatOptions, runtime, "di", Environment.GetEnvironmentVariable);
        }
        catch (Exception)
        {
        }

        // A log that throws after the install makes Install report a failure, yet this instance is tracked: the
        // registration keeps that tracker, so every instance is tracked the same way.
        if (tracker is null && !installedBefore && EngineInstaller.TryGetTracker(options, out var installed))
        {
            tracker = installed;
        }

        return tracker;
    }

    private void AdoptHostLogger(AgentCatRuntime runtime, AgentCatOptions? agentCatOptions)
    {
        try
        {
            if (agentCatOptions is { LogToHost: true } && runtime.Log is ProcessLog processLog
                && _loggerFactory is { } factory)
            {
                processLog.TryAdoptHost(factory);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _loggerFactory = null;
        }
    }

    // Written only when the runtime's gate says on: while AgentCat is off, a failure writes nothing either.
    internal static void WriteFailure(Func<AgentCatRuntime> runtime, Exception error)
    {
        try
        {
            var target = runtime();
            if (target.Gate.IsEnabled())
            {
                target.Log.Write("Warning: Failed to track server - " + ErrorText.Of(error));
            }
        }
        catch (Exception)
        {
        }
    }
}
