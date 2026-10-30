using System;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace AgentCat;

// The overloads differ in their first parameter type, so a call never binds to the wrong one.
#pragma warning disable RS0026

/// <summary>Publishes custom events to AgentCat, and flushes or closes the queue of events.</summary>
/// <remarks>
/// A literal <see langword="null"/> as the first argument of <c>PublishCustomEventAsync</c> is ambiguous between the
/// two overloads. Cast it to <see cref="string"/> or <see cref="McpServer"/>.
/// </remarks>
public interface IAgentCat
{
    /// <summary>Publishes a custom event that belongs to a session that you name.</summary>
    /// <param name="sessionId">
    /// The session ID, used as given. <see cref="CustomEventData.SessionId"/> wins when it is not empty. An empty
    /// string is valid and publishes the event without a session.
    /// </param>
    /// <param name="projectId">The AgentCat project ID that receives the event.</param>
    /// <param name="data">The event data. <see langword="null"/> publishes an event without data.</param>
    /// <returns>A task that completes when AgentCat has queued the event, not when it has delivered it.</returns>
    /// <remarks>
    /// <para>
    /// This form does not need a tracked server. The event carries no SDK, server, client or user fields, and the
    /// <c>RedactEvent</c> and <c>RedactSensitiveInformation</c> hooks do not run for it. A <see langword="null"/>
    /// <paramref name="sessionId"/> faults the task, so do not pass a literal <see langword="null"/>: it is
    /// ambiguous with the <see cref="McpServer"/> overload.
    /// </para>
    /// <para>
    /// While AgentCat is off, the method checks its arguments, then publishes nothing and writes nothing. The
    /// <c>AGENTCAT_ENABLED</c> environment variable turns AgentCat on (<c>true</c>, <c>1</c>, <c>yes</c> or <c>on</c>) or off
    /// (<c>false</c>, <c>0</c>, <c>no</c> or <c>off</c>). Without it, AgentCat is on, except in a test run (a process that
    /// loads xUnit, NUnit, MSTest or TUnit), where it is off unless an <see cref="AgentCatFake"/> is current.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Reported in the returned task, never thrown by the call itself: <paramref name="projectId"/> is null or empty
    /// (<c>projectId is required for publishCustomEvent</c>), or <paramref name="sessionId"/> is null
    /// (<c>First parameter must be either an MCP server object or a session ID string</c>).
    /// </exception>
    Task PublishCustomEventAsync(string sessionId, string projectId, CustomEventData? data = null);

    /// <summary>Publishes a custom event through a server that AgentCat tracks.</summary>
    /// <param name="server">
    /// A tracked server. Inside a tool, pass the <see cref="McpServer"/> that the tool receives: the event joins the
    /// session of the tool call only through that instance.
    /// </param>
    /// <param name="projectId">
    /// A non-empty project ID. The event carries the project ID of the tracked server, not this value.
    /// </param>
    /// <param name="data">The event data. <see cref="CustomEventData.SessionId"/> attributes the event to a session.</param>
    /// <returns>A task that completes when AgentCat has queued the event, not when it has delivered it.</returns>
    /// <remarks>
    /// <para>
    /// The session of the event is <see cref="CustomEventData.SessionId"/> when it is not empty. Otherwise, inside a
    /// tool call that AgentCat traces on this same <paramref name="server"/> (the tool and the work it starts, also
    /// after the call returns), the event joins the session of that tool call; anywhere else it has no session and
    /// AgentCat logs a warning.
    /// </para>
    /// <para>
    /// The event carries the server and client identity. The <c>RedactEvent</c> hook runs for it, tracing off drops
    /// it, and a server in telemetry-only mode never sends it. A literal <see langword="null"/> as <paramref name="server"/>
    /// is ambiguous with the string overload. Cast it, and the task faults. If AgentCat was off when a server was set up
    /// in this process, an untracked server does not fault the task: AgentCat logs
    /// <c>Warning: Failed to publish custom event - the server is not tracked (…)</c> instead.
    /// </para>
    /// <para>
    /// While AgentCat is off, the method checks its arguments, then publishes nothing and writes nothing. The
    /// <c>AGENTCAT_ENABLED</c> environment variable turns AgentCat on (<c>true</c>, <c>1</c>, <c>yes</c> or <c>on</c>) or off
    /// (<c>false</c>, <c>0</c>, <c>no</c> or <c>off</c>). Without it, AgentCat is on, except in a test run (a process that
    /// loads xUnit, NUnit, MSTest or TUnit), where it is off unless an <see cref="AgentCatFake"/> is current.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Reported in the returned task, never thrown by the call itself: <paramref name="projectId"/> is null or empty
    /// (<c>projectId is required for publishCustomEvent</c>), <paramref name="server"/> is null
    /// (<c>First parameter must be either an MCP server object or a session ID string</c>), or the server is not
    /// tracked (<c>Server is not tracked. Please call agentcat.track() first or provide a session ID string.</c>).
    /// </exception>
    /// <example>
    /// A tool that gets <see cref="IAgentCat"/> from dependency injection publishes a custom event into the session of
    /// its tool call:
    /// <code>
    /// [McpServerTool(Name = "complete_task")]
    /// public static async Task&lt;string&gt; CompleteTask(McpServer server, IAgentCat agentCat, string taskId)
    /// {
    ///     await agentCat.PublishCustomEventAsync(server, "proj_123", new CustomEventData
    ///     {
    ///         ResourceName = "task_completed",
    ///         Parameters = new JsonObject { ["taskId"] = taskId },
    ///     });
    ///     return "Task " + taskId + " completed";
    /// }
    /// </code>
    /// </example>
    Task PublishCustomEventAsync(McpServer server, string projectId, CustomEventData? data = null);

