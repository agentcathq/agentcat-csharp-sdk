using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.E2E;

// A local HTTP host in place of the collectors of all four exporters: a FakeAgentCatApi that records every request,
// read here by each exporter's path. OTLP, Sentry and PostHog take its URL. The Datadog URLs are https on the Datadog
// site, so an in-process lane sends its exporter requests through Handler, which routes that site here with the Host
// header the exporter addressed. Set DefaultStatus, AnswerDelay or AnswerGate on Server before the first request.
internal sealed class FakeExporterHost : IDisposable
{
    private readonly RoutingHandler _handler;

    internal FakeExporterHost()
    {
        _handler = new RoutingHandler(new Uri(Server.BaseUrl));
    }

    internal FakeAgentCatApi Server { get; } = new();

    internal string BaseUrl => Server.BaseUrl;

    // The exporter handler of an in-process lane.
    internal HttpMessageHandler Handler => _handler;

    // Every request, in arrival order.
    internal IReadOnlyList<RecordedRequest> Requests => Server.Requests;

    internal IReadOnlyList<RecordedRequest> Otlp => On(E2EExporters.OtlpPath);

    internal IReadOnlyList<RecordedRequest> DatadogLogs => On(E2EExporters.DatadogLogsPath);

    internal IReadOnlyList<RecordedRequest> DatadogMetrics => On(E2EExporters.DatadogMetricsPath);

    internal IReadOnlyList<RecordedRequest> Sentry => On(E2EExporters.SentryPath);

    internal IReadOnlyList<RecordedRequest> PostHog => On(E2EExporters.PostHogPath);

    internal static string Text(RecordedRequest request) => Encoding.UTF8.GetString(request.Body);

    internal static JsonNode Json(RecordedRequest request) => JsonNode.Parse(request.Body)!;

    // The three lines of a Sentry envelope: its header, the item header and the item.
    internal static JsonNode[] Envelope(RecordedRequest request)
    {
        var lines = Text(request).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        return lines.Select(l => JsonNode.Parse(l)!).ToArray();
    }

    // A lane whose runtime hosts the real exporters and sends their requests through Handler, with every exporter of
    // E2EExporters at this host, Datadog and tracing included. configure runs after, on the same options.
    internal LaneSetup Lane(string? projectId = null, Action<AgentCatOptions>? configure = null, bool testRun = false)
    {
        // The server's callbacks capture locals only, never this host.
        var url = BaseUrl;
        return new LaneSetup
        {
            ProjectId = projectId,
            AgentCat = o =>
            {
                E2EExporters.Add(o, url, withDatadog: true, tracing: true);
                configure?.Invoke(o);
            },
            ExportHandler = Handler,
            TestRun = testRun,
        };
    }

    // Ends every request the host still holds: its connections close.
    public void Dispose()
    {
        Server.Dispose();
        _handler.Dispose();
    }

    private RecordedRequest[] On(string path) => Requests.Where(r => r.Path == path).ToArray();

    // Sends a request for the Datadog site to the host and keeps the Host header the exporter addressed. Every request
    // must end on the loopback host: anything else fails here, so a test can never reach a real collector.
    private sealed class RoutingHandler(Uri host) : DelegatingHandler(new SocketsHttpHandler { ActivityHeadersPropagator = null })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host.EndsWith("." + E2EExporters.DatadogSite, StringComparison.Ordinal))
            {
                request.Headers.Host = uri.Authority;
                request.RequestUri = new Uri(host, uri.PathAndQuery);
            }

            if (!request.RequestUri!.IsLoopback)
            {
                throw new InvalidOperationException("An end-to-end exporter request left the machine: " + uri);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
