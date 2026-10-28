using System;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry.Datadog;
using AgentCat.Internal.Telemetry.Otlp;
using AgentCat.Internal.Telemetry.PostHog;
using AgentCat.Internal.Telemetry.Sentry;

namespace AgentCat.Internal.Telemetry;

// Builds the exporter of one configuration, or returns null for a configuration it builds nothing for. A throw is a
// configuration it cannot use (TS: the exporter's constructor threw).
internal delegate ITelemetryExporter? ExporterBuilder(string name, AgentCatExporter config, AgentCatRuntime runtime);

// TS TelemetryManager constructor and createExporter: the exporter of one named configuration, with the TS lines.
internal static class ExporterFactory
{
    // Through the runtime's builder; the real one for a runtime that hosts no exporters.
    internal static ITelemetryExporter? Create(string name, AgentCatExporter? config, AgentCatRuntime runtime)
        => Create(name, config, runtime, runtime.ExporterBuilder ?? Build);

    internal static ITelemetryExporter? Create(string name, AgentCatExporter? config, AgentCatRuntime runtime, ExporterBuilder build)
    {
        if (config is null)
        {
            runtime.Log.Write("Unknown exporter type: null", LogSeverity.Error);
            return null;
        }

        ITelemetryExporter? exporter;
        try
        {
            exporter = build(name, config.Snapshot(), runtime);
        }
        catch (Exception ex)
        {
            runtime.Log.Write("Failed to initialize exporter " + name + ": " + JsError.Text(ex));
            return null;
        }

        if (exporter is not null)
        {
            runtime.Log.WriteRoutine("Initialized telemetry exporter: " + name);
        }

        return exporter;
    }

    // The exporter of each configuration type; a type without one builds nothing and writes no line.
    internal static ITelemetryExporter? Build(string name, AgentCatExporter config, AgentCatRuntime runtime) => config switch
    {
        OtlpExporter otlp => new OtlpTelemetryExporter(name, otlp, runtime),
        DatadogExporter datadog => new DatadogTelemetryExporter(datadog, runtime),
        SentryExporter sentry => new SentryTelemetryExporter(sentry, runtime),
        PostHogExporter posthog => new PostHogTelemetryExporter(posthog, runtime),
        _ => null,
    };
}
