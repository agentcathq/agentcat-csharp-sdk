using System;
using AgentCat.Internal.Lifecycle;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry;
using AgentCat.Internal.Text;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal static class AgentCatEngine
{
    private const string AlreadyTracked = "[SESSION DEBUG] track() - Server already being tracked, skipping initialization";

    internal static AgentCatTracker? Install(
        McpServerOptions? options,
        string? projectId,
        AgentCatOptions? agentCatOptions,
        AgentCatRuntime runtime,
        string flavor)
        => Install(options, projectId, agentCatOptions, runtime, flavor, Environment.GetEnvironmentVariable);

    // The gate comes first: when AgentCat is off, nothing is installed and no line is written. Without options there is
    // no server to install on, so the gate does not count a stopped install.
    internal static AgentCatTracker? Install(
        McpServerOptions? options,
        string? projectId,
        AgentCatOptions? agentCatOptions,
        AgentCatRuntime runtime,
        string flavor,
        Func<string, string?> environment)
        => (options is null ? runtime.Gate.IsEnabled() : runtime.Gate.AllowsInstall())
            ? InstallEnabled(options, projectId, agentCatOptions, runtime, flavor, environment)
            : null;

    // For a caller that has asked the gate. The order of TS track(): a repeated call still applies the base URL and
    // logs the started line.
    internal static AgentCatTracker? InstallEnabled(
        McpServerOptions? options,
        string? projectId,
        AgentCatOptions? agentCatOptions,
        AgentCatRuntime runtime,
        string flavor,
        Func<string, string?> environment)
    {
        try
        {
            runtime.Diagnostics.Init(
                AgentCatTracker.ResolveProjectId(projectId, environment),
                agentCatOptions?.DisableDiagnostics ?? false);

            // A .NET host owns SIGTERM and SIGINT, and the hosted lifecycle flushes after it stops: only Track adds
            // signal handlers.
            ShutdownHandlers.EnsureRegistered(runtime, agentCatOptions?.DisableShutdownHandlers ?? false, withSignals: flavor == "options");
            if (options is null)
            {
                LogFailure(runtime, new ArgumentNullException(nameof(options)));
                return null;
            }

            var tracker = AgentCatTracker.Create(projectId, agentCatOptions, runtime, flavor, environment);
            if (tracker.SetupFailed)
            {
                return null;
            }

            tracker.LogSetupStarted();
            tracker.WarnIfLogToHostUnsupported();

            // The install runs before the exporters and the no-project warning, so a concurrent call that loses the
            // install logs like a repeated track(). The lines still come in TS order: exporters, warning, then complete
            // or the failure.
            var outcome = EngineInstaller.TryInstall(options, tracker, out var failure);
            if (outcome == EngineInstaller.Outcome.AlreadyInstalled)
            {
                runtime.Log.Write(AlreadyTracked);
                return null;
            }

            if (outcome == EngineInstaller.Outcome.AlternateHandler)
            {
                runtime.Log.Write(EngineInstaller.AlternateHandlerWarning);
                return null;
            }

            // TS order: the exporters come after the already-tracked check and before the warning, and they stay when
            // the install failed.
            TelemetryManager.Install(runtime, tracker.Settings.Exporters);
            tracker.WarnIfNoProject();
            if (outcome != EngineInstaller.Outcome.Installed)
            {
                if (failure is not null)
                {
                    LogFailure(runtime, failure);
                }

                return null;
            }

            tracker.LogSetupComplete();
            return tracker;
        }
        catch (Exception ex)
        {
            LogFailure(runtime, ex);
            return null;
        }
    }

    private static void LogFailure(AgentCatRuntime runtime, Exception ex)
        => runtime.Log.Write("Warning: Failed to track server - " + ErrorText.Of(ex));
}
