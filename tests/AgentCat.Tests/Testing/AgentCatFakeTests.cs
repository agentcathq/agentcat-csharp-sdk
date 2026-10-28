using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SdkDiagnostics = AgentCat.Internal.Diagnostics.Diagnostics;

namespace AgentCat.Tests.Testing;

// The fake is process-wide: it changes AgentCatRuntime.Current for every caller, so these tests run alone.
[Collection(SharedRuntime.Name)]
public sealed class AgentCatFakeTests : IDisposable
{
    private const string Project = "proj_abcdefghijklmnopqrstuvwxyz0";

    private const string ToolCall = "mcp:tools/call";

    private const string NotTracked = "Server is not tracked. Please call agentcat.track() first or provide a session ID string.";

    private const string NotTrackedWhileOff =
        "Warning: Failed to publish custom event - the server is not tracked (it was set up while AgentCat was off; call AgentCatSdk.Fake() before WithAgentCat or Track)";

    private const string Hint = " A server was set up while AgentCat was off: call AgentCatSdk.Fake() before WithAgentCat or Track.";

    private const string NoMatchingToolCall = "Failed asserting that a tool call matching the filter was tracked.";

    private const string NoMatchingCustomEvent = "Failed asserting that a custom event matching the filter was published.";

    private const string Rebuilt = "Rebuilt injection registries on demand (tools/call before tools/list on this instance)";

    private const string NoProjectWarning = "Warning: No projectId provided and no exporters configured. Events will not be sent anywhere.";

    private const string ImageMarker = "[image content redacted - not supported by AgentCat]";

    private readonly List<AgentCatRuntime> _runtimes = new();

    public AgentCatFakeTests()
    {
        EnabledGate.ResetSkippedAnInstall();
        AgentCatTracker.ResetLogToHostWarning();
    }

    public void Dispose()
    {
        EnabledGate.ResetSkippedAnInstall();
        AgentCatTracker.ResetLogToHostWarning();
        foreach (var runtime in _runtimes)
        {
            runtime.Pipeline.Dispose();
        }
    }

    public static TheoryData<string> EntryPoints => new() { "WithAgentCat", "Track" };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Started(string project, string flavor = "di")
        => $"AgentCat setup started | project {project} | sdk v{McpHarness.McpMajor}/{flavor} | signals filters";

    private static string Complete(string project)
        => $"AgentCat setup complete | project {project} | tracing=true context=true reportMissing=true exporters=0";

    private static string Published(string session) => $"Published custom event for session {session} with type 'agentcat:custom'";

    private static McpHarness Echo()
    {
        var h = new McpHarness();
        h.AddTool((string msg) => "echo:" + msg, "echo");
        return h;
    }

    // Starts an echo server tracked through the public entry point, so it binds to AgentCatRuntime.Current.
    private static async Task<McpHarness> TrackedAsync(
        string entryPoint = "WithAgentCat",
        string project = Project,
        Action<AgentCatOptions>? configure = null,
        McpHarness? harness = null)
    {
        var h = harness ?? Echo();
        if (entryPoint == "WithAgentCat")
        {
            h.ConfigureBuilder(b => b.WithAgentCat(project, configure));
            await h.StartAsync(null, withServices: true);
        }
        else
        {
            AgentCatOptions? options = null;
            if (configure is not null)
            {
                options = new AgentCatOptions();
                configure(options);
            }

            h.Configure(o => AgentCatSdk.Track(o, project, options));
            await h.StartAsync();
        }

        return h;
    }

    // A runtime whose gate says "test run" and no AGENTCAT_ENABLED: what AgentCatRuntime.Shared is in a customer's
    // test run.
    private AgentCatRuntime TestRun(RecordingLog? log = null)
    {
        var runtime = new AgentCatRuntime(
            log ?? new RecordingLog(),
            new RecordingSender(),
            gate: new EnabledGate(static _ => null, static () => true, NullAgentCatLog.Instance));
        _runtimes.Add(runtime);
        return runtime;
    }

