using System;

namespace AgentCat;

/// <summary>Sends each event to Datadog as a log entry and as metrics.</summary>
/// <remarks>
/// <para>
/// AgentCat posts the log entry to <c>https://http-intake.logs.{Site}/api/v2/logs</c> and the metrics to
/// <c>https://api.{Site}/api/v1/series</c>. A leading <c>https://</c> or <c>http://</c> and a trailing <c>/</c> of
/// <see cref="Site"/> are removed first.
/// </para>
/// </remarks>
public sealed class DatadogExporter : AgentCatExporter
{
    /// <summary>Creates the configuration of a Datadog exporter.</summary>
    /// <param name="apiKey">The Datadog API key.</param>
    /// <param name="site">The Datadog site, for example <c>datadoghq.com</c> or <c>datadoghq.eu</c>.</param>
    /// <param name="service">The service name of the events, usually the name of the MCP server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="apiKey"/>, <paramref name="site"/> or <paramref name="service"/> is <see langword="null"/>.</exception>
    public DatadogExporter(string apiKey, string site, string service)
    {
        ApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        Site = site ?? throw new ArgumentNullException(nameof(site));
        Service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>The Datadog API key.</summary>
    public string ApiKey { get; }

    /// <summary>The Datadog site, for example <c>datadoghq.com</c> or <c>datadoghq.eu</c>.</summary>
    public string Site { get; }

    /// <summary>The service name of the events, usually the name of the MCP server.</summary>
    public string Service { get; }

    /// <summary>The environment of the events, for example <c>production</c>. <see langword="null"/> sends none.</summary>
    public string? Env { get; init; }
}