    /// <summary>
    /// Waits until AgentCat has delivered the queued events and sent their requests to the configured exporters.
    /// </summary>
    /// <param name="timeout">
    /// How long to wait. <see langword="null"/> waits without a limit, a negative value counts as zero, and a value of
    /// <see cref="int.MaxValue"/> milliseconds or more waits without a limit.
    /// </param>
    /// <param name="cancellationToken">
    /// Ends the wait when it is cancelled, as a timeout does. It only ends the wait: AgentCat keeps sending the queued
    /// events. A token that is already cancelled acts as a zero timeout.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when every queued event is delivered or dropped and no exporter request is left,
    /// <see langword="false"/> when the wait ends first, by the timeout or by <paramref name="cancellationToken"/>.
    /// </returns>
    /// <remarks>
    /// This method never throws, even when <paramref name="cancellationToken"/> is cancelled. While an
    /// <see cref="AgentCatFake"/> is current, it waits for the events of the fake instead of the process-wide queue.
    /// </remarks>
    Task<bool> FlushAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>Closes the AgentCat queue, then sends the queued events for at most 5 seconds.</summary>
    /// <param name="cancellationToken">
    /// Ends the wait when it is cancelled. It only ends the wait: the queue stays closed, and AgentCat keeps sending the
    /// queued events for the rest of the 5 seconds. A token that is already cancelled ends the wait at once.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when nothing was left: every queued event is delivered or dropped, and no exporter request
    /// is left. <see langword="false"/> when events or exporter requests were still queued or in flight after 5 seconds,
    /// or when <paramref name="cancellationToken"/> ended the wait.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The queue is process-wide. After this call, AgentCat drops every new event of the process, tool-call events
    /// included, and logs <c>Queue is shutting down, event dropped</c> for each one. Call it once, when your own code
    /// ends the process. A host does not need it: <c>WithAgentCat</c> flushes after the host stops, and AgentCat closes
    /// the queue at process exit, unless the first install set <see cref="AgentCatOptions.DisableShutdownHandlers"/>.
    /// </para>
    /// <para>
    /// A second call waits for the same drain. After the drain, AgentCat sends its last diagnostics batch and waits at
    /// most 1 second for it.
    /// </para>
    /// <para>
    /// This method never throws. While an <see cref="AgentCatFake"/> is current, it closes the queue of the fake instead
    /// of the process-wide queue.
    /// </para>
    /// </remarks>
    Task<bool> ShutdownAsync(CancellationToken cancellationToken = default);
}
