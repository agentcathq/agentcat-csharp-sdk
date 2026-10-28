using System;

namespace AgentCat;

/// <summary>Sends each event to PostHog as a product analytics event, and each failed event as a PostHog exception as well.</summary>
/// <remarks>
/// <para>
/// AgentCat posts the events to <c>{Host}/batch</c>, with one trailing <c>/</c> of <see cref="Host"/> removed.
/// </para>
/// <para>
/// PostHog receives the HTTP request headers of a tool call, when AgentCat captured them, in the event's
/// <c>parameters</c> (at <c>extra.requestInfo.headers</c>) and in <c>$ai_input_state</c>. Remove credentials such as
/// <c>authorization</c> and <c>cookie</c> with <see cref="AgentCatOptions.RedactEvent"/> first.
/// </para>
/// </remarks>
public sealed class PostHogExporter : AgentCatExporter
{
    /// <summary>Creates the configuration of a PostHog exporter.</summary>
    /// <param name="apiKey">The PostHog project API key, for example <c>phc_…</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="apiKey"/> is <see langword="null"/>.</exception>
    public PostHogExporter(string apiKey)
    {
        ApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    /// <summary>The PostHog project API key, for example <c>phc_…</c>.</summary>
    public string ApiKey { get; }

    /// <summary>
    /// The PostHog host, for example <c>https://eu.i.posthog.com</c> or the URL of a self-hosted instance.
    /// <see langword="null"/> or an empty string uses <c>https://us.i.posthog.com</c>.
    /// </summary>
    public string? Host { get; init; }

    /// <summary>Also sends each tool call as a PostHog LLM analytics span (<c>$ai_span</c>). Default <see langword="false"/>.</summary>
    public bool EnableAITracing { get; init; }
}
