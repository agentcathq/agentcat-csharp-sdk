using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Runtime;
using ModelContextProtocol.Server;

namespace AgentCat;

/// <summary>Static entry points of AgentCat.</summary>
public static class AgentCatSdk
{
    private static readonly AgentCatClient Client = new(static () => AgentCatRuntime.Current);

    /// <summary>Adds AgentCat tracking to the MCP server that <paramref name="options"/> will configure.</summary>
    /// <param name="options">The options that you pass to <c>McpServer.Create</c>.</param>
    /// <param name="projectId">
    /// The AgentCat project ID. <see langword="null"/> reads the <c>AGENTCAT_PROJECT_ID</c> environment variable.
    /// An empty string, or no project ID at all, tracks the server but sends no events to AgentCat.
    /// </param>
    /// <param name="agentCatOptions">The tracking options. <see langword="null"/> uses the defaults.</param>
    /// <returns>The same <paramref name="options"/> instance, or <see langword="null"/> when <paramref name="options"/> is <see langword="null"/>.</returns>
    /// <remarks>
    /// <para>
    /// Use this method when you create the server yourself with <c>McpServer.Create</c>. Call it before
    /// <c>McpServer.Create</c>: the server reads its filters when it is created. HTTP servers, stateful or stateless,
    /// get their options from dependency injection in ASP.NET Core; use <c>WithAgentCat</c> on the
    /// <c>IMcpServerBuilder</c> for them.
    /// </para>
    /// <para>
    /// AgentCat reads <paramref name="agentCatOptions"/> once, during this call. Later changes to that object have
    /// no effect.
    /// </para>
    /// <para>
    /// A second call on the same <paramref name="options"/> instance installs nothing, and the first call's project
    /// ID and options stay. The second call still applies its API base URL (<see cref="AgentCatOptions.ApiBaseUrl"/>,
    /// else the <c>AGENTCAT_API_URL</c> environment variable, else <c>MCPCAT_API_URL</c>), because all servers in the
    /// process share one event queue.
    /// </para>
    /// <para>
    /// AgentCat adds its <c>tools/call</c> filters at both ends of the filter list. A filter that you insert at index 0
    /// after this call runs outside AgentCat. When such a filter turns a tool exception into its own result, that
    /// result still gets the session text, but the event records the error result that the MCP SDK builds from the
    /// exception, which the client does not see.
    /// </para>
    /// <para>
    /// On ModelContextProtocol 1.x, AgentCat hands the tool's own result object outward when it adds nothing to it. A
    /// filter outside AgentCat that changes that result in place therefore races the event's later read: the event can
    /// carry the change or lose its response field, and the response to the client is not affected. A filter that needs
    /// to change the result returns a new one.
    /// </para>
    /// <para>
    /// On ModelContextProtocol 2.x, an error result built from a tool exception gets no session text in two cases:
    /// implicit MRTR (a tool that asks the client for input on a stateful session), and a tool call that runs as a
    /// background task of the MCP Tasks extension.
    /// </para>
    /// <para>
    /// On an ASP.NET Core server, AgentCat copies the HTTP request headers and URL into each tool-call event, at
    /// <c>parameters.extra.requestInfo</c>, and into <see cref="AgentCatToolCall.Headers"/>. This method registers
    /// nothing, so AgentCat captures only when your app registered an <c>IHttpContextAccessor</c> (for example with
    /// <c>AddHttpContextAccessor()</c>). The headers go out as received, including <c>authorization</c> and
    /// <c>cookie</c>, so redact them if they must not leave your process.
    /// AgentCat has no request to read on stdio servers, on .NET Framework and <c>netstandard2.0</c> hosts, and with
    /// <c>PerSessionExecutionContext = true</c>. It also skips the read for a tool call whose client opts in to MCP 2.x
    /// tasks, because the MCP Tasks extension can run that call after the request has ended. The event then has no
    /// <c>requestInfo</c>, and <see cref="AgentCatToolCall.Headers"/> is empty. Under the
    /// legacy SSE transport the request is the session's <c>GET /sse</c> request, not the <c>POST</c> that carried the
    /// call. The URL keeps its query string, which can carry an API key or other credentials. Remove credentials with
    /// <see cref="AgentCatOptions.RedactEvent"/>.
    /// </para>
    /// <para>
    /// While an <see cref="AgentCatFake"/> is current, the server records its events into that fake and sends nothing. A
    /// server tracked while a fake was current never sends events: when no fake is current, AgentCat drops them. See
    /// <see cref="Fake"/>.
    /// </para>
    /// <para>
    /// While AgentCat is off, this method installs nothing, writes nothing, not even the AgentCat log file, and returns
    /// <paramref name="options"/> unchanged. The <c>AGENTCAT_ENABLED</c> environment variable turns AgentCat on
    /// (<c>true</c>, <c>1</c>, <c>yes</c> or <c>on</c>) or off (<c>false</c>, <c>0</c>, <c>no</c> or <c>off</c>). Without it,
    /// AgentCat is on, except in a test run (a process that loads xUnit, NUnit, MSTest or TUnit), where it is off unless
    /// an <see cref="AgentCatFake"/> is current.
    /// </para>
    /// <para>
    /// When the process ends, AgentCat sends the queued events, then its last diagnostics batch: at process exit
    /// (<c>AppDomain.ProcessExit</c>), and, on .NET 8 and later, on SIGTERM and SIGINT, because .NET 10 no longer raises
    /// <c>ProcessExit</c> on SIGTERM. These handlers, and the flush after a host stops, share one stop budget: about 6
    /// seconds for the whole stop (5 for the events, then at most 1 for the diagnostics batch), however many of them
    /// run. The first one to start sets the 5-second deadline for the events, and a later one waits only for the time
    /// that is left. A stop that sent every queued event ends the budget, so a process that keeps running gets a new
    /// one at its next stop. The signal handlers never cancel the signal, so the process ends as usual, with its usual
    /// exit code. They do nothing when a handler that ran before them, a .NET host's for example, has cancelled the
    /// signal. Events that are still queued after an unhandled exception or a SIGKILL are lost. On .NET Framework, all
    /// <c>ProcessExit</c> handlers of the process share 2 seconds.
    /// </para>
    /// <para>
    /// .NET 8 calls signal handlers in the order they were registered, and .NET 10 calls the newest first. So AgentCat's
    /// handler can run before the handler of a .NET host in the same process: on .NET 10 when the host started before
    /// this call, on .NET 8 when it started after it. AgentCat then sends the queued events first, and the host's
    /// graceful stop can start up to 6 seconds later (5 for the events, then at most 1 for the diagnostics batch). The
    /// handler does not close the queue. When it sent every queued event, the flush after the host stops gets a new
    /// 5-second budget for the events of that stop; when it ran out of time, that flush does not wait for events again.
    /// </para>
    /// <para>
    /// The first install in the process, while AgentCat is on, decides about these handlers: <c>Track</c> adds the
    /// process-exit and signal handlers, <c>WithAgentCat</c> adds only the process-exit handler, and
    /// <see cref="AgentCatOptions.DisableShutdownHandlers"/> on that install adds none.
    /// </para>
    /// <para>
    /// This method never throws. When it cannot track the server, it logs a warning and returns
    /// <paramref name="options"/> unchanged. The warning is <c>Warning: Failed to track server - {error}</c>, or, when
    /// <c>Handlers.CallToolWithAlternateHandler</c> is set (ModelContextProtocol 2.x),
    /// <c>Warning: AgentCat did not instrument this server: …</c>, because <c>McpServer.Create</c> rejects
    /// <c>tools/call</c> filters with that handler.
    /// </para>
    /// </remarks>
    [return: NotNullIfNotNull(nameof(options))]
    public static McpServerOptions? Track(McpServerOptions? options, string? projectId = null, AgentCatOptions? agentCatOptions = null)
        => Track(options, projectId, agentCatOptions, static () => AgentCatRuntime.Current);

