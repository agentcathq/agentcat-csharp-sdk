using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Events;
using AgentCat.Internal.Handles;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Engine;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Hooks can still hold the token after the event is processed; the source has no timer, so it is left to the garbage collector.")]
internal sealed class PendingHooks
{
    private const string SessionLabel = "resolveSessionId";
    private const string IdentityLabel = "identify";
    private const string TagsLabel = "eventTags";
    private const string PropertiesLabel = "eventProperties";

    private readonly Task<string?>? _session;
    private readonly Task<UserIdentity?>? _identity;
    private readonly Task<IReadOnlyDictionary<string, string>?>? _tags;
    private readonly Task<JsonObject?>? _properties;
    private readonly CancellationTokenSource _abandoned = new();
    private readonly TimeSpan _budget;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IAgentCatLog _log;
    private readonly IPipelineClock _clock;
    private string? _mintedSession;

    private PendingHooks(TrackerSettings settings, AgentCatToolCall call, IAgentCatLog log, IPipelineClock clock)
    {
        _budget = settings.PendingHookBudget;
        _delay = settings.HookBudgetDelay;
        _log = log;
        _clock = clock;
        var abandoned = _abandoned.Token;
        var calls = new Stack<AgentCatToolCall>();
        calls.Push(call);
        for (var i = Count(settings); i > 1; i--)
        {
            calls.Push(call.WithOwnMeta());
        }

        if (settings.ResolveSessionId is { } resolveSessionId)
        {
            _session = InvokeAsync(resolveSessionId, calls.Pop(), "resolveSessionId hook error: ", log, abandoned);
        }

        if (settings.Identify is { } identify)
        {
            _identity = InvokeAsync(identify, calls.Pop(), "Error: identify callback threw - ", log, abandoned);
        }

        if (settings.EventTags is { } eventTags)
        {
            _tags = InvokeAsync(eventTags, calls.Pop(), "eventTags callback error: ", log, abandoned);
        }

        if (settings.EventProperties is { } eventProperties)
        {
            _properties = InvokeAsync(eventProperties, calls.Pop(), "eventProperties callback error: ", log, abandoned);
        }
    }

    internal static bool AnyConfigured(TrackerSettings settings) => Count(settings) > 0;

    internal bool ResolvesSession => _session is not null;

    internal static PendingHooks? Start(TrackerSettings settings, AgentCatToolCall call, IAgentCatLog log, IPipelineClock clock)
        => AnyConfigured(settings) ? new PendingHooks(settings, call, log, clock) : null;

    internal void Abandon()
    {
        try
        {
            _abandoned.Cancel();
        }
        catch (AggregateException)
        {
        }
    }

    internal async Task ApplyAsync(AgentCatEvent e, CancellationToken cancellationToken)
    {
        try
        {
            var session = SettleAsync(_session, SessionLabel, cancellationToken);
            var identity = SettleAsync(_identity, IdentityLabel, cancellationToken);
            var tags = SettleAsync(_tags, TagsLabel, cancellationToken);
            var properties = SettleAsync(_properties, PropertiesLabel, cancellationToken);
            var settled = Task.WhenAll(session, identity, tags, properties);
            await ResumeOffHookThreadAsync(settled).ConfigureAwait(false);
            await settled.ConfigureAwait(false);
            Apply(e, await session.ConfigureAwait(false), await identity.ConfigureAwait(false), await tags.ConfigureAwait(false), await properties.ConfigureAwait(false));
        }
        finally
        {
            try
            {
                _abandoned.Cancel();
            }
            catch (AggregateException)
            {
            }
        }
    }

    // The Prepare stage of a custom event that a tool publishes during this call in hook mode. It waits for the hook
    // within its own budget, as the call's event does, and gets only the session from the call.
    internal async ValueTask ApplySessionAsync(AgentCatEvent e, CancellationToken cancellationToken)
    {
        var hookValue = SettleAsync(_session, SessionLabel, cancellationToken);
        await ResumeOffHookThreadAsync(hookValue).ConfigureAwait(false);
        e.SessionId = SessionOf(await hookValue.ConfigureAwait(false), e).SessionId;
    }

