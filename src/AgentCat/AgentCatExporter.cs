namespace AgentCat;

/// <summary>
/// The configuration of a telemetry exporter, which sends each AgentCat event to another observability tool as well.
/// Add instances to <see cref="AgentCatOptions.Exporters"/>.
/// </summary>
/// <remarks>
/// <para>
/// The exporters are <see cref="OtlpExporter"/>, <see cref="DatadogExporter"/>, <see cref="SentryExporter"/> and
/// <see cref="PostHogExporter"/>. You cannot derive your own.
/// </para>
/// <para>
/// A configuration checks only that its required values are not <see langword="null"/>. When AgentCat sets the
/// exporters up, it checks one more value, the Sentry DSN: an invalid one writes
/// <c>Failed to initialize exporter {name}: Error: Invalid Sentry DSN: …</c> to the AgentCat log, with the key part of the
/// DSN, between <c>://</c> and <c>@</c>, replaced by <c>***</c> (<c>https://***@o1.ingest.sentry.io/42</c>), and AgentCat
/// skips that exporter only.
/// </para>
/// <para>
/// AgentCat uses every other value as given. A wrong endpoint, site, host or API key does not stop the set-up: it shows
/// up as a failure line in the AgentCat log for each export.
/// </para>
/// </remarks>
public abstract class AgentCatExporter
{
    internal AgentCatExporter()
    {
    }

    // The configuration that the install keeps: later changes to the customer's objects have no effect.
    internal virtual AgentCatExporter Snapshot() => this;
}
