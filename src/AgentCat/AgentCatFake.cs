using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Text;

namespace AgentCat;

/// <summary>
/// Records the events of AgentCat in memory, so that your tests can assert what your MCP server tracks.
/// <see cref="AgentCatSdk.Fake"/> creates one.
/// </summary>
/// <remarks>
/// <para>
/// AgentCat is off in a test run: <c>WithAgentCat</c> and <c>Track</c> install nothing. A fake turns AgentCat on. While
/// the fake is current, every server that AgentCat tracks records its events into the fake and sends nothing. Create
/// the fake before you build the host or call <c>Track</c>.
/// </para>
/// <para>
/// A <c>WithAgentCat</c> registration decides at the first server options that are built while AgentCat is on. A
/// stateless HTTP server builds new server options for every request, so a stateless server that you set up before the
/// fake is tracked from its next request. A stdio or stateful HTTP server whose options were built while AgentCat was
/// off stays untracked. When no tool call was recorded, the failure messages of <see cref="AssertTrackedAsync"/> and
/// <see cref="AssertTrackedCountAsync"/> say so.
/// </para>
/// <para>
/// The fake runs every stage of a real event: the hooks, <c>RedactEvent</c>, <c>RedactSensitiveInformation</c>,
/// sanitization, truncation and the event ID. Then it records the event and does not send it. Events of servers
/// without a project ID are recorded too. Nothing goes to the network, diagnostics are off, and the AgentCat log lines
/// go to <see cref="GetLogsAsync"/>, not to the AgentCat log file or to the host logger.
/// </para>
/// <para>
/// The fake is process-wide. While it is current, every <c>Track</c> call, every <c>WithAgentCat</c> registration that
/// decides, every tracked server and every custom event uses it. Tests that use a fake must not run in parallel with
/// each other or with other tests that use AgentCat.
/// </para>
/// <para>The <c>AGENTCAT_ENABLED</c> environment variable does not affect the fake.</para>
/// <para>
/// <see cref="Dispose"/> takes the fake out: the newest fake that is not disposed becomes current, or no fake. After
/// that, the servers that you set up under this fake record into the current fake, or AgentCat drops their events. It
/// never sends them. The methods of a disposed fake still work and return what it recorded.
/// </para>
/// <para>
/// While no fake is current, a stateless HTTP server that you set up under a fake serves its requests unchanged: no
/// <c>get_more_tools</c> tool, no <c>context</c> parameters and no session text, until a new fake is current. A stdio or
/// stateful HTTP server builds its server options once, so it keeps these AgentCat changes for its lifetime, also after
/// the fake is disposed.
/// </para>
/// </remarks>
public sealed class AgentCatFake : IDisposable
{
    private const string NoMatchingToolCall = "Failed asserting that a tool call matching the filter was tracked.";

    private const string NoMatchingCustomEvent = "Failed asserting that a custom event matching the filter was published.";

    private const string OffInstallHint = " A server was set up while AgentCat was off: call AgentCatSdk.Fake() before WithAgentCat or Track.";

    private readonly List<AgentCatEvent> _events = new();
    private readonly FakeLog _log = new();

    internal AgentCatFake()
    {
        Runtime = new AgentCatRuntime(_log, Record);
    }

    internal AgentCatRuntime Runtime { get; }

