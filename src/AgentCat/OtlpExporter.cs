using System;
using System.Collections.Generic;
using AgentCat.Internal.Collections;

namespace AgentCat;

/// <summary>Sends each event as an OpenTelemetry span to an OTLP/HTTP traces endpoint (JSON encoding).</summary>
/// <remarks>
/// <para>
/// AgentCat removes the trailing <c>/</c> characters of <see cref="Endpoint"/> and posts the span to
/// <c>{Endpoint}/v1/traces</c>, or to <see cref="Endpoint"/> itself when it already ends in <c>/v1/traces</c>. Any
/// OpenTelemetry Collector, and any vendor that accepts OTLP over HTTP with JSON, can receive it.
/// </para>
/// </remarks>
public sealed class OtlpExporter : AgentCatExporter
{
    /// <summary>Creates the configuration of an OTLP exporter.</summary>
    /// <param name="endpoint">The base URL of the OTLP/HTTP receiver, for example <c>http://localhost:4318</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    public OtlpExporter(string endpoint)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    /// <summary>The base URL of the OTLP/HTTP receiver, for example <c>http://localhost:4318</c>.</summary>
    public string Endpoint { get; }

    /// <summary>
    /// Headers to add to every request, for example an API key. A header with the name <c>Content-Type</c> or
    /// <c>User-Agent</c> replaces AgentCat's value. <see langword="null"/> adds none.
    /// </summary>
    /// <remarks>AgentCat copies the headers when it sets the exporter up. Later changes to the dictionary have no effect.</remarks>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    internal override AgentCatExporter Snapshot()
    {
        if (Headers is not { } headers)
        {
            return this;
        }

        var copy = new InsertionOrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            copy[header.Key] = header.Value;
        }

        return new OtlpExporter(Endpoint) { Headers = copy };
    }
}
