using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Injection;

namespace AgentCat.Internal.Engine;

internal sealed class TrackerSettings
{
    private TrackerSettings(AgentCatOptions o)
    {
        EnableReportMissing = o.EnableReportMissing;
        EnableTracing = o.EnableTracing;
        EnableToolCallContext = o.EnableToolCallContext;
        CustomContextDescription = o.CustomContextDescription;
        EnableAgentTracking = o.EnableAgentTracking;
        ResolveSessionId = o.ResolveSessionId;
        Identify = o.Identify;
        EventTags = o.EventTags;
        EventProperties = o.EventProperties;
        RedactSensitiveInformation = o.RedactSensitiveInformation;
        RedactEvent = o.RedactEvent;
        ApiBaseUrl = o.ApiBaseUrl;
        LogToHost = o.LogToHost;
        Exporters = o.Exporters.Select(static e => new KeyValuePair<string, AgentCatExporter?>(e.Key, e.Value)).ToArray();
        Injection = new InjectionSettings(
            EnableReportMissing,
            EnableTracing && ResolveSessionId is null,
            EnableTracing && EnableAgentTracking,
            EnableToolCallContext,
            CustomContextDescription);
    }

    public bool EnableReportMissing { get; }

    public bool EnableTracing { get; }

    public bool EnableToolCallContext { get; }

    public string? CustomContextDescription { get; }

    public bool EnableAgentTracking { get; }

    public Func<AgentCatToolCall, CancellationToken, ValueTask<string?>>? ResolveSessionId { get; }

    public Func<AgentCatToolCall, CancellationToken, ValueTask<UserIdentity?>>? Identify { get; }

    public Func<AgentCatToolCall, CancellationToken, ValueTask<IReadOnlyDictionary<string, string>?>>? EventTags { get; }

    public Func<AgentCatToolCall, CancellationToken, ValueTask<JsonObject?>>? EventProperties { get; }

    public Func<string, CancellationToken, ValueTask<string>>? RedactSensitiveInformation { get; }

    public Func<AgentCatEvent, CancellationToken, ValueTask<AgentCatEvent?>>? RedactEvent { get; }

    public string? ApiBaseUrl { get; }

    public bool LogToHost { get; }

    // The named exporter configurations of this install, in order. Empty means "not configured".
    public IReadOnlyList<KeyValuePair<string, AgentCatExporter?>> Exporters { get; }

    public InjectionSettings Injection { get; }

    public TimeSpan PendingHookBudget { get; internal set; } = TimeSpan.FromSeconds(30);

    // Test seam: waits out one pending hook's budget.
    internal Func<TimeSpan, CancellationToken, Task> HookBudgetDelay { get; set; } = Task.Delay;

    public static TrackerSettings From(AgentCatOptions? options) => new(options ?? new AgentCatOptions());
}
