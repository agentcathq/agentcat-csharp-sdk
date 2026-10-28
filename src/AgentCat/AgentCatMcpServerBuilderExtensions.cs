using System;
using AgentCat;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Http;
using AgentCat.Internal.Lifecycle;
using AgentCat.Internal.Runtime;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds AgentCat to an MCP server that dependency injection builds.</summary>
public static class AgentCatMcpServerBuilderExtensions
{
    /// <summary>Adds AgentCat tracking to the MCP server that <paramref name="builder"/> configures.</summary>
    /// <param name="builder">The builder that <c>AddMcpServer</c> returns.</param>
    /// <param name="projectId">
    /// The AgentCat project ID. <see langword="null"/> reads the <c>AGENTCAT_PROJECT_ID</c> environment variable.
    /// An empty string, or no project ID at all, tracks the server but sends no events to AgentCat.
    /// </param>
    /// <param name="configure">
    /// Sets the tracking options. <see langword="null"/> uses the defaults. AgentCat calls it once, when the first
    /// server options are built.
    /// </param>
    /// <returns>The same <paramref name="builder"/>.</returns>
    /// <remarks>
    /// <para>
    /// Use this method for servers that dependency injection builds: stdio, stateful HTTP and stateless HTTP. It also
    /// registers <see cref="IAgentCat"/> as a singleton, unless a registration for it already exists.
    /// </para>
    /// <para>
    /// AgentCat installs after every <c>Configure</c> step of the server options, wherever <c>WithAgentCat</c> sits in
    /// the builder chain, so it sees the tools and handlers that later calls such as <c>WithTools</c> add. Stateless
    /// HTTP builds new server options for every request. All of them share one tracker per <c>WithAgentCat</c> call,
    /// and AgentCat writes its setup lines once per call, not once per request.
    /// </para>
    /// <para>
    /// AgentCat reads the options once, when the first server options are built. Later changes to that object have no
    /// effect.
    /// </para>
    /// <para>
    /// A second <c>WithAgentCat</c> call on the same server installs nothing, and the first call's project ID and
    /// options stay. The second call still applies its API base URL (<see cref="AgentCatOptions.ApiBaseUrl"/>, else
    /// the <c>AGENTCAT_API_URL</c> environment variable, else <c>MCPCAT_API_URL</c>), because all servers in the
    /// process share one event queue.
    /// </para>
    /// <para>
    /// AgentCat adds its <c>tools/call</c> filters at both ends of the filter list, after your <c>Configure</c> steps.
    /// A filter that you insert at index 0 later, in <c>ConfigureSessionOptions</c> or in a <c>PostConfigure</c> step
    /// that you register after this call, runs outside AgentCat. When such a filter turns a tool exception into its own
    /// result, that result still gets the session text, but the event records the error result that the MCP SDK builds
    /// from the exception, which the client does not see.
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
    /// <c>parameters.extra.requestInfo</c>, and into <see cref="AgentCatToolCall.Headers"/>. It reads them through
    /// <c>IHttpContextAccessor</c> without a reference to ASP.NET Core, and keeps no request object. The headers go
    /// out as received, including <c>authorization</c> and <c>cookie</c>, so redact them if they must not leave your
    /// process. This method registers <c>HttpContextAccessor</c> when the app has none, also while AgentCat is off.
    /// AgentCat has no request to read on stdio servers, on .NET Framework and <c>netstandard2.0</c> hosts, and with
    /// <c>PerSessionExecutionContext = true</c>. It also skips the read for a tool call whose client opts in to MCP 2.x
    /// tasks, because the MCP Tasks extension can run that call after the request has ended. The event then has no
    /// <c>requestInfo</c>, and <see cref="AgentCatToolCall.Headers"/> is empty. Under the
    /// legacy SSE transport the request is the session's <c>GET /sse</c> request, not the <c>POST</c> that carried the
    /// call. The URL keeps its query string, which can carry an API key or other credentials. Remove credentials with
    /// <see cref="AgentCatOptions.RedactEvent"/>.
    /// </para>
    /// <para>
    /// While an <see cref="AgentCatFake"/> is current, the server records its events into that fake and sends nothing.
    /// A registration that decided while a fake was current never sends events: when no fake is current, AgentCat drops
    /// them. The registration decides at the first server options that are built while AgentCat is on, so in a test run
    /// a stateless HTTP server that you set up before the fake is tracked from its next request. See
    /// <see cref="AgentCatSdk.Fake"/>.
    /// </para>
    /// <para>
    /// While AgentCat is off, the registration installs nothing and writes nothing, not even the AgentCat log file. The
    /// one exception is ASP.NET Core's <c>HttpContextAccessor</c>: when the app runs ASP.NET Core and has no
    /// <c>IHttpContextAccessor</c>, this method registers one at once, because AgentCat decides whether it is on only
    /// after the service provider is built. It costs about 70 ns on every HTTP request of the app. The
    /// <c>AGENTCAT_ENABLED</c> environment variable turns AgentCat on (<c>true</c>, <c>1</c>, <c>yes</c> or <c>on</c>) or off
    /// (<c>false</c>, <c>0</c>, <c>no</c> or <c>off</c>). Without it, AgentCat is on, except in a test run (a process that
    /// loads xUnit, NUnit, MSTest or TUnit), where it is off unless an <see cref="AgentCatFake"/> is current.
    /// </para>
    /// <para>
    /// When the host stops, AgentCat sends the queued events for at most 5 seconds, then its last diagnostics batch. It
    /// does this after every hosted service has stopped, the MCP stdio session and Kestrel included, so the events of
    /// tool calls that end during the stop are sent too. It ignores the host's shutdown token, so a stop can take up to
    /// 6 seconds longer than <c>HostOptions.ShutdownTimeout</c> (5 for the events, then at most 1 for the diagnostics
    /// batch). This flush does not close the queue: another host or server in the process keeps sending. Only a host
    /// runs it. If you build and dispose a service provider yourself, call <see cref="IAgentCat.FlushAsync"/> or
    /// <see cref="IAgentCat.ShutdownAsync"/> before you dispose it. A stdio client that kills the server process, as the
    /// official C# client does, leaves no time for any flush.
    /// </para>
    /// <para>
    /// The host owns SIGTERM and SIGINT, so this method adds no signal handler. The first install in the process, while
    /// AgentCat is on, also adds a process-exit handler (<c>AppDomain.ProcessExit</c>) that closes the queue and sends
    /// the queued events, unless <see cref="AgentCatOptions.DisableShutdownHandlers"/> is set on that install. The flush
    /// after the host stops stays on in every case.
    /// </para>
    /// <para>
    /// The flush after the host stops and the process-exit handler share one stop budget: about 6 seconds for the whole
    /// stop (5 for the events, then at most 1 for the diagnostics batch), however many of them run. The first one to
    /// start sets the 5-second deadline for the events, and a later one waits only for the time that is left. A stop
    /// that sent every queued event ends the budget, so a process that keeps running gets a new one at its next stop.
    /// </para>
    /// <para>
    /// This method throws only when <paramref name="builder"/> is <see langword="null"/>. When AgentCat cannot track
    /// the server, it logs a warning and the server runs untracked. The warning is
    /// <c>Warning: Failed to track server - {error}</c>, or, when <c>Handlers.CallToolWithAlternateHandler</c> is set
    /// (ModelContextProtocol 2.x), <c>Warning: AgentCat did not instrument this server: …</c>, because the MCP SDK
    /// rejects <c>tools/call</c> filters with that handler.
    /// </para>
    /// <para>
    /// If you set <c>Handlers.CallToolWithAlternateHandler</c>, set it in a <c>Configure</c> step, not in
    /// <c>ConfigureSessionOptions</c>. <c>ConfigureSessionOptions</c> runs after AgentCat has added its filters, and the
    /// MCP SDK then rejects the session.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static IMcpServerBuilder WithAgentCat(this IMcpServerBuilder builder, string? projectId = null, Action<AgentCatOptions>? configure = null)
        => WithAgentCat(builder, projectId, configure, static () => AgentCatRuntime.Current);