    // What the public WithAgentCat resolves in a customer's test run: the current fake, else Shared, whose gate says
    // "test run" there. The test process sets AGENTCAT_ENABLED=true, so a test-run runtime stands in for Shared.
    private static Func<AgentCatRuntime> TestRunUnlessFake(AgentCatRuntime testRun)
        => () =>
        {
            var current = AgentCatRuntime.Current;
            return ReferenceEquals(current, AgentCatRuntime.Shared) ? testRun : current;
        };

    private static bool HasContext(Tool tool)
        => tool.InputSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("context", out _);

    private static string? Msg(AgentCatEvent e) => (string?)e.Parameters?["request"]?["params"]?["arguments"]?["msg"];

    private static async Task<string> FailureOf(Func<Task> assertion)
        => (await Assert.ThrowsAsync<AgentCatAssertionException>(assertion)).Message;

    private static async Task<T> Bounded<T>(Task<T> task)
    {
        await FakeAgentCatApi.WithinAsync(task, FakeAgentCatApi.WaitBound);
        return await task;
    }

    private static async Task Bounded(Task task)
    {
        await FakeAgentCatApi.WithinAsync(task, FakeAgentCatApi.WaitBound);
        await task;
    }

    // Other tests write this file too, and each write holds an exclusive lock on it for a moment.
    private static async Task<string> SharedLogTextAsync()
    {
        var path = FileLog.DefaultPath();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return path is not null && File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(10, Token);
            }
        }
    }

    [Fact]
    public async Task Fake_turns_the_engine_on_in_a_test_run()
    {
        var project = "proj_fake" + Guid.NewGuid().ToString("N");
        using var fake = AgentCatSdk.Fake();
        using var testRun = new EnvironmentScope(EnabledGate.Variable, null);
        Assert.False(AgentCatRuntime.Shared.Gate.IsEnabled(), "The process gate does not say test run.");

        await using var h = await TrackedAsync(project: project);
        await h.CallToolAsync("echo", """{"msg":"hi","context":"checking the fake"}""");
        await AgentCatSdk.PublishCustomEventAsync("s-1", project, new CustomEventData { Message = "m" });

        var e = Assert.Single(await fake.GetEventsAsync(Token), x => x.EventType == ToolCall);
        Assert.Equal("echo", e.ResourceName);
        Assert.Equal("checking the fake", e.UserIntent);
        Assert.Equal(project, e.ProjectId);
        Assert.Matches("^evt_[0-9A-Za-z]{27}$", e.Id);
        Assert.Equal(
            new[] { Started(project), Complete(project), Rebuilt, Published("s-1") },
            await fake.GetLogsAsync(Token));
        Assert.DoesNotContain(project, await SharedLogTextAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tracked_tool_call_is_recorded_with_its_hooks_applied()
    {
        using var fake = AgentCatSdk.Fake();
        await using var h = await TrackedAsync(configure: o => o.Identify = (_, _) => new ValueTask<UserIdentity?>(new UserIdentity("u1")));

        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        await fake.AssertTrackedAsync(e => e.ResourceName == "echo" && e.IdentifyActorGivenId == "u1", Token);
        await fake.AssertTrackedCountAsync(1, Token);
        var e = Assert.Single(await fake.GetEventsAsync(Token));
        Assert.Matches("^evt_[0-9A-Za-z]{27}$", e.Id);
    }

    [Fact]
    public async Task A_hook_result_is_awaited_before_assertions()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fake = AgentCatSdk.Fake();
        await using var h = await TrackedAsync(configure: o => o.Identify = async (_, _) =>
        {
            await release.Task;
            return new UserIdentity("late-user");
        });

        await h.CallToolAsync("echo", """{"msg":"hi"}""");
        var assertion = fake.AssertTrackedAsync(e => e.IdentifyActorGivenId == "late-user", Token);
        await Task.Delay(50, Token);

        Assert.False(assertion.IsCompleted, "The assertion did not wait for the Identify hook.");
        release.SetResult(true);
        await Bounded(assertion);
        Assert.Equal("late-user", Assert.Single(await fake.GetEventsAsync(Token)).IdentifyActorGivenId);
    }

    [Fact]
    public async Task Recorded_events_are_after_redaction_sanitization_and_truncation()
    {
        var intent = "secret " + new string('x', 3000);
        using var fake = AgentCatSdk.Fake();
        var harness = new McpHarness();
        harness.AddHandlerTool(
            """{"name":"picture","inputSchema":{"type":"object","properties":{"msg":{"type":"string"}}}}""",
            (_, _) => new ValueTask<CallToolResult>(Mcp.Result("""{"content":[{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"}]}""")));
        await using var h = await TrackedAsync(
            harness: harness,
            configure: o =>
            {
                o.RedactEvent = (e, _) =>
                {
                    e.UserIntent = intent;
                    return new ValueTask<AgentCatEvent?>(e);
                };
                o.RedactSensitiveInformation = (text, _) => new ValueTask<string>(text.Replace("secret", "[REDACTED]"));
            });

        await h.CallToolAsync("picture", """{"msg":"my secret","context":"short"}""");

        var e = Assert.Single(await fake.GetEventsAsync(Token));
        Assert.Equal("[REDACTED] " + new string('x', 2048 - "[REDACTED] ".Length) + "...", e.UserIntent);
        Assert.Equal("my [REDACTED]", (string?)e.Parameters?["request"]?["params"]?["arguments"]?["msg"]);
        Assert.Equal(ImageMarker, (string?)e.Response?["content"]?[0]?["text"]);
        Assert.Matches("^evt_[0-9A-Za-z]{27}$", e.Id);
    }

    [Fact]
    public async Task Telemetry_only_events_are_recorded_too()
    {
        using var fake = AgentCatSdk.Fake();
        await using var h = await TrackedAsync(project: "");

        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        await fake.AssertTrackedCountAsync(1, Token);
        Assert.True(string.IsNullOrEmpty(Assert.Single(await fake.GetEventsAsync(Token)).ProjectId));
        Assert.Contains(NoProjectWarning, await fake.GetLogsAsync(Token));
    }

    [Fact]
    public async Task No_success_line_is_logged()
    {
        using var fake = AgentCatSdk.Fake();
        await using var h = await TrackedAsync();

        await h.CallToolAsync("echo", """{"msg":"hi"}""");
        await AgentCatSdk.PublishCustomEventAsync("s-1", Project);

        Assert.Equal(2, (await fake.GetEventsAsync(Token)).Count);
        Assert.DoesNotContain(await fake.GetLogsAsync(Token), l => l.StartsWith("Successfully sent event", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_goes_to_the_network()
    {
        using var api = new FakeAgentCatApi();
        using var fake = AgentCatSdk.Fake();
        await using var h = await TrackedAsync(configure: o => o.ApiBaseUrl = api.BaseUrl);

        await h.CallToolAsync("echo", """{"msg":"hi"}""");
        await AgentCatSdk.PublishCustomEventAsync("s-1", Project);

        await fake.AssertTrackedCountAsync(1, Token);
        await fake.AssertCustomEventPublishedAsync(cancellationToken: Token);
        Assert.NotEmpty(await fake.GetLogsAsync(Token));
        Assert.DoesNotContain(await fake.GetLogsAsync(Token), l => l.Contains("send event", StringComparison.Ordinal));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Custom_events_of_both_forms_are_recorded()
    {
        using var fake = AgentCatSdk.Fake();
        await using var h = await TrackedAsync();
        var injected = h.Services.GetRequiredService<IAgentCat>();

        await AgentCatSdk.PublishCustomEventAsync("s-static", "proj_static", new CustomEventData { ResourceName = "static-form" });
        await injected.PublishCustomEventAsync("s-injected", "proj_injected", new CustomEventData { ResourceName = "injected-form" });
        await AgentCatSdk.PublishCustomEventAsync(h.Server, "proj_ignored", new CustomEventData { SessionId = "s-server", ResourceName = "server-form" });

        await fake.AssertCustomEventPublishedAsync(e => e.ResourceName == "static-form" && e.SessionId == "s-static" && e.ProjectId == "proj_static", Token);
        await fake.AssertCustomEventPublishedAsync(e => e.ResourceName == "injected-form" && e.ProjectId == "proj_injected", Token);
        await fake.AssertCustomEventPublishedAsync(e => e.ResourceName == "server-form" && e.SessionId == "s-server" && e.ProjectId == Project, Token);
        Assert.All(await fake.GetEventsAsync(Token), e => Assert.Matches("^evt_[0-9A-Za-z]{27}$", e.Id));
        await fake.AssertTrackedCountAsync(0, Token);
    }

    [Fact]
    public async Task Custom_event_is_not_a_tool_call()
    {
        using var fake = AgentCatSdk.Fake();

        await AgentCatSdk.PublishCustomEventAsync("s-1", Project, new CustomEventData { ResourceName = "c" });

        await fake.AssertCustomEventPublishedAsync(e => e.ResourceName == "c", Token);
        await fake.AssertCustomEventPublishedAsync(cancellationToken: Token);
        await fake.AssertTrackedCountAsync(0, Token);
        Assert.Equal(NoMatchingToolCall, await FailureOf(() => fake.AssertTrackedAsync(cancellationToken: Token)));
        Assert.Equal(NoMatchingCustomEvent, await FailureOf(() => fake.AssertCustomEventPublishedAsync(e => e.ResourceName == "other", Token)));
    }

    [Fact]
    public async Task AssertTrackedAsync_without_a_filter_needs_one_tool_call()
    {
        using var fake = AgentCatSdk.Fake();

        Assert.Equal(NoMatchingToolCall, await FailureOf(() => fake.AssertTrackedAsync(cancellationToken: Token)));

        await using var h = await TrackedAsync();
        await h.CallToolAsync("echo", """{"msg":"hi"}""");
        await fake.AssertTrackedAsync(cancellationToken: Token);
    }

    [Fact]
    public async Task AssertNothingTrackedAsync_passes_on_an_empty_fake_and_fails_after_any_event()
    {
        using var fake = AgentCatSdk.Fake();
        await fake.AssertNothingTrackedAsync(Token);

        await AgentCatSdk.PublishCustomEventAsync("s-1", Project);

        Assert.Equal(
            "Failed asserting that nothing was tracked: 1 event(s) were recorded.",
            await FailureOf(() => fake.AssertNothingTrackedAsync(Token)));

        await using var h = await TrackedAsync();
        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        Assert.Equal(
            "Failed asserting that nothing was tracked: 2 event(s) were recorded.",
            await FailureOf(() => fake.AssertNothingTrackedAsync(Token)));
    }

    [Fact]
    public async Task AssertTrackedCountAsync_counts_tool_calls_only()
    {
        using var fake = AgentCatSdk.Fake();
        await fake.AssertTrackedCountAsync(0, Token);
        await using var h = await TrackedAsync();

        await h.CallToolAsync("echo", """{"msg":"one"}""");
        await h.CallToolAsync("echo", """{"msg":"two"}""");
        await AgentCatSdk.PublishCustomEventAsync("s-1", Project);

        await fake.AssertTrackedCountAsync(2, Token);
        Assert.Equal("Failed asserting that 3 tool call(s) were tracked: 2 were.", await FailureOf(() => fake.AssertTrackedCountAsync(3, Token)));
        Assert.Equal("Failed asserting that 0 tool call(s) were tracked: 2 were.", await FailureOf(() => fake.AssertTrackedCountAsync(0, Token)));
    }

    [Fact]
    public async Task Failing_assertions_say_why()
    {
        using var fake = AgentCatSdk.Fake();
        await using var h = await TrackedAsync();
        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        Assert.Equal(NoMatchingToolCall, await FailureOf(() => fake.AssertTrackedAsync(_ => false, Token)));
        Assert.Equal("Failed asserting that 2 tool call(s) were tracked: 1 were.", await FailureOf(() => fake.AssertTrackedCountAsync(2, Token)));
        Assert.Equal(NoMatchingCustomEvent, await FailureOf(() => fake.AssertCustomEventPublishedAsync(cancellationToken: Token)));
        Assert.Equal(
            "Failed asserting that nothing was tracked: 1 event(s) were recorded.",
            await FailureOf(() => fake.AssertNothingTrackedAsync(Token)));
    }

    [Fact]
    public async Task Off_install_adds_the_hint_to_tracked_assertions()
    {
        var off = TestRun();
        AgentCatSdk.Track(new McpServerOptions(), Project, null, () => off);
        Assert.True(EnabledGate.SkippedAnInstall);
        using var fake = AgentCatSdk.Fake();

        Assert.Equal(NoMatchingToolCall + Hint, await FailureOf(() => fake.AssertTrackedAsync(_ => true, Token)));
        Assert.Equal("Failed asserting that 1 tool call(s) were tracked: 0 were." + Hint, await FailureOf(() => fake.AssertTrackedCountAsync(1, Token)));
        Assert.Equal(NoMatchingCustomEvent, await FailureOf(() => fake.AssertCustomEventPublishedAsync(cancellationToken: Token)));

        await using var h = await TrackedAsync();
        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        Assert.Equal(NoMatchingToolCall, await FailureOf(() => fake.AssertTrackedAsync(_ => false, Token)));
        Assert.Equal("Failed asserting that 2 tool call(s) were tracked: 1 were.", await FailureOf(() => fake.AssertTrackedCountAsync(2, Token)));
    }

    [Fact]
    public async Task Track_without_options_in_a_test_run_adds_no_hint()
    {
        var off = TestRun();

        Assert.Null(AgentCatSdk.Track(null, Project, null, () => off));

        Assert.False(EnabledGate.SkippedAnInstall, "Track(null) installs nothing, so it skipped no install.");
        using var fake = AgentCatSdk.Fake();
        Assert.Equal(NoMatchingToolCall, await FailureOf(() => fake.AssertTrackedAsync(cancellationToken: Token)));
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public async Task Custom_event_for_a_server_set_up_while_off_logs_the_warning(string entryPoint)
    {
        var offLog = new RecordingLog();
        var off = TestRun(offLog);
        await using var h = Echo();
        if (entryPoint == "WithAgentCat")
        {
            h.ConfigureBuilder(b => AgentCatMcpServerBuilderExtensions.WithAgentCat(b, Project, null, () => off));
            await h.StartAsync(null, withServices: true);
        }
        else
        {
            h.Configure(o => AgentCatSdk.Track(o, Project, null, () => off));
            await h.StartAsync();
        }

        Assert.True(EnabledGate.SkippedAnInstall);
        using var fake = AgentCatSdk.Fake();

        var published = AgentCatSdk.PublishCustomEventAsync(h.Server, Project, new CustomEventData { Message = "m" });

        Assert.Equal(TaskStatus.RanToCompletion, published.Status);
        Assert.Equal(new[] { NotTrackedWhileOff }, await fake.GetLogsAsync(Token));
        Assert.Empty(await fake.GetEventsAsync(Token));
        Assert.Empty(offLog.Lines);
    }

    [Fact]
    public async Task Untracked_server_without_an_off_install_still_throws()
    {
        using var fake = AgentCatSdk.Fake();
        await using var h = Echo();
        await h.StartAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => AgentCatSdk.PublishCustomEventAsync(h.Server, Project));

        Assert.Equal(NotTracked, error.Message);
        Assert.Empty(await fake.GetLogsAsync(Token));
    }

    [Fact]
    public async Task GetLogsAsync_runs_the_waiting_hooks_first()
    {
        using var fake = AgentCatSdk.Fake();
        await using var h = await TrackedAsync(configure: o => o.EventTags = async (_, ct) =>
        {
            await Task.Delay(50, ct);
            throw new InvalidOperationException("tags down");
        });

        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        Assert.Contains("eventTags callback error: InvalidOperationException: tags down", await fake.GetLogsAsync(Token));
    }

    [Fact]
    public async Task LogToHost_under_the_fake_writes_no_line_to_the_host_logger()
    {
        var host = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(host));
        using var fake = AgentCatSdk.Fake();
        await using var h = Echo();
        h.UseLoggerFactory(factory);

        await TrackedAsync(configure: o => o.LogToHost = true, harness: h);
        await AgentCatSdk.PublishCustomEventAsync(h.Server, Project, new CustomEventData { SessionId = "ses_1" });

        Assert.DoesNotContain(host.Entries, e => e.Category == "AgentCat");
        Assert.Equal(new[] { Started(Project), Complete(Project), Published("ses_1") }, await fake.GetLogsAsync(Token));
    }

    [Fact]
    public async Task Diagnostics_and_exporters_are_off_under_the_fake()
    {
        using var fake = AgentCatSdk.Fake();
        var runtime = AgentCatRuntime.Current;
        var exporter = new CountingExporter();
        Assert.True(runtime.Pipeline.TrySetExporter(exporter));
        await using var h = await TrackedAsync(configure: o => o.DisableDiagnostics = false);

        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        await fake.AssertTrackedCountAsync(1, Token);
        Assert.Equal(0, exporter.Calls);
        Assert.Same(SdkDiagnostics.Inert, runtime.Diagnostics);
        Assert.False(runtime.Diagnostics.IsEnabled);
        Assert.IsNotType<ProcessLog>(runtime.Log);
        Assert.Contains(Complete(Project), await fake.GetLogsAsync(Token));
    }

    [Fact]
    public async Task Kill_switch_false_does_not_affect_the_fake()
    {
        using var fake = AgentCatSdk.Fake();
        using var killSwitch = new EnvironmentScope(EnabledGate.Variable, "false");
        Assert.False(AgentCatRuntime.Shared.Gate.IsEnabled(), "The kill switch is not set.");

        await using var h = await TrackedAsync();
        await h.CallToolAsync("echo", """{"msg":"hi"}""");
        await AgentCatSdk.PublishCustomEventAsync("s-1", Project);

        await fake.AssertTrackedCountAsync(1, Token);
        await fake.AssertCustomEventPublishedAsync(cancellationToken: Token);
    }

    [Fact]
    public async Task Second_fake_starts_empty()
    {
        using var first = AgentCatSdk.Fake();
        await using var h = await TrackedAsync();
        await h.CallToolAsync("echo", """{"msg":"hi"}""");
        await first.AssertTrackedCountAsync(1, Token);

        using var second = AgentCatSdk.Fake();

        await second.AssertNothingTrackedAsync(Token);
        Assert.NotSame(first.Runtime, second.Runtime);

        // The server set up under the first fake records into the current one.
        await h.CallToolAsync("echo", """{"msg":"again"}""");
        Assert.Equal("again", Msg(Assert.Single(await second.GetEventsAsync(Token))));
        await first.AssertTrackedCountAsync(1, Token);
    }

    [Fact]
    public async Task Dispose_restores_the_previous_runtime()
    {
        var shared = AgentCatRuntime.Shared;
        Assert.Same(shared, AgentCatRuntime.Current);

        var first = AgentCatSdk.Fake();
        var second = first;
        try
        {
            Assert.Same(first.Runtime, AgentCatRuntime.Current);
            await AgentCatSdk.PublishCustomEventAsync("while-first", Project);

            second = AgentCatSdk.Fake();
            Assert.Same(second.Runtime, AgentCatRuntime.Current);
            await AgentCatSdk.PublishCustomEventAsync("while-second", Project);

            second.Dispose();
            Assert.Same(first.Runtime, AgentCatRuntime.Current);
            second.Dispose();
            Assert.Same(first.Runtime, AgentCatRuntime.Current);
            await AgentCatSdk.PublishCustomEventAsync("after-second", Project);

            first.Dispose();
            first.Dispose();
            Assert.Same(shared, AgentCatRuntime.Current);
        }
        finally
        {
            second.Dispose();
            first.Dispose();
        }

        Assert.Equal(new[] { Published("while-first"), Published("after-second") }, await first.GetLogsAsync(Token));
        Assert.Equal(new[] { Published("while-second") }, await second.GetLogsAsync(Token));
        Assert.Collection(
            await first.GetEventsAsync(Token),
            e => Assert.Equal("while-first", e.SessionId),
            e => Assert.Equal("after-second", e.SessionId));
    }

    [Fact]
    public void Disposing_an_older_fake_keeps_the_newest_current()
    {
        var shared = AgentCatRuntime.Shared;
        using var first = AgentCatSdk.Fake();
        using var second = AgentCatSdk.Fake();

        first.Dispose();
        Assert.Same(second.Runtime, AgentCatRuntime.Current);

        second.Dispose();
        Assert.Same(shared, AgentCatRuntime.Current);
    }

    [Fact]
    public async Task Servers_set_up_under_a_fake_record_into_the_next_fake_or_nowhere_after_Dispose()
    {
        using var fake = AgentCatSdk.Fake();
        await using var h = await TrackedAsync();
        fake.Dispose();

        await h.CallToolAsync("echo", """{"msg":"nowhere"}""");
        await AgentCatSdk.PublishCustomEventAsync(h.Server, Project, new CustomEventData { SessionId = "s-nowhere", ResourceName = "nowhere" });

        await fake.AssertNothingTrackedAsync(Token);
        Assert.Equal(new[] { Started(Project), Complete(Project) }, await fake.GetLogsAsync(Token));

        using var later = AgentCatSdk.Fake();
        await h.CallToolAsync("echo", """{"msg":"later"}""");
        await AgentCatSdk.PublishCustomEventAsync(h.Server, Project, new CustomEventData { SessionId = "s-later", ResourceName = "later" });

        Assert.Equal("later", Msg(Assert.Single(await later.GetEventsAsync(Token), e => e.EventType == ToolCall)));
        await later.AssertCustomEventPublishedAsync(e => e.ResourceName == "later", Token);
        await fake.AssertNothingTrackedAsync(Token);
    }

    [Fact]
    public async Task A_fixture_server_set_up_before_the_fakes_records_into_each_test_fake()
    {
        using var api = new FakeAgentCatApi();
        AgentCatRuntime.Shared.ApplyBaseUrl(api.BaseUrl, _ => null);
        var testRunLog = new RecordingLog();
        await using var fixture = new StatelessServer(TestRunUnlessFake(TestRun(testRunLog)));

        await fixture.CallAsync("before");
        Assert.True(EnabledGate.SkippedAnInstall);

        var first = AgentCatSdk.Fake();
        try
        {
            await fixture.CallAsync("one");
        }
        finally
        {
            first.Dispose();
        }

        await fixture.CallAsync("two");

        using var second = AgentCatSdk.Fake();
        await fixture.CallAsync("three");

        Assert.Equal("one", Msg(Assert.Single(await first.GetEventsAsync(Token))));
        Assert.Equal("three", Msg(Assert.Single(await second.GetEventsAsync(Token))));
        Assert.True(await AgentCatRuntime.Shared.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, Token));
        Assert.Empty(api.Requests);
        Assert.Empty(testRunLog.Lines);
    }

    // A fixture that decided under a fake that is gone serves each later request as the customer's own server.
    [Fact]
    public async Task A_stateless_fixture_is_unchanged_after_its_fake_is_disposed()
    {
        await using var fixture = new StatelessServer(TestRunUnlessFake(TestRun()));
        using (var fake = AgentCatSdk.Fake())
        {
            var (tracked, _) = await fixture.ListAndCallAsync("tracked");

            Assert.Contains(tracked, t => t.Name == "get_more_tools");
            Assert.True(HasContext(Assert.Single(tracked, t => t.Name == "echo")));
            await fake.AssertTrackedCountAsync(1, Token);
        }

        var (tools, result) = await fixture.ListAndCallAsync("after");

        var tool = Assert.Single(tools);
        Assert.Equal("echo", tool.Name);
        Assert.False(HasContext(tool));
        Assert.Equal("echo:after", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task A_server_tracked_before_a_fake_records_into_the_fake()
    {
        using var api = new FakeAgentCatApi();
        await using var h = Echo();
        h.Configure(o => AgentCatSdk.Track(o, Project, new AgentCatOptions { ApiBaseUrl = api.BaseUrl }));
        await h.StartAsync();
        Assert.True(EngineInstaller.TryGetTracker(h.ServerOptions, out var tracker));
        Assert.Same(AgentCatRuntime.Shared, tracker.Runtime);

        await h.CallToolAsync("echo", """{"msg":"sent"}""");
        await api.WaitForRequestsAsync(1);

        using var fake = AgentCatSdk.Fake();
        await h.CallToolAsync("echo", """{"msg":"recorded"}""");

        Assert.Equal("recorded", Msg(Assert.Single(await fake.GetEventsAsync(Token))));
        Assert.True(await AgentCatRuntime.Shared.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, Token));
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task A_stateful_server_set_up_while_off_stays_untracked_and_hints()
    {
        var fallback = TestRunUnlessFake(TestRun());
        await using var h = Echo();
        h.ConfigureBuilder(b => AgentCatMcpServerBuilderExtensions.WithAgentCat(b, Project, null, fallback));
        await h.StartAsync(null, withServices: true);

        using var fake = AgentCatSdk.Fake();
        var tools = (await h.ListToolsAsync()).Tools;
        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        Assert.False(EngineInstaller.IsInstalled(h.ServerOptions));
        Assert.DoesNotContain(tools, t => t.Name == "get_more_tools");
        Assert.Equal(NoMatchingToolCall + Hint, await FailureOf(() => fake.AssertTrackedAsync(cancellationToken: Token)));
        Assert.Equal("Failed asserting that 1 tool call(s) were tracked: 0 were." + Hint, await FailureOf(() => fake.AssertTrackedCountAsync(1, Token)));
        Assert.Empty(await fake.GetLogsAsync(Token));
    }

    [Fact]
    public async Task Reads_honor_the_cancellation_token()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fake = AgentCatSdk.Fake();
        await using var h = await TrackedAsync(configure: o => o.Identify = async (_, _) =>
        {
            await release.Task;
            return null;
        });
        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.GetEventsAsync(cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.AssertNothingTrackedAsync(cancelled.Token));
        }

        using (var later = new CancellationTokenSource())
        {
            var logs = fake.GetLogsAsync(later.Token);
            await Task.Delay(50, Token);
            Assert.False(logs.IsCompleted, "The read did not wait for the Identify hook.");
            later.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(logs));
        }

        release.SetResult(true);
        await Bounded(fake.AssertTrackedCountAsync(1, Token));
    }

    // A WithAgentCat server that builds new server options for every request, as stateless HTTP does, and serves one
    // tools/call on each.
    private sealed class StatelessServer : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        internal StatelessServer(Func<AgentCatRuntime> fallback)
        {
            var services = new ServiceCollection();
            var echo = McpServerTool.Create((string msg) => "echo:" + msg, new McpServerToolCreateOptions { Name = "echo" });
            var builder = services.AddMcpServer().WithTools(new[] { echo });
            AgentCatMcpServerBuilderExtensions.WithAgentCat(builder, Project, null, fallback);
            _provider = services.BuildServiceProvider();
        }

        internal async Task CallAsync(string msg)
        {
            var options = _provider.GetRequiredService<IOptionsFactory<McpServerOptions>>().Create(Microsoft.Extensions.Options.Options.DefaultName);
            await using var h = new McpHarness().UseOptions(options);
            await h.StartAsync();
            var result = await h.CallToolAsync("echo", "{\"msg\":\"" + msg + "\"}");
            Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo:" + msg);
        }

        // One request that lists the tools and one that calls echo, each on new server options.
        internal async Task<(IList<Tool> Tools, CallToolResult Result)> ListAndCallAsync(string msg)
        {
            IList<Tool> tools;
            await using (var listing = await StartAsync())
            {
                tools = (await listing.ListToolsAsync()).Tools;
            }

            await using var call = await StartAsync();
            return (tools, await call.CallToolAsync("echo", "{\"msg\":\"" + msg + "\"}"));
        }

        private async Task<McpHarness> StartAsync()
        {
            var options = _provider.GetRequiredService<IOptionsFactory<McpServerOptions>>().Create(Microsoft.Extensions.Options.Options.DefaultName);
            var h = new McpHarness().UseOptions(options);
            await h.StartAsync();
            return h;
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    private sealed class CountingExporter : IEventExporter
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task ExportAsync(AgentCatEvent e, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }
    }

    // Sets an environment variable for one test and puts the earlier value back.
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        internal EnvironmentScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