    [return: NotNullIfNotNull(nameof(options))]
    internal static McpServerOptions? Track(McpServerOptions? options, string? projectId, AgentCatOptions? agentCatOptions, Func<AgentCatRuntime> runtime)
    {
        AgentCatRuntime resolved;
        try
        {
            resolved = runtime();
        }
        catch (Exception)
        {
            return options;
        }

        AgentCatEngine.Install(options, projectId, agentCatOptions, resolved, "options");
        return options;
    }

// The overloads differ in their first parameter type, so a call never binds to the wrong one.
#pragma warning disable RS0026
    /// <inheritdoc cref="IAgentCat.PublishCustomEventAsync(string, string, CustomEventData?)"/>
    /// <remarks>
    /// <para>
    /// This method delivers through the process-wide AgentCat queue, or records to the current
    /// <see cref="AgentCatFake"/> when there is one. A literal <see langword="null"/> as the first
    /// argument is ambiguous with the <see cref="McpServer"/> overload: cast it.
    /// </para>
    /// <para>
    /// While AgentCat is off, the method checks its arguments, then publishes nothing and writes nothing. The
    /// <c>AGENTCAT_ENABLED</c> environment variable turns AgentCat on (<c>true</c>, <c>1</c>, <c>yes</c> or <c>on</c>) or off
    /// (<c>false</c>, <c>0</c>, <c>no</c> or <c>off</c>). Without it, AgentCat is on, except in a test run (a process that
    /// loads xUnit, NUnit, MSTest or TUnit), where it is off unless an <see cref="AgentCatFake"/> is current.
    /// </para>
    /// </remarks>
    public static Task PublishCustomEventAsync(string sessionId, string projectId, CustomEventData? data = null)
        => Client.PublishCustomEventAsync(sessionId, projectId, data);

