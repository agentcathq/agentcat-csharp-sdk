namespace AgentCat.Tests.E2E;

// The exporters of the end-to-end tests, all pointed at one local fake host, so that no request leaves the machine.
// Compiled into the stdio server too, which adds them when AGENTCAT_E2E_EXPORTER_HOST names the host.
internal static class E2EExporters
{
    internal const string HostVariable = "AGENTCAT_E2E_EXPORTER_HOST";

    // The names in AgentCatOptions.Exporters, in the order Add adds them.
    internal const string Otlp = "otlp";
    internal const string Datadog = "datadog";
    internal const string Sentry = "sentry";
    internal const string PostHog = "posthog";

    // Datadog's URLs are always https://http-intake.logs.<site>/api/v2/logs and https://api.<site>/api/v1/series. The
    // site is a reserved name that resolves nowhere: only the exporter handler of an in-process lane sends it to the host.
    internal const string DatadogSite = "datadog.test";
    internal const string DatadogLogsHost = "http-intake.logs." + DatadogSite;
    internal const string DatadogMetricsHost = "api." + DatadogSite;
    internal const string DatadogApiKey = "dd-e2e-api-key";
    internal const string DatadogService = "agentcat-e2e";

    // A Sentry DSN takes a hex public key.
    internal const string SentryPublicKey = "0123456789abcdef0123456789abcdef";
    internal const string SentryProjectId = "42";
    internal const string PostHogApiKey = "phc_e2e";

    // The path of each exporter's requests at the host.
    internal const string OtlpPath = "/v1/traces";
    internal const string DatadogLogsPath = "/api/v2/logs";
    internal const string DatadogMetricsPath = "/api/v1/series";
    internal const string SentryPath = "/api/" + SentryProjectId + "/envelope/";
    internal const string PostHogPath = "/batch";

    // OTLP, Datadog when withDatadog is set, Sentry and PostHog, all at hostUrl (http://127.0.0.1:<port>). tracing turns
    // on Sentry's EnableTracing and PostHog's EnableAITracing.
    internal static void Add(AgentCatOptions options, string hostUrl, bool withDatadog, bool tracing)
    {
        options.Exporters[Otlp] = new OtlpExporter(hostUrl);
        if (withDatadog)
        {
            options.Exporters[Datadog] = new DatadogExporter(DatadogApiKey, DatadogSite, DatadogService);
        }

        options.Exporters[Sentry] = new SentryExporter(SentryDsn(hostUrl)) { EnableTracing = tracing };
        options.Exporters[PostHog] = new PostHogExporter(PostHogApiKey) { Host = hostUrl, EnableAITracing = tracing };
    }

    // http://<key>@127.0.0.1:<port>/42, whose envelopes go to http://127.0.0.1:<port>/api/42/envelope/.
    internal static string SentryDsn(string hostUrl)
        => hostUrl.Replace("://", "://" + SentryPublicKey + "@", StringComparison.Ordinal) + "/" + SentryProjectId;
}
