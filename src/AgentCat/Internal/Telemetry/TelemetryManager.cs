using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;

namespace AgentCat.Internal.Telemetry;

// TS TelemetryManager: the one exporter of the pipeline's slot, which hands each event to every configured exporter in
// the order of AgentCatOptions.Exporters. A failure of one exporter never stops the others or the AgentCat delivery.
internal sealed class TelemetryManager : IEventExporter
{
    private readonly KeyValuePair<string, ITelemetryExporter>[] _exporters;
    private readonly IAgentCatLog _log;

    internal TelemetryManager(IReadOnlyList<KeyValuePair<string, ITelemetryExporter>> exporters, IAgentCatLog log)
    {
        _exporters = new KeyValuePair<string, ITelemetryExporter>[exporters.Count];
        for (var i = 0; i < _exporters.Length; i++)
        {
            _exporters[i] = exporters[i];
        }

        _log = log;
    }

    internal int Count => _exporters.Length;

    // TS track(): the first install that configures exporters on a runtime that hosts them builds its manager; the
    // exporters of every later install are ignored without a line. An empty set configures nothing.
    internal static void Install(AgentCatRuntime runtime, IReadOnlyList<KeyValuePair<string, AgentCatExporter?>> configs)
    {
        if (configs.Count == 0 || !runtime.TryClaimExporters())
        {
            return;
        }

        var built = new List<KeyValuePair<string, ITelemetryExporter>>(configs.Count);
        foreach (var config in configs)
        {
            if (ExporterFactory.Create(config.Key, config.Value, runtime) is { } exporter)
            {
                built.Add(new KeyValuePair<string, ITelemetryExporter>(config.Key, exporter));
            }
        }

        runtime.Pipeline.TrySetExporter(new TelemetryManager(built, runtime.Log));
        runtime.Log.Write("Initialized telemetry with " + configs.Count.ToString(CultureInfo.InvariantCulture) + " exporters");
    }

    // TS: `Telemetry export failed for ${name}: ${error.message}`.
    internal static string ExportFailedLine(string name, string message) => "Telemetry export failed for " + name + ": " + message;

    // Never throws and never faults: the pipeline's "Telemetry export error" line stays unreachable, as in TS.
    public Task ExportAsync(AgentCatEvent e, CancellationToken cancellationToken)
    {
        foreach (var exporter in _exporters)
        {
            try
            {
                exporter.Value.Export(e);
            }
            catch (Exception ex)
            {
                LogFailure(exporter.Key, ex);
            }
        }

        return Task.CompletedTask;
    }

    private void LogFailure(string name, Exception error)
    {
        string message;
        try
        {
            message = error.Message;
        }
        catch (Exception)
        {
            message = "";
        }

        try
        {
            _log.Write(ExportFailedLine(name, message));
        }
        catch (Exception)
        {
        }
    }
}
