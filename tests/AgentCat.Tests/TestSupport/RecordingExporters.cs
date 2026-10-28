using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry;

namespace AgentCat.Tests.TestSupport;

// A test exporter builder: every config builds a recording exporter under its name, and nothing is sent. Give Build to
// a runtime as its exporter builder to make the runtime host exporters.
internal sealed class RecordingExporters
{
    private readonly object _gate = new();
    private readonly List<BuiltExporter> _built = new();
    private readonly List<RecordedExport> _exports = new();

    // Runs first in each build, as the constructor of a real exporter does: it can write a line or throw.
    internal Action<string, AgentCatExporter, AgentCatRuntime>? OnBuild { get; set; }

    // Thrown by Export of the exporter with that name, after the export is recorded.
    internal Func<string, Exception?>? ThrowOnExport { get; set; }

    internal IReadOnlyList<BuiltExporter> Built
    {
        get
        {
            lock (_gate)
            {
                return _built.ToArray();
            }
        }
    }

    internal IReadOnlyList<RecordedExport> Exports
    {
        get
        {
            lock (_gate)
            {
                return _exports.ToArray();
            }
        }
    }

    internal ITelemetryExporter? Build(string name, AgentCatExporter config, AgentCatRuntime runtime)
    {
        OnBuild?.Invoke(name, config, runtime);
        lock (_gate)
        {
            _built.Add(new BuiltExporter(name, config, runtime));
        }

        return new Exporter(this, name);
    }

    internal IReadOnlyList<string> ExportsTo(string name) => Exports.Where(x => x.Name == name).Select(x => x.Event.Id).ToArray();

    private void Record(string name, AgentCatEvent e)
    {
        lock (_gate)
        {
            _exports.Add(new RecordedExport(name, e));
        }

        if (ThrowOnExport?.Invoke(name) is { } error)
        {
            throw error;
        }
    }

    internal sealed record BuiltExporter(string Name, AgentCatExporter Config, AgentCatRuntime Runtime);

    internal sealed record RecordedExport(string Name, AgentCatEvent Event);

    private sealed class Exporter(RecordingExporters owner, string name) : ITelemetryExporter
    {
        public void Export(AgentCatEvent e) => owner.Record(name, e);
    }
}
