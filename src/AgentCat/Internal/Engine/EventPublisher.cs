using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Runtime;

namespace AgentCat.Internal.Engine;

internal static class EventPublisher
{
    internal static void Publish(
        AgentCatTracker tracker,
        AgentCatEvent e,
        Func<AgentCatEvent, CancellationToken, ValueTask>? prepare,
        bool redactStrings,
        Action? discard = null)
        => Publish(AgentCatRuntime.EffectiveFor(tracker.Runtime), tracker, e, prepare, redactStrings, discard);

    // A null runtime drops the event: its tracker belongs to a fake that is no longer current.
    internal static void Publish(
        AgentCatRuntime? runtime,
        AgentCatTracker tracker,
        AgentCatEvent e,
        Func<AgentCatEvent, CancellationToken, ValueTask>? prepare,
        bool redactStrings,
        Action? discard = null)
    {
        var settings = tracker.Settings;
        if (!settings.EnableTracing)
        {
            return;
        }

        if (runtime is null)
        {
            try
            {
                discard?.Invoke();
            }
            catch (Exception)
            {
            }

            return;
        }

        var clock = tracker.Runtime.Clock;
        if (e.Duration is null && e.Timestamp != default)
        {
            e.Duration = (int)(clock.UtcNow - e.Timestamp).TotalMilliseconds;
        }

        if (e.Timestamp == default)
        {
            e.Timestamp = clock.UtcNow;
        }

        e.ProjectId = tracker.ProjectId;
        e.SdkLanguage = SdkInfo.Language;
        e.AgentCatVersion = SdkInfo.Version;
        e.IdentifyActorData ??= new JsonObject();

        runtime.Pipeline.Add(new QueuedEvent(e)
        {
            Prepare = prepare,
            Discard = discard,
            RedactEvent = settings.RedactEvent,
            RedactStrings = redactStrings ? settings.RedactSensitiveInformation : null,
        });
    }
}