    /// <inheritdoc cref="IAgentCat.PublishCustomEventAsync(McpServer, string, CustomEventData?)"/>
    /// <remarks>
    /// <para>
    /// The event goes to the queue of the tracker that is installed on <paramref name="server"/>. A literal
    /// <see langword="null"/> as the first argument is ambiguous with the string overload: cast it.
    /// </para>
    /// <para>
    /// While AgentCat is off, the method checks its arguments, then publishes nothing and writes nothing. The
    /// <c>AGENTCAT_ENABLED</c> environment variable turns AgentCat on (<c>true</c>, <c>1</c>, <c>yes</c> or <c>on</c>) or off
    /// (<c>false</c>, <c>0</c>, <c>no</c> or <c>off</c>). Without it, AgentCat is on, except in a test run (a process that
    /// loads xUnit, NUnit, MSTest or TUnit), where it is off unless an <see cref="AgentCatFake"/> is current.
    /// </para>
    /// </remarks>
    public static Task PublishCustomEventAsync(McpServer server, string projectId, CustomEventData? data = null)
        => Client.PublishCustomEventAsync(server, projectId, data);

#pragma warning restore RS0026

    /// <inheritdoc cref="IAgentCat.FlushAsync(TimeSpan?, CancellationToken)"/>
    public static Task<bool> FlushAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Client.FlushAsync(timeout, cancellationToken);

    /// <inheritdoc cref="IAgentCat.ShutdownAsync(CancellationToken)"/>
    public static Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
        => Client.ShutdownAsync(cancellationToken);

    /// <summary>Creates a fake that records AgentCat events in memory for your tests, and makes it current.</summary>
    /// <returns>The new fake. Dispose it at the end of the test.</returns>
    /// <remarks>
    /// Call this method before you build the host or call <see cref="Track(McpServerOptions?, string?, AgentCatOptions?)"/>.
    /// While the fake is current, every server that AgentCat tracks records into it. A stdio or stateful HTTP server
    /// whose options were built while AgentCat was off stays untracked, and when no tool call was recorded, the failure
    /// messages of the fake's tracked-call assertions say so; a stateless HTTP server is tracked from its next request.
    /// A new fake becomes current and starts empty. The fake is process-wide, so tests that use it must
    /// not run in parallel. The <c>AGENTCAT_ENABLED</c> environment variable does not affect it. See
    /// <see cref="AgentCatFake"/>.
    /// </remarks>
    public static AgentCatFake Fake()
    {
        var fake = new AgentCatFake();
        AgentCatRuntime.MakeCurrent(fake.Runtime);
        return fake;
    }
}
