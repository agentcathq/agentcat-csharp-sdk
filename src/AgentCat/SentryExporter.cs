using System;

namespace AgentCat;

/// <summary>Sends each event to Sentry as a log entry, and each failed event as a Sentry error as well.</summary>
/// <remarks>
/// <para>
/// AgentCat reads the project and the key from <see cref="Dsn"/>. A DSN that it cannot read writes
/// <c>Failed to initialize exporter {name}: Error: Invalid Sentry DSN: …</c> to the AgentCat log, with the key part of the
/// DSN, between <c>://</c> and <c>@</c>, replaced by <c>***</c> (<c>https://***@o1.ingest.sentry.io/42</c>), and AgentCat
/// skips this exporter only.
/// </para>
/// </remarks>
public sealed class SentryExporter : AgentCatExporter
{
    /// <summary>Creates the configuration of a Sentry exporter.</summary>
    /// <param name="dsn">The DSN of the Sentry project, for example <c>https://key@o1.ingest.sentry.io/42</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dsn"/> is <see langword="null"/>.</exception>
    public SentryExporter(string dsn)
    {
        Dsn = dsn ?? throw new ArgumentNullException(nameof(dsn));
    }

    /// <summary>The DSN of the Sentry project, for example <c>https://key@o1.ingest.sentry.io/42</c>.</summary>
    public string Dsn { get; }

    /// <summary>The Sentry environment of the events, for example <c>production</c>. <see langword="null"/> sends none.</summary>
    public string? Environment { get; init; }

    /// <summary>The release of the events. <see langword="null"/> sends none.</summary>
    public string? Release { get; init; }

    /// <summary>Also sends each event as a Sentry transaction (performance tracing). Default <see langword="false"/>.</summary>
    public bool EnableTracing { get; init; }
}
