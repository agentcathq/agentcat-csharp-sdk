using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Collections;

namespace AgentCat;

/// <summary>Options that configure AgentCat tracking. AgentCat reads them once, when tracking starts.</summary>
public sealed class AgentCatOptions
{
    /// <summary>Adds the <c>get_more_tools</c> tool, which lets agents report missing capabilities. Default <see langword="true"/>.</summary>
    public bool EnableReportMissing { get; set; } = true;

    /// <summary>Records events and injects the <c>session_id</c> parameter into tool schemas. Default <see langword="true"/>.</summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>Injects a <c>context</c> parameter into tool schemas so agents explain why they call a tool. Default <see langword="true"/>.</summary>
    public bool EnableToolCallContext { get; set; } = true;

    /// <summary>Replaces the default description of the injected <c>context</c> parameter.</summary>
    public string? CustomContextDescription { get; set; }

    /// <summary>Injects an <c>agent_id</c> parameter into tool schemas to track agents. Default <see langword="false"/>.</summary>
    public bool EnableAgentTracking { get; set; }

    /// <summary>Supplies the session identifier of a tool call. When set, AgentCat does not inject <c>session_id</c> into tool schemas.</summary>
    /// <remarks>
    /// <para>Return <see langword="null"/> to let AgentCat derive the session identifier itself.</para>
    /// <para>AgentCat calls the hook when the tool call starts, and the hook runs alongside the tool. AgentCat waits at most 30 seconds for the result, then publishes the event without it. The <see cref="CancellationToken"/> is cancelled when AgentCat stops waiting for the result: after those 30 seconds, once AgentCat has used the result, or when AgentCat drops the event (a full queue or a shutdown). It is not the cancellation token of the request.</para>
    /// </remarks>
    public Func<AgentCatToolCall, CancellationToken, ValueTask<string?>>? ResolveSessionId { get; set; }

    /// <summary>Identifies the user behind a tool call.</summary>
    /// <remarks>
    /// <para>Return <see langword="null"/> for an anonymous call.</para>
    /// <para>The hook runs and its <see cref="CancellationToken"/> is cancelled as described for <see cref="ResolveSessionId"/>.</para>
    /// </remarks>
    public Func<AgentCatToolCall, CancellationToken, ValueTask<UserIdentity?>>? Identify { get; set; }

    /// <summary>Supplies tags to attach to the event of a tool call.</summary>
    /// <remarks>
    /// <para>The hook runs and its <see cref="CancellationToken"/> is cancelled as described for <see cref="ResolveSessionId"/>.</para>
    /// </remarks>
    public Func<AgentCatToolCall, CancellationToken, ValueTask<IReadOnlyDictionary<string, string>?>>? EventTags { get; set; }

    /// <summary>Supplies custom properties to attach to the event of a tool call.</summary>
    /// <remarks>
    /// <para>The hook runs and its <see cref="CancellationToken"/> is cancelled as described for <see cref="ResolveSessionId"/>.</para>
    /// </remarks>
    public Func<AgentCatToolCall, CancellationToken, ValueTask<JsonObject?>>? EventProperties { get; set; }

    /// <summary>Redacts every string in an event before delivery. It receives each string and returns its replacement.</summary>
    public Func<string, CancellationToken, ValueTask<string>>? RedactSensitiveInformation { get; set; }

    /// <summary>Redacts a whole event before delivery. Return <see langword="null"/> to drop the event.</summary>
    /// <remarks>
    /// <para>
    /// The headers of the HTTP request are in <c>parameters.extra.requestInfo.headers</c>, as received, and its URL is in
    /// <c>parameters.extra.requestInfo.url</c>. The URL keeps its query string, which can carry an API key or other
    /// credentials. The hook also gets custom events, whose <c>parameters</c> can have any shape, and an exception in the
    /// hook drops the event, so test each level by type. This sample removes the <c>authorization</c> and <c>cookie</c>
    /// headers and the query string; remove any other header that carries a secret the same way:
    /// </para>
    /// <code>
    /// options.RedactEvent = (e, _) =>
    /// {
    ///     if (e.Parameters is JsonObject p &amp;&amp; p["extra"] is JsonObject x &amp;&amp; x["requestInfo"] is JsonObject info)
    ///     {
    ///         if (info["headers"] is JsonObject headers)
    ///         {
    ///             headers.Remove("authorization");
    ///             headers.Remove("cookie");
    ///         }
    ///
    ///         if (info["url"] is JsonValue url &amp;&amp; url.TryGetValue(out string? text) &amp;&amp; text.IndexOf('?') is var query and >= 0)
    ///         {
    ///             info["url"] = text.Substring(0, query);
    ///         }
    ///     }
    ///
    ///     return new ValueTask&lt;AgentCatEvent?&gt;(e);
    /// };
    /// </code>
    /// </remarks>
    public Func<AgentCatEvent, CancellationToken, ValueTask<AgentCatEvent?>>? RedactEvent { get; set; }