    /// <summary>Returns every recorded event, in the order that AgentCat queued them.</summary>
    /// <param name="cancellationToken">
    /// Ends the wait for the events that AgentCat still processes. The wait has no time limit, so pass the token of your
    /// test framework.
    /// </param>
    /// <returns>The recorded event objects themselves, not copies: a change to one of them shows in later calls.</returns>
    /// <remarks>
    /// The call first waits until AgentCat has processed every queued event, so the results of hooks that are still
    /// running are in the events.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled before the wait ends.</exception>
    public async Task<IReadOnlyList<AgentCatEvent>> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        lock (_events)
        {
            return _events.ToArray();
        }
    }

    /// <summary>Returns the AgentCat log lines of this fake, in order, without the time and the prefix of the log file.</summary>
    /// <param name="cancellationToken">
    /// Ends the wait for the events that AgentCat still processes. The wait has no time limit, so pass the token of your
    /// test framework.
    /// </param>
    /// <returns>The log messages, for example setup lines, hook errors and warnings.</returns>
    /// <remarks>
    /// The call first waits until AgentCat has processed every queued event, so the errors of hooks that are still
    /// running are in the lines.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled before the wait ends.</exception>
    public async Task<IReadOnlyList<string>> GetLogsAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        return _log.Messages;
    }

    /// <summary>
    /// Asserts that at least one tool call (<c>mcp:tools/call</c>) was recorded, and that one matches
    /// <paramref name="filter"/> when you give it.
    /// </summary>
    /// <param name="filter">The condition that one tool call event must meet. <see langword="null"/> accepts any.</param>
    /// <param name="cancellationToken">
    /// Ends the wait for the events that AgentCat still processes. The wait has no time limit, so pass the token of your
    /// test framework.
    /// </param>
    /// <returns>A task that completes when the assertion passes.</returns>
    /// <remarks>Like <see cref="GetEventsAsync"/>, the call first waits until AgentCat has processed every queued event.</remarks>
    /// <exception cref="AgentCatAssertionException">No recorded tool call matches.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled before the wait ends.</exception>
    public async Task AssertTrackedAsync(Func<AgentCatEvent, bool>? filter = null, CancellationToken cancellationToken = default)
    {
        var toolCalls = OfType(await GetEventsAsync(cancellationToken).ConfigureAwait(false), WireNames.ToolCallEventType);
        if (!AnyMatches(toolCalls, filter))
        {
            throw new AgentCatAssertionException(Hinted(NoMatchingToolCall, toolCalls));
        }
    }

    /// <summary>Asserts the number of recorded tool calls (<c>mcp:tools/call</c>). Custom events do not count.</summary>
    /// <param name="count">The expected number of tool calls.</param>
    /// <param name="cancellationToken">
    /// Ends the wait for the events that AgentCat still processes. The wait has no time limit, so pass the token of your
    /// test framework.
    /// </param>
    /// <returns>A task that completes when the assertion passes.</returns>
    /// <remarks>Like <see cref="GetEventsAsync"/>, the call first waits until AgentCat has processed every queued event.</remarks>
    /// <exception cref="AgentCatAssertionException">The number of recorded tool calls is not <paramref name="count"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled before the wait ends.</exception>
    public async Task AssertTrackedCountAsync(int count, CancellationToken cancellationToken = default)
    {
        var toolCalls = OfType(await GetEventsAsync(cancellationToken).ConfigureAwait(false), WireNames.ToolCallEventType);
        if (toolCalls.Count != count)
        {
            throw new AgentCatAssertionException(Hinted(
                string.Format(CultureInfo.InvariantCulture, "Failed asserting that {0} tool call(s) were tracked: {1} were.", count, toolCalls.Count),
                toolCalls));
        }
    }

    /// <summary>Asserts that no event of any type was recorded.</summary>
    /// <param name="cancellationToken">
    /// Ends the wait for the events that AgentCat still processes. The wait has no time limit, so pass the token of your
    /// test framework.
    /// </param>
    /// <returns>A task that completes when the assertion passes.</returns>
    /// <remarks>Like <see cref="GetEventsAsync"/>, the call first waits until AgentCat has processed every queued event.</remarks>
    /// <exception cref="AgentCatAssertionException">At least one event was recorded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled before the wait ends.</exception>
    public async Task AssertNothingTrackedAsync(CancellationToken cancellationToken = default)
    {
        var count = (await GetEventsAsync(cancellationToken).ConfigureAwait(false)).Count;
        if (count != 0)
        {
            throw new AgentCatAssertionException(string.Format(
                CultureInfo.InvariantCulture,
                "Failed asserting that nothing was tracked: {0} event(s) were recorded.",
                count));
        }
    }

    /// <summary>
    /// Asserts that at least one custom event (<c>agentcat:custom</c>) was recorded, and that one matches
    /// <paramref name="filter"/> when you give it.
    /// </summary>
    /// <param name="filter">The condition that one custom event must meet. <see langword="null"/> accepts any.</param>
    /// <param name="cancellationToken">
    /// Ends the wait for the events that AgentCat still processes. The wait has no time limit, so pass the token of your
    /// test framework.
    /// </param>
    /// <returns>A task that completes when the assertion passes.</returns>
    /// <remarks>Like <see cref="GetEventsAsync"/>, the call first waits until AgentCat has processed every queued event.</remarks>
    /// <exception cref="AgentCatAssertionException">No recorded custom event matches.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled before the wait ends.</exception>
    public async Task AssertCustomEventPublishedAsync(Func<AgentCatEvent, bool>? filter = null, CancellationToken cancellationToken = default)
    {
        var customEvents = OfType(await GetEventsAsync(cancellationToken).ConfigureAwait(false), WireNames.CustomEventType);
        if (!AnyMatches(customEvents, filter))
        {
            throw new AgentCatAssertionException(NoMatchingCustomEvent);
        }
    }

    /// <summary>
    /// Takes the fake out: the newest fake that is not disposed becomes current, or no fake. A second call does nothing.
    /// </summary>
    /// <remarks>
    /// After this call, the servers that you set up under this fake record into the current fake, or AgentCat drops
    /// their events.
    /// </remarks>
    public void Dispose() => AgentCatRuntime.RemoveCurrent(Runtime);

    private static List<AgentCatEvent> OfType(IReadOnlyList<AgentCatEvent> events, string eventType)
    {
        var found = new List<AgentCatEvent>();
        foreach (var e in events)
        {
            if (e.EventType == eventType)
            {
                found.Add(e);
            }
        }

        return found;
    }

    private static bool AnyMatches(List<AgentCatEvent> events, Func<AgentCatEvent, bool>? filter)
    {
        foreach (var e in events)
        {
            if (filter is null || filter(e))
            {
                return true;
            }
        }

        return false;
    }

    // No tool call at all can mean that the server was set up while AgentCat was off, so it is not tracked.
    private static string Hinted(string message, List<AgentCatEvent> toolCalls)
        => toolCalls.Count == 0 && EnabledGate.SkippedAnInstall ? message + OffInstallHint : message;

    private void Record(AgentCatEvent e)
    {
        lock (_events)
        {
            _events.Add(e);
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Runtime.Pipeline.FlushAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class FakeLog : IAgentCatLog
    {
        private readonly List<string> _messages = new();

        internal IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public void Write(string message)
        {
            lock (_messages)
            {
                _messages.Add(message);
            }
        }

        public void Write(string message, LogSeverity hostSeverity) => Write(message);
    }
}
