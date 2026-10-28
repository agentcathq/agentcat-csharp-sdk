namespace AgentCat.Internal.Telemetry;

// One configured exporter. Export runs on the pipeline reader before each AgentCat POST attempt: it builds every
// request body from the event before it returns, then hands the requests to the runtime's export channel. It never
// keeps the event or its JSON trees. The pipeline runs several readers at once (five by default), so Export runs
// concurrently for different events: an exporter must be thread-safe.
internal interface ITelemetryExporter
{
    void Export(AgentCatEvent e);
}