    /// <summary>
    /// Telemetry exporters that send every event to other observability tools as well, by name. Empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Add an <see cref="OtlpExporter"/>, <see cref="DatadogExporter"/>, <see cref="SentryExporter"/> or
    /// <see cref="PostHogExporter"/> under a name of your choice, for example
    /// <c>Exporters = { ["otlp"] = new OtlpExporter("http://localhost:4318") }</c>. The name appears only in AgentCat log
    /// lines, and one type can appear under several names. The exporters run in the order you added them, and AgentCat
    /// sets them up in that order. Keys are case-sensitive and must not be <see langword="null"/>, and <c>Add</c> with a
    /// key that is already there throws <see cref="ArgumentException"/>, as for any dictionary.
    /// </para>
    /// <para>
    /// Exporters are first-wins for the whole process: the first <c>Track</c> or <c>WithAgentCat</c> install with
    /// exporters sets them up, and every server in the process then exports through them. AgentCat ignores the exporters
    /// of later installs, without a log line. An empty dictionary means "not configured": it sets nothing up, so a later
    /// install can still add exporters, and the warning about a missing project ID stays. While an
    /// <see cref="AgentCatFake"/> is current, AgentCat sets up no exporters.
    /// </para>
    /// <para>
    /// With exporters and an empty project ID, AgentCat tracks the server and sends its events to the exporters only.
    /// AgentCat calls the exporters for every attempt to send an event to AgentCat, so an event that AgentCat retries
    /// reaches each exporter again. An exporter never delays or stops the delivery to AgentCat, and its failures only
    /// write lines to the AgentCat log.
    /// </para>
    /// <para>
    /// AgentCat reads the dictionary and the exporter configurations, <see cref="OtlpExporter.Headers"/> included, when it
    /// installs. Later changes have no effect.
    /// </para>
    /// </remarks>
    public IDictionary<string, AgentCatExporter> Exporters { get; } = new InsertionOrderedDictionary<string, AgentCatExporter>(StringComparer.Ordinal);

    /// <summary>Base URL of the AgentCat API. Overrides the <c>AGENTCAT_API_URL</c> and <c>MCPCAT_API_URL</c> environment variables.</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>Turns off SDK diagnostics, which send AgentCat's own log lines to AgentCat. Default <see langword="false"/>.</summary>
    /// <remarks>
    /// <para>Diagnostics send every AgentCat log line to <c>otel.agentcat.com</c>, as the TypeScript SDK does. The lines name tools and session IDs, carry the messages of hook errors, and the per-event <c>Successfully sent event</c> line ends with the actor's given ID. They carry no request or response payloads. The first install in the process decides, with its own value of this option.</para>
    /// <para>The environment variables <c>AGENTCAT_DISABLE_DIAGNOSTICS</c> and <c>DISABLE_DIAGNOSTICS</c> (<c>true</c>, <c>1</c>, <c>yes</c> or <c>on</c>) also turn them off. Diagnostics stay off in a test run unless <c>AGENTCAT_DISABLE_DIAGNOSTICS</c> is <c>false</c>, <c>0</c>, <c>no</c> or <c>off</c>.</para>
    /// </remarks>
    public bool DisableDiagnostics { get; set; }

    /// <summary>Sends AgentCat log lines to the host <c>ILoggerFactory</c> (category <c>AgentCat</c>) instead of the AgentCat log file. Default <see langword="false"/>.</summary>
    /// <remarks>
    /// <para>This works only with <c>WithAgentCat</c>, because only dependency injection supplies a logger factory. <c>AgentCatSdk.Track</c> keeps writing to the log file and logs a warning.</para>
    /// <para>The first <c>WithAgentCat</c> call with this option set decides for the whole process. If the service provider has no <c>ILoggerFactory</c>, AgentCat keeps writing to the file.</para>
    /// <para>A line that starts with <c>WARN:</c> logs at Warning. Else a line that contains <c>fail</c> or <c>error</c>, in any ASCII case, logs at Error. Else a line that contains <c>Warning:</c> logs at Warning. All other lines log at Information. Routine lines that carry a value of yours or an ID, such as <c>Successfully sent event</c> (it ends with the actor's ID), the setup lines and the exporter lines, always log at Information, so a tool, actor or tag named <c>search_errors</c> never raises a line. Lines that report a lost event (queue full, shutdown), an ignored custom event or tag, or an unknown exporter log at Warning or Error whatever their text says. A filter that sets the <c>AgentCat</c> category to Warning keeps every failure line and every warning, and drops the routine lines.</para>
    /// <para>A stdio server must send console logs to stderr, because stdout carries the protocol. Set <c>LogToStandardErrorThreshold = LogLevel.Trace</c> on the console logger, as the <c>mcpserver</c> template does.</para>
    /// </remarks>
    public bool LogToHost { get; set; }

    /// <summary>
    /// Turns off the process-exit drain and, for <c>Track</c>, the SIGTERM and SIGINT drains. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>The first install in the process decides, with its own value of this option. An install while an <see cref="AgentCatFake"/> is current does not count.</para>
    /// <para>The flush after a host stops, which <c>WithAgentCat</c> adds, stays on. <see cref="AgentCatSdk.FlushAsync"/> and <see cref="AgentCatSdk.ShutdownAsync"/> still work: without these handlers, events that are still queued when the process ends are lost unless you call one of them first.</para>
    /// </remarks>
    public bool DisableShutdownHandlers { get; set; }
}
