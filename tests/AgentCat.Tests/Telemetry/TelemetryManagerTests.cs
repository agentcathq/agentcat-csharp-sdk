#pragma warning disable CA1861
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Internal.Telemetry;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Telemetry;

public sealed class TelemetryManagerTests : IDisposable
{
    private const string SentryEndpointLine = "SentryExporter: Initialized with endpoint https://o1.ingest.sentry.io/api/42/envelope/";

    private const string PostHogEndpointLine = "PostHogExporter: Initialized with endpoint https://us.i.posthog.com/batch";

    private readonly RecordingLog _log = new();
    private readonly RecordingSender _sender = new();
    private readonly RecordingExporters _exporters = new();
    private readonly AgentCatRuntime _runtime;

    public TelemetryManagerTests()
    {
        _runtime = new AgentCatRuntime(_log, _sender, exporterBuilder: _exporters.Build);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _runtime.Pipeline.Dispose();

    private static AgentCatEvent Event(string id = "evt_1") => new()
    {
        Id = id,
        SessionId = "ses_1",
        ProjectId = "proj_1",
        EventType = "mcp:tools/call",
        Timestamp = new DateTimeOffset(2025, 1, 15, 10, 0, 0, TimeSpan.Zero),
    };

    private static KeyValuePair<string, AgentCatExporter?> Entry(string name, AgentCatExporter? config) => new(name, config);

    private static KeyValuePair<string, ITelemetryExporter> Named(string name, ITelemetryExporter exporter) => new(name, exporter);

    private static KeyValuePair<string, AgentCatExporter?>[] AllFourTypes() => new[]
    {
        Entry("otlp", new OtlpExporter("http://otlp.test")),
        Entry("dd", new DatadogExporter("k", "datadoghq.com", "svc")),
        Entry("sentry", new SentryExporter("https://abc@o1.ingest.sentry.io/42")),
        Entry("ph", new PostHogExporter("phc_x")),
    };

    // The Sentry and PostHog constructors log their endpoint (TS); the others log nothing.
    private void LogLikeTheRealConstructors()
        => _exporters.OnBuild = (_, config, runtime) =>
        {
            switch (config)
            {
                case SentryExporter:
                    runtime.Log.Write(SentryEndpointLine);
                    break;
                case PostHogExporter:
                    runtime.Log.Write(PostHogEndpointLine);
                    break;
            }
        };

    private async Task FlushAsync()
        => Assert.True(await _runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, Ct), "The event pipeline did not drain.");

    [Fact]
    public async Task Every_config_type_builds_its_exporter_and_every_exporter_gets_the_event()
    {
        LogLikeTheRealConstructors();

        TelemetryManager.Install(_runtime, AllFourTypes());

        Assert.Equal(
            new[]
            {
                "Initialized telemetry exporter: otlp",
                "Initialized telemetry exporter: dd",
                SentryEndpointLine,
                "Initialized telemetry exporter: sentry",
                PostHogEndpointLine,
                "Initialized telemetry exporter: ph",
                "Initialized telemetry with 4 exporters",
            },
            _log.Lines);
        Assert.Equal(new[] { "otlp", "dd", "sentry", "ph" }, _exporters.Built.Select(b => b.Name));
        Assert.All(_exporters.Built, b => Assert.Same(_runtime, b.Runtime));
        Assert.True(_runtime.ExportersClaimed);

        var e = Event();
        _runtime.Pipeline.Add(new QueuedEvent(e));
        await FlushAsync();

        Assert.Equal(new[] { "otlp", "dd", "sentry", "ph" }, _exporters.Exports.Select(x => x.Name));
        Assert.All(_exporters.Exports, x => Assert.Same(e, x.Event));
    }

    // PHP testAnUnknownConfigIsSkippedAndTheOthersAreBuilt: the set of config types is closed, so only a null value is
    // unknown.
    [Fact]
    public void A_null_config_is_skipped_with_its_type_and_the_others_are_built()
    {
        TelemetryManager.Install(_runtime, new[] { Entry("a", new OtlpExporter("http://h")), Entry("b", null) });

        Assert.Equal(
            new[] { "Initialized telemetry exporter: a", "Unknown exporter type: null", "Initialized telemetry with 2 exporters" },
            _log.Lines);
        Assert.Contains(_log.Written, w => w.Line == "Unknown exporter type: null" && w.Severity == LogSeverity.Error);
        Assert.Equal("a", Assert.Single(_exporters.Built).Name);
    }