    // The fallback runtime serves when dependency injection has none, and takes the registration failure line.
    internal static IMcpServerBuilder WithAgentCat(
        IMcpServerBuilder builder,
        string? projectId,
        Action<AgentCatOptions>? configure,
        Func<AgentCatRuntime> fallback)
    {
        _ = builder ?? throw new ArgumentNullException(nameof(builder));
        try
        {
            var services = builder.Services;
            services.AddSingleton<IPostConfigureOptions<McpServerOptions>>(
                sp => new AgentCatPostConfigure(projectId, configure, RuntimeOf(sp, fallback), fallback, LoggerFactoryOf(sp)));
            services.TryAddSingleton<IAgentCat>(sp => new AgentCatClient(RuntimeOf(sp, fallback)));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AgentCatLifecycle>());
            AspNetCoreRequest.TryRegisterAccessor(services);
        }
        catch (Exception ex)
        {
            AgentCatPostConfigure.WriteFailure(fallback, ex);
        }

        return builder;
    }

    // The registration keeps the logger factory, never the provider: the provider reaches the cached server options.
    private static ILoggerFactory? LoggerFactoryOf(IServiceProvider services)
    {
        try
        {
            return services.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static Func<AgentCatRuntime> RuntimeOf(IServiceProvider services, Func<AgentCatRuntime> fallback)
    {
        try
        {
            if (services.GetService(typeof(AgentCatRuntime)) is AgentCatRuntime injected)
            {
                return () => injected;
            }
        }
        catch (Exception)
        {
        }

        return fallback;
    }
}