    private static int Count(TrackerSettings settings)
        => (settings.ResolveSessionId is null ? 0 : 1)
            + (settings.Identify is null ? 0 : 1)
            + (settings.EventTags is null ? 0 : 1)
            + (settings.EventProperties is null ? 0 : 1);

    private static async Task<T?> InvokeAsync<T>(
        Func<AgentCatToolCall, CancellationToken, ValueTask<T?>> hook,
        AgentCatToolCall call,
        string errorPrefix,
        IAgentCatLog log,
        CancellationToken abandoned)
        where T : class
    {
        try
        {
            return await hook(call, abandoned).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!(ex is OperationCanceledException && abandoned.IsCancellationRequested))
            {
                log.Write(errorPrefix + ErrorText.Of(ex));
            }

            return null;
        }
    }

    // A hook can settle on a customer thread; the pipeline stages that follow must not run there.
    private static async Task ResumeOffHookThreadAsync(Task settled)
    {
        if (settled.IsCompleted)
        {
            return;
        }

        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ResumeAfterAsync(settled, resumed);
        await resumed.Task.ConfigureAwait(false);
    }

    private static async Task ResumeAfterAsync(Task settled, TaskCompletionSource<bool> resumed)
    {
        await Task.WhenAny(settled).ConfigureAwait(false);
        resumed.TrySetResult(true);
    }

    // The session that an event of this call gets from the hook value it saw. A non-empty value derives the same ID for
    // every event of the call. Without one (null, a failed hook, or an event whose own budget ran out), every event of
    // the call gets the one ID that the first of them minted.
    private (string SessionId, SessionSource Source) SessionOf(string? hookValue, AgentCatEvent e)
    {
        var (sessionId, source) = HandleResolver.FromHookValue(
            hookValue,
            string.IsNullOrEmpty(e.ProjectId) ? null : e.ProjectId,
            _clock.UtcNow);
        return source == SessionSource.Minted
            ? (Interlocked.CompareExchange(ref _mintedSession, sessionId, null) ?? sessionId, source)
            : (sessionId, source);
    }

    private void Apply(
        AgentCatEvent e,
        string? hookValue,
        UserIdentity? user,
        IReadOnlyDictionary<string, string>? customerTags,
        JsonObject? customProperties)
    {
        if (_session is not null)
        {
            var (sessionId, source) = SessionOf(hookValue, e);
            e.SessionId = sessionId;
            var sdkTags = e.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal);
            sdkTags[WireNames.TagSessionSource] = source.ToWire();
            e.Tags = sdkTags;
        }

        if (user is not null)
        {
            e.IdentifyActorGivenId = user.UserId;
            e.IdentifyActorName = user.UserName;
            e.IdentifyActorData = user.UserData ?? new JsonObject();
        }

        if (Validate(customerTags) is { } merged)
        {
            if (e.Tags is { } sdk)
            {
                foreach (var pair in sdk)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            e.Tags = merged;
        }

        if (customProperties is not null)
        {
            e.Properties = customProperties;
        }
    }

    private async Task<T?> SettleAsync<T>(Task<T?>? hook, string label, CancellationToken cancellationToken)
        where T : class
    {
        if (hook is null)
        {
            return null;
        }

        if (!hook.IsCompleted)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var budget = _delay(_budget, timer.Token);
            var first = await Task.WhenAny(hook, budget).ConfigureAwait(false);
            timer.Cancel();
            if (first != hook)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _log.Write(
                    "Warning: " + label + " hook did not settle within "
                    + ((long)_budget.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                    + "ms; publishing event without it");
                return null;
            }
        }

        try
        {
            return await hook.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write("Warning: " + label + " hook failed in background - " + ErrorText.Of(ex));
            return null;
        }
    }

    private Dictionary<string, string>? Validate(IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null)
        {
            return null;
        }

        try
        {
            return TagValidator.Validate(tags, _log);
        }
        catch (Exception ex)
        {
            _log.Write("eventTags callback error: " + ErrorText.Of(ex));
            return null;
        }
    }
}