    public static TheoryData<Exception, string> InitializeFailures => new()
    {
        { new JsError("Error", "Invalid Sentry DSN: https://***@o1.ingest.sentry.io/42"), "Error: Invalid Sentry DSN: https://***@o1.ingest.sentry.io/42" },
        { new ArgumentException("bad config"), "ArgumentException: bad config" },
    };

    // PHP testAnInvalidDsnDoesNotStopTheNextExporter. An error the SDK builds keeps its TS text; a platform error is
    // written as "{type}: {message}".
    [Theory]
    [MemberData(nameof(InitializeFailures))]
    public async Task An_invalid_config_does_not_stop_the_next_exporter(Exception failure, string text)
    {
        LogLikeTheRealConstructors();
        var onBuild = _exporters.OnBuild!;
        _exporters.OnBuild = (name, config, runtime) =>
        {
            if (config is SentryExporter)
            {
                throw failure;
            }

            onBuild(name, config, runtime);
        };

        TelemetryManager.Install(_runtime, new[]
        {
            Entry("sentry", new SentryExporter("https://ABC@o1.ingest.sentry.io/42")),
            Entry("ph", new PostHogExporter("phc_x")),
        });
        _runtime.Pipeline.Add(new QueuedEvent(Event()));
        await FlushAsync();

        Assert.Equal(
            new[]
            {
                "Failed to initialize exporter sentry: " + text,
                PostHogEndpointLine,
                "Initialized telemetry exporter: ph",
                "Initialized telemetry with 2 exporters",
            },
            _log.Lines.Take(4));
        Assert.Equal(new[] { "ph" }, _exporters.Exports.Select(x => x.Name));
    }

    public static TheoryData<Exception, string> ExportFailures => new()
    {
        { new InvalidOperationException("boom"), "boom" },
        { new JsError("TypeError", "value.replace is not a function"), "value.replace is not a function" },
    };

    // PHP testAThrowingExporterIsLoggedWithTheTypeScriptMessageAndTheNextOneRuns: the line carries TS error.message.
    [Theory]
    [MemberData(nameof(ExportFailures))]
    public void A_throwing_exporter_is_logged_with_its_message_and_the_next_one_runs(Exception failure, string message)
    {
        _exporters.ThrowOnExport = name => name == "first" ? failure : null;
        var manager = new TelemetryManager(
            new[] { Named("first", _exporters.Build("first", new OtlpExporter("http://a"), _runtime)!), Named("second", _exporters.Build("second", new OtlpExporter("http://b"), _runtime)!) },
            _log);
        var e = Event();

        var export = manager.ExportAsync(e, Ct);

        Assert.Equal(TaskStatus.RanToCompletion, export.Status);
        Assert.Equal(new[] { "Telemetry export failed for first: " + message }, _log.Lines);
        Assert.Equal(new[] { "first", "second" }, _exporters.Exports.Select(x => x.Name));
        Assert.All(_exporters.Exports, x => Assert.Same(e, x.Event));
    }

    // The pipeline's own "Telemetry export error" line stays unreachable, as in TS.
    [Fact]
    public async Task A_throwing_exporter_never_reaches_the_pipeline()
    {
        _exporters.ThrowOnExport = _ => new InvalidOperationException("boom");
        TelemetryManager.Install(_runtime, new[] { Entry("a", new OtlpExporter("http://a")), Entry("b", new OtlpExporter("http://b")) });

        var e = Event();
        _runtime.Pipeline.Add(new QueuedEvent(e));
        await FlushAsync();

        Assert.Equal(new[] { "Telemetry export failed for a: boom", "Telemetry export failed for b: boom" }, _log.Lines.Skip(3).Take(2));
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Telemetry export error", StringComparison.Ordinal));
        Assert.Same(e, Assert.Single(_sender.Sent));
    }

    [Fact]
    public void A_manager_without_exporters_does_nothing()
    {
        var manager = new TelemetryManager(Array.Empty<KeyValuePair<string, ITelemetryExporter>>(), _log);

        var export = manager.ExportAsync(Event(), Ct);

        Assert.Equal(TaskStatus.RanToCompletion, export.Status);
        Assert.Equal(0, manager.Count);
        Assert.Empty(_log.Lines);
    }

