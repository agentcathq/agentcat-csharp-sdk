using System.Threading;
using System;
using System.Globalization;
using AgentCat.Internal.Injection;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal sealed class AgentCatTracker
{
    private static readonly int McpMajor = typeof(McpServer).Assembly.GetName().Version?.Major ?? 0;

    private readonly string _flavor;

    private AgentCatTracker(string projectId, TrackerSettings settings, AgentCatRuntime runtime, string flavor, bool setupFailed = false)
    {
        ProjectId = projectId;
        Settings = settings;
        Runtime = runtime;
        _flavor = flavor;
        SetupFailed = setupFailed;
        Seams = new EngineSeams(this);
    }

    internal string ProjectId { get; }

    // Set when Create could not apply the options; such a tracker traces nothing and must not be installed.
    internal bool SetupFailed { get; }

    internal TrackerSettings Settings { get; }

    internal AgentCatRuntime Runtime { get; }

    // The filters every install of this tracker inserts. They hold the tracker and never an options instance.
    internal EngineSeams Seams { get; }

    internal InjectionState State { get; } = new();

    internal ToolRegistries Registries { get; } = new();

    internal TimeSpan RebuildTimeout { get; set; } = RegistryRebuilder.DefaultTimeout;

    // Test seam: invoked with "strip" before each strip, and with "prepare", "finish" and "fail" at the start of each
    // traced tools/call stage.
    internal Action<string>? OnCallStage { get; set; }

    private string ShownProject => ProjectId.Length == 0 ? "(telemetry-only)" : ProjectId;

    // Reads the options and applies the API base URL. Logs nothing on success.
    internal static AgentCatTracker Create(
        string? projectId,
        AgentCatOptions? options,
        AgentCatRuntime runtime,
        string flavor,
        Func<string, string?> environment)
    {
        try
        {
            var settings = TrackerSettings.From(options);
            runtime.ApplyBaseUrl(settings.ApiBaseUrl, environment);
            var resolved = ResolveProjectId(projectId, environment);
            return new AgentCatTracker(resolved, settings, runtime, flavor);
        }
        catch (Exception ex)
        {
            runtime.Log.Write("Warning: Failed to track server - " + ErrorText.Of(ex));
            return new AgentCatTracker(projectId ?? "", TrackerSettings.From(new AgentCatOptions { EnableTracing = false }), runtime, flavor, setupFailed: true);
        }
    }

    // A null project ID reads AGENTCAT_PROJECT_ID.
    internal static string ResolveProjectId(string? projectId, Func<string, string?> environment)
        => projectId ?? environment("AGENTCAT_PROJECT_ID") ?? "";

    internal void LogSetupStarted()
        => Runtime.Log.WriteRoutine($"AgentCat setup started | project {ShownProject} | sdk v{McpMajor}/{_flavor} | signals filters");

    internal const string LogToHostWarning = "WARN: AgentCat LogToHost works only with WithAgentCat; Track keeps writing to the AgentCat log file";

    private static int _logToHostWarned;

    // Test seam: lets the once-per-process warning be written again.
    internal static void ResetLogToHostWarning() => Interlocked.Exchange(ref _logToHostWarned, 0);

    internal void WarnIfLogToHostUnsupported()
    {
        if (_flavor == "options" && Settings.LogToHost && Interlocked.Exchange(ref _logToHostWarned, 1) == 0)
        {
            Runtime.Log.Write(LogToHostWarning);
        }
    }

    // TS `!projectId && !options.exporters`, where an empty Exporters is "not configured".
    internal void WarnIfNoProject()
    {
        if (ProjectId.Length == 0 && Settings.Exporters.Count == 0)
        {
            Runtime.Log.Write("Warning: No projectId provided and no exporters configured. Events will not be sent anywhere.");
        }
    }

    internal void LogSetupComplete()
        => Runtime.Log.WriteRoutine(
            $"AgentCat setup complete | project {ShownProject} | tracing={Flag(Settings.EnableTracing)} context={Flag(Settings.EnableToolCallContext)} reportMissing={Flag(Settings.EnableReportMissing)} exporters={Settings.Exporters.Count.ToString(CultureInfo.InvariantCulture)}");

    private static string Flag(bool value) => value ? "true" : "false";
}
