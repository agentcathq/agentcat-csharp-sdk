using System.Reflection;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.Telemetry;

public sealed class ExporterConfigTests
{
    private static readonly Type[] ConfigTypes = { typeof(OtlpExporter), typeof(DatadogExporter), typeof(SentryExporter), typeof(PostHogExporter) };

    // PHP ExporterClassesTest: defaults and named arguments.
    [Fact]
    public void Required_values_come_from_the_constructor_and_optional_ones_start_unset()
    {
        var otlp = new OtlpExporter("http://otlp.test");
        var datadog = new DatadogExporter("dd-key", "datadoghq.eu", "weather-mcp");
        var sentry = new SentryExporter("https://abc@o1.ingest.sentry.io/42");
        var posthog = new PostHogExporter("phc_x");

        Assert.Equal("http://otlp.test", otlp.Endpoint);
        Assert.Null(otlp.Headers);
        Assert.Equal("dd-key", datadog.ApiKey);
        Assert.Equal("datadoghq.eu", datadog.Site);
        Assert.Equal("weather-mcp", datadog.Service);
        Assert.Null(datadog.Env);
        Assert.Equal("https://abc@o1.ingest.sentry.io/42", sentry.Dsn);
        Assert.Null(sentry.Environment);
        Assert.Null(sentry.Release);
        Assert.False(sentry.EnableTracing);
        Assert.Equal("phc_x", posthog.ApiKey);
        Assert.Null(posthog.Host);
        Assert.False(posthog.EnableAITracing);
    }

    [Fact]
    public void Optional_values_are_set_with_object_initializers()
    {
        var headers = new Dictionary<string, string> { ["x-api-key"] = "k" };

        var otlp = new OtlpExporter("http://otlp.test") { Headers = headers };
        var datadog = new DatadogExporter("dd-key", "datadoghq.com", "svc") { Env = "prod" };
        var sentry = new SentryExporter("https://abc@o1.ingest.sentry.io/42") { Environment = "staging", Release = "1.2.3", EnableTracing = true };
        var posthog = new PostHogExporter("phc_x") { Host = "https://eu.i.posthog.com/", EnableAITracing = true };

        Assert.Same(headers, otlp.Headers);
        Assert.Equal("prod", datadog.Env);
        Assert.Equal("staging", sentry.Environment);
        Assert.Equal("1.2.3", sentry.Release);
        Assert.True(sentry.EnableTracing);
        Assert.Equal("https://eu.i.posthog.com/", posthog.Host);
        Assert.True(posthog.EnableAITracing);
    }

    [Fact]
    public void A_null_required_value_throws_ArgumentNullException_with_its_name()
    {
        Assert.Equal("endpoint", Assert.Throws<ArgumentNullException>(() => new OtlpExporter(null!)).ParamName);
        Assert.Equal("apiKey", Assert.Throws<ArgumentNullException>(() => new DatadogExporter(null!, "datadoghq.com", "svc")).ParamName);
        Assert.Equal("site", Assert.Throws<ArgumentNullException>(() => new DatadogExporter("k", null!, "svc")).ParamName);
        Assert.Equal("service", Assert.Throws<ArgumentNullException>(() => new DatadogExporter("k", "datadoghq.com", null!)).ParamName);
        Assert.Equal("dsn", Assert.Throws<ArgumentNullException>(() => new SentryExporter(null!)).ParamName);
        Assert.Equal("apiKey", Assert.Throws<ArgumentNullException>(() => new PostHogExporter(null!)).ParamName);
    }

    // PHP "an invalid DSN constructs without an error": the manager logs and skips it at install, in the customer's
    // code nothing throws.
    [Fact]
    public void Invalid_or_empty_values_construct_without_an_error()
    {
        var thrown = Record.Exception(() =>
        {
            _ = new SentryExporter("not a dsn");
            _ = new SentryExporter("");
            _ = new OtlpExporter("");
            _ = new OtlpExporter("::not a url") { Headers = new Dictionary<string, string> { ["bad header name"] = "\n" } };
            _ = new DatadogExporter("", "", "");
            _ = new PostHogExporter("") { Host = "" };
        });

        Assert.Null(thrown);
    }

    [Fact]
    public void The_set_of_exporter_types_is_closed()
    {
        var baseType = typeof(AgentCatExporter);

        Assert.True(baseType.IsAbstract);
        Assert.Equal("AgentCat", baseType.Namespace);
        Assert.DoesNotContain(
            baseType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly);
        Assert.All(ConfigTypes, t =>
        {
            Assert.True(t.IsPublic && t.IsSealed, t.Name);
            Assert.Same(baseType, t.BaseType);
            Assert.Equal("AgentCat", t.Namespace);
        });
        Assert.Equal(
            ConfigTypes.OrderBy(t => t.Name),
            baseType.Assembly.GetTypes().Where(t => t.IsSubclassOf(baseType)).OrderBy(t => t.Name));
    }

    // PHP "final readonly": every value is fixed once the object is built.
    [Fact]
    public void Config_values_cannot_change_after_construction()
    {
        Assert.All(ConfigTypes.Append(typeof(AgentCatExporter)), t =>
        {
            foreach (var property in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var setter = property.SetMethod;
                Assert.True(
                    setter is null || !setter.IsPublic || setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"),
                    t.Name + "." + property.Name + " has a public setter.");
            }
        });
    }

    [Fact]
    public void Headers_are_copied_at_install()
    {
        var headers = new Dictionary<string, string> { ["x-b"] = "2", ["x-a"] = "1" };
        var config = new OtlpExporter("http://otlp.test") { Headers = headers };
        var exporters = new RecordingExporters();
        var runtime = new AgentCatRuntime(new RecordingLog(), new RecordingSender(), exporterBuilder: exporters.Build);
        try
        {
            var options = new AgentCatOptions();
            options.Exporters["otlp"] = config;

            Assert.NotNull(AgentCatEngine.Install(new McpServerOptions(), "proj_a", options, runtime, "options", _ => null));
            headers["x-a"] = "changed";
            headers.Remove("x-b");
            headers["x-c"] = "3";

            var built = Assert.IsType<OtlpExporter>(Assert.Single(exporters.Built).Config);
            Assert.Equal("http://otlp.test", built.Endpoint);
            Assert.Equal(new[] { Pair("x-b", "2"), Pair("x-a", "1") }, built.Headers!);
            Assert.Same(headers, config.Headers);
        }
        finally
        {
            runtime.Pipeline.Dispose();
        }
    }

    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);
}