    // PHP testInstallIsFirstWinsAndWarnsOnceForADifferentSet, without the WARN line: TS ignores a later set silently.
    [Fact]
    public async Task Install_is_first_wins_and_a_later_set_is_ignored_silently()
    {
        TelemetryManager.Install(_runtime, Array.Empty<KeyValuePair<string, AgentCatExporter?>>());
        Assert.Empty(_log.Lines);

        TelemetryManager.Install(_runtime, new[] { Entry("otlp", new OtlpExporter("http://a")) });
        TelemetryManager.Install(_runtime, new[] { Entry("otlp", new OtlpExporter("http://a")) });
        TelemetryManager.Install(_runtime, new[] { Entry("otlp", new OtlpExporter("http://b")) });
        TelemetryManager.Install(_runtime, new[] { Entry("x", new OtlpExporter("http://c")) });
        TelemetryManager.Install(_runtime, Array.Empty<KeyValuePair<string, AgentCatExporter?>>());

        Assert.Equal(new[] { "Initialized telemetry exporter: otlp", "Initialized telemetry with 1 exporters" }, _log.Lines);
        var built = Assert.Single(_exporters.Built);
        Assert.Equal("http://a", Assert.IsType<OtlpExporter>(built.Config).Endpoint);

        _runtime.Pipeline.Add(new QueuedEvent(Event()));
        await FlushAsync();
        Assert.Equal("otlp", Assert.Single(_exporters.Exports).Name);
    }

    // PHP testAnEmptySetInstallsAnEmptyManager, inverted: an empty set is "not configured" in C#, so it claims nothing.
    [Fact]
    public async Task An_empty_set_installs_nothing_and_a_later_set_still_installs()
    {
        TelemetryManager.Install(_runtime, Array.Empty<KeyValuePair<string, AgentCatExporter?>>());
        TelemetryManager.Install(_runtime, Array.Empty<KeyValuePair<string, AgentCatExporter?>>());

        Assert.Empty(_log.Lines);
        Assert.False(_runtime.ExportersClaimed);

        TelemetryManager.Install(_runtime, new[] { Entry("otlp", new OtlpExporter("http://a")) });
        _runtime.Pipeline.Add(new QueuedEvent(Event()));
        await FlushAsync();

        Assert.Equal("Initialized telemetry with 1 exporters", _log.Lines[1]);
        Assert.Equal("otlp", Assert.Single(_exporters.Exports).Name);
    }

    // PHP testTheInstallCountIsOfTheEntriesGiven: built or not.
    [Fact]
    public void The_install_count_is_of_the_entries_given()
    {
        _exporters.OnBuild = (name, _, _) =>
        {
            if (name == "broken")
            {
                throw new InvalidOperationException("no");
            }
        };

        TelemetryManager.Install(_runtime, new[]
        {
            Entry("otlp", new OtlpExporter("http://a")),
            Entry("bad", null),
            Entry("broken", new DatadogExporter("k", "datadoghq.com", "svc")),
        });

        Assert.Equal(
            new[]
            {
                "Initialized telemetry exporter: otlp",
                "Unknown exporter type: null",
                "Failed to initialize exporter broken: InvalidOperationException: no",
                "Initialized telemetry with 3 exporters",
            },
            _log.Lines);
    }

    [Fact]
    public void A_runtime_that_hosts_no_exporters_builds_nothing_and_claims_nothing()
    {
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, new RecordingSender());
        try
        {
            TelemetryManager.Install(runtime, AllFourTypes());

            Assert.False(runtime.HostsExporters);
            Assert.False(runtime.ExportersClaimed);
            Assert.Empty(log.Lines);
            Assert.True(_runtime.HostsExporters);
        }
        finally
        {
            runtime.Pipeline.Dispose();
        }
    }

    [Fact]
    public void A_claim_succeeds_once_per_runtime()
    {
        var other = new AgentCatRuntime(new RecordingLog(), new RecordingSender(), exporterBuilder: _exporters.Build);
        try
        {
            Assert.True(_runtime.TryClaimExporters());
            Assert.False(_runtime.TryClaimExporters());
            Assert.True(other.TryClaimExporters());
            Assert.False(new AgentCatRuntime(new RecordingLog(), new RecordingSender()).TryClaimExporters());
        }
        finally
        {
            other.Pipeline.Dispose();
        }
    }

    // A known type that has no exporter builds nothing, with no line of its own.
    [Fact]
    public void The_factory_writes_the_initialized_line_only_for_a_built_exporter()
    {
        Assert.Null(ExporterFactory.Create("none", new OtlpExporter("http://a"), _runtime, static (_, _, _) => null));
        Assert.Empty(_log.Lines);

        Assert.NotNull(ExporterFactory.Create("one", new OtlpExporter("http://a"), _runtime));
        Assert.Equal(new[] { "Initialized telemetry exporter: one" }, _log.Lines);
    }
}
