using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.PublicApi;

public class WithAgentCatTests
{
    private const string AlreadyTracked = "[SESSION DEBUG] track() - Server already being tracked, skipping initialization";

    private const string FailedToTrack = "Warning: Failed to track server - ";

    private const string NoProjectWarning = "Warning: No projectId provided and no exporters configured. Events will not be sent anywhere.";

    private const string AlternateHandlerWarning =
        "Warning: AgentCat did not instrument this server: Handlers.CallToolWithAlternateHandler is set, and McpServer.Create rejects tools/call filters with it.";

    public static TheoryData<string?> Protocols => McpHarness.ProtocolVersions;

    private static string Started(string project)
        => $"AgentCat setup started | project {project} | sdk v{McpHarness.McpMajor}/di | signals filters";

    private static string Complete(string project, bool tracing = true)
        => $"AgentCat setup complete | project {project} | tracing={(tracing ? "true" : "false")} context=true reportMissing=true exporters=0";

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task WithAgentCat_installs_on_the_DI_options(string? protocolVersion)
    {
        using var rt = new TestRuntime();
        await using var h = new McpHarness();
        h.AddTool((string msg) => "echo:" + msg, "echo");
        h.ConfigureServices(s => s.AddSingleton(rt.Runtime));
        h.ConfigureBuilder(b => b.WithAgentCat("proj_test"));

        await h.StartAsync(protocolVersion, withServices: true);

        Assert.Equal(new[] { Started("proj_test"), Complete("proj_test") }, rt.Log.Lines);
        Assert.True(EngineInstaller.TryGetTracker(h.ServerOptions, out var tracker));
        AssertSeams(h.ServerOptions, tracker);

        var tools = (await h.ListToolsAsync()).Tools;
        Assert.Single(tools, t => t.Name == "get_more_tools");
        Assert.True(HasContext(Assert.Single(tools, t => t.Name == "echo")));
        var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"di install"}""");
        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo:hi");
        Assert.Single(result.Content, c => c is TextContentBlock t && t.Text.StartsWith("[session_id", StringComparison.Ordinal));

        var e = Assert.Single(await rt.FlushAsync());
        Assert.Equal("proj_test", e.ProjectId);
        Assert.Equal("mcp:tools/call", e.EventType);
        Assert.Equal("echo", e.ResourceName);
        Assert.Equal("di install", e.UserIntent);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task WithAgentCat_before_WithTools_still_sees_the_tools(string? protocolVersion)
    {
        McpRequestFilter<CallToolRequestParams, CallToolResult> userFilter = next => next;
        using var rt = new TestRuntime();
        await using var h = new McpHarness();
        h.ConfigureServices(s => s.AddSingleton(rt.Runtime));
        h.ConfigureBuilder(b =>
        {
            b.WithAgentCat("proj_test").WithTools<EchoTools>();
            b.Services.Configure<McpServerOptions>(o => o.Filters.Request.CallToolFilters.Insert(0, userFilter));
        });

        await h.StartAsync(protocolVersion, withServices: true);

        Assert.True(EngineInstaller.TryGetTracker(h.ServerOptions, out var tracker));
        Assert.Collection(
            h.ServerOptions.Filters.Request.CallToolFilters,
            f => Assert.Same(tracker.Seams.Outer, f),
            f => Assert.Same(userFilter, f),
            f => Assert.Same(tracker.Seams.Inner, f));
        var tools = (await h.ListToolsAsync()).Tools;
        Assert.Single(tools, t => t.Name == "get_more_tools");
        Assert.True(HasContext(Assert.Single(tools, t => t.Name == "echo")));
        var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"tools after install"}""");
        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo:hi");

        var e = Assert.Single(await rt.FlushAsync());
        Assert.Equal("echo", e.ResourceName);
        Assert.Equal("tools after install", e.UserIntent);
    }

    [Fact]
    public async Task One_tracker_per_WithAgentCat_call_on_every_options_instance()
    {
        using var rt = new TestRuntime();
        await using var provider = Provider(rt, b => b.WithAgentCat("proj_test"));
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();

        var instances = new[]
        {
            factory.Create(""),
            factory.Create(""),
            factory.Create(""),
            provider.GetRequiredService<IOptions<McpServerOptions>>().Value,
        };

        Assert.Equal(instances.Length, instances.Distinct(ReferenceComparer.Instance).Count());
        Assert.True(EngineInstaller.TryGetTracker(instances[0], out var tracker));
        Assert.Equal("proj_test", tracker.ProjectId);
        Assert.All(instances, o => AssertSeams(o, tracker));
        Assert.Equal(new[] { Started("proj_test"), Complete("proj_test") }, rt.Log.Lines);
    }

    [Fact]
    public async Task WithAgentCat_with_an_empty_project_is_telemetry_only_and_warns_once()
    {
        using var rt = new TestRuntime();
        await using var provider = Provider(rt, b => b.WithAgentCat(""));
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();

        var instances = new[] { factory.Create(""), factory.Create(""), factory.Create("") };

        Assert.True(EngineInstaller.TryGetTracker(instances[0], out var tracker));
        Assert.Equal("", tracker.ProjectId);
        Assert.All(instances, o => AssertSeams(o, tracker));
        Assert.Equal(new[] { Started("(telemetry-only)"), NoProjectWarning, Complete("(telemetry-only)") }, rt.Log.Lines);
    }

    // Stateless HTTP builds an options instance for every POST, on concurrent requests.
    [Fact]
    public async Task Stateless_install_logs_once_and_reuses_the_seams()
    {
        const int Instances = 200;
        const int Threads = 8;
        var configureCalls = 0;
        var allArrived = false;
        using var arrived = new CountdownEvent(Threads);
        using var rt = new TestRuntime();

        // The decision waits until every thread is about to create its first instance, so the others queue behind it.
        await using var provider = Provider(rt, b => b.WithAgentCat("proj_test", _ =>
        {
            Interlocked.Increment(ref configureCalls);
            allArrived = arrived.Wait(FakeAgentCatApi.WaitBound);
        }));
        var created = new McpServerOptions[Instances];
        var failures = new ConcurrentQueue<Exception>();
        var factories = Enumerable.Range(0, Threads).Select(_ => provider.GetRequiredService<IOptionsFactory<McpServerOptions>>()).ToArray();

        var threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            try
            {
                arrived.Signal();
                for (var i = t; i < Instances; i += Threads)
                {
                    created[i] = factories[t].Create("");
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        })).ToArray();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Empty(failures);
        Assert.True(allArrived);
        Assert.Equal(1, configureCalls);
        Assert.Equal(new[] { Started("proj_test"), Complete("proj_test") }, rt.Log.Lines);
        Assert.True(EngineInstaller.TryGetTracker(created[0], out var tracker));
        Assert.All(created, o => AssertSeams(o, tracker));
    }

    [Fact]
    public async Task Configure_that_builds_server_options_does_not_recurse()
    {
        var calls = 0;
        IServiceProvider? captured = null;
        McpServerOptions? nested = null;
        using var rt = new TestRuntime();
        await using var provider = Provider(rt, b => b.WithAgentCat("proj_test", _ =>
        {
            if (Interlocked.Increment(ref calls) < 5)
            {
                nested = captured!.GetRequiredService<IOptionsFactory<McpServerOptions>>().Create("");
            }
        }));
        captured = provider;
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();

        var outer = factory.Create("");
        var later = factory.Create("");

        Assert.Equal(1, calls);
        Assert.NotNull(nested);
        AssertUntouched(nested);
        Assert.True(EngineInstaller.TryGetTracker(outer, out var tracker));
        AssertSeams(outer, tracker);
        AssertSeams(later, tracker);
        Assert.Equal(new[] { Started("proj_test"), Complete("proj_test") }, rt.Log.Lines);
    }

    [Fact]
    public void Registration_failure_logs_and_does_not_throw()
    {
        using var rt = new TestRuntime();
        var services = new ServiceCollection();
        var builder = services.AddMcpServer();
        services.MakeReadOnly();

        IMcpServerBuilder? returned = null;
        var thrown = Record.Exception(() => returned = AgentCatMcpServerBuilderExtensions.WithAgentCat(builder, "proj_a", null, () => rt.Runtime));

        Assert.Null(thrown);
        Assert.Same(builder, returned);
        Assert.StartsWith(FailedToTrack + "InvalidOperationException: ", Assert.Single(rt.Log.Lines), StringComparison.Ordinal);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAgentCat));

        var noRuntime = new AgentCatPostConfigure("proj_a", null, () => throw new InvalidOperationException("no runtime"), () => rt.Runtime);
        var options = new McpServerOptions();
        Assert.Null(Record.Exception(() =>
        {
            noRuntime.PostConfigure("", options);
            noRuntime.PostConfigure("", new McpServerOptions());
        }));
        AssertUntouched(options);
        Assert.Equal(FailedToTrack + "InvalidOperationException: no runtime", rt.Log.Lines[1]);
        Assert.Equal(2, rt.Log.Lines.Count);

        var unreachable = new AgentCatPostConfigure("proj_a", null, () => throw new InvalidOperationException("no runtime"), () => throw new InvalidOperationException("no fallback"));
        Assert.Null(Record.Exception(() => unreachable.PostConfigure("", new McpServerOptions())));
        Assert.Null(Record.Exception(() => AgentCatMcpServerBuilderExtensions.WithAgentCat(builder, "proj_a", null, () => throw new InvalidOperationException("no fallback"))));
    }

    [Fact]
    public async Task Second_WithAgentCat_applies_its_api_base_url()
    {
        using var firstApi = new FakeAgentCatApi();
        using var secondApi = new FakeAgentCatApi();
        using var sender = new HttpEventSender();
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, sender);
        try
        {
            await using var h = new McpHarness();
            h.AddTool((string msg) => "echo:" + msg, "echo");
            h.ConfigureServices(s => s.AddSingleton(runtime));
            h.ConfigureBuilder(b => b
                .WithAgentCat("proj_first", o => o.ApiBaseUrl = firstApi.BaseUrl)
                .WithAgentCat("proj_second", o => o.ApiBaseUrl = secondApi.BaseUrl));
            await h.StartAsync(withServices: true);

            Assert.Equal(secondApi.BaseUrl + "/v1/public/events/", sender.Endpoint);
            await h.CallToolAsync("echo", """{"msg":"hi","context":"second base url"}""");

            var request = Assert.Single(await secondApi.WaitForRequestsAsync(1));
            Assert.True(await runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
            Assert.Single(secondApi.Requests);
            Assert.Empty(firstApi.Requests);
            var body = JsonNode.Parse(Encoding.UTF8.GetString(request.Body))!.AsObject();
            Assert.Equal("proj_first", (string?)body["project_id"]);
            Assert.Equal("second base url", (string?)body["user_intent"]);
        }
        finally
        {
            runtime.Pipeline.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Second_WithAgentCat_is_skipped_like_a_repeated_track(string? protocolVersion)
    {
        using var rt = new TestRuntime();
        await using var h = new McpHarness();
        h.AddTool((string msg) => "echo:" + msg, "echo");
        h.ConfigureServices(s => s.AddSingleton(rt.Runtime));
        h.ConfigureBuilder(b => b.WithAgentCat("proj_first").WithAgentCat("proj_second", o => o.EnableTracing = false));

        await h.StartAsync(protocolVersion, withServices: true);
        var factory = h.Services.GetRequiredService<IOptionsFactory<McpServerOptions>>();
        var instances = new[] { h.ServerOptions, factory.Create(""), factory.Create("") };

        Assert.Equal(new[] { Started("proj_first"), Complete("proj_first"), Started("proj_second"), AlreadyTracked }, rt.Log.Lines);
        Assert.True(EngineInstaller.TryGetTracker(h.ServerOptions, out var tracker));
        Assert.Equal("proj_first", tracker.ProjectId);
        Assert.All(instances, o => AssertSeams(o, tracker));

        var tools = (await h.ListToolsAsync()).Tools;
        Assert.Single(tools, t => t.Name == "get_more_tools");
        var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"double registration"}""");
        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo:hi");
        Assert.Single(result.Content, c => c is TextContentBlock t && t.Text.StartsWith("[session_id", StringComparison.Ordinal));

        var e = Assert.Single(await rt.FlushAsync());
        Assert.Equal("proj_first", e.ProjectId);
        Assert.Equal("double registration", e.UserIntent);
    }

    // A stateless server runs for weeks and builds an options instance for every POST: none of them may stay reachable.
    [Fact]
    public async Task Stateless_options_instances_are_not_retained()
    {
        using var rt = new TestRuntime();
        await using var provider = Provider(rt, b => b.WithAgentCat("proj_test"));
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();

        var references = await ServeAndDropAsync(factory, rt);

        // The last await can complete inline, which leaves the helper's frames below this one on the stack.
        await Task.Yield();
        Assert.Equal(20 * 2 + 100, references.Count);
        Assert.Empty(await Retention.LiveAfterCollectingAsync(references));
        Assert.Equal(new[] { Started("proj_test"), Complete("proj_test") }, rt.Log.Lines.Where(l => l.StartsWith("AgentCat setup", StringComparison.Ordinal)));
    }

    // While the API is down, tool-call events wait in the queue (up to 10,000). Each keeps the server's name and version,
    // never the options instance of the stateless POST that it came from. Five sends hang, so five events are in process
    // and the rest wait in the queue.
    [Fact]
    public async Task Queued_tool_call_events_do_not_keep_their_options_alive()
    {
        using var rt = new TestRuntime();
        rt.Sender.Hang(times: 5);
        await using var provider = Provider(rt, b =>
        {
            b.WithAgentCat("proj_test");
            b.Services.Configure<McpServerOptions>(o => o.ServerInfo = Info("""{"name":"queued-server","version":"4.5.6"}"""));
        });
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();

        var references = await ServeWhileSendsHangAsync(factory, 20);
        await WaitUntilAsync(() => rt.Sender.HangingCount == 5 && rt.Runtime.Pipeline.QueueLength == 15);

        // The last await can complete inline, which leaves the helper's frames below this one on the stack.
        await Task.Yield();
        var live = await Retention.LiveAfterCollectingAsync(references);
        Assert.Equal(5, rt.Sender.HangingCount);
        Assert.Equal(15, rt.Runtime.Pipeline.QueueLength);
        Assert.Equal(20, references.Count);
        Assert.Empty(live);

        rt.Sender.Release();
        var events = await rt.FlushAsync();
        Assert.Equal(20, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal("queued-server", e.ServerName);
            Assert.Equal("4.5.6", e.ServerVersion);
        });
    }

    [Fact]
    public async Task WithAgentCat_registers_one_IAgentCat_singleton()
    {
        using var rt = new TestRuntime();
        var services = new ServiceCollection();
        services.AddSingleton(rt.Runtime);
        var builder = services.AddMcpServer();
        var postConfigures = services.Count(d => d.ServiceType == typeof(IPostConfigureOptions<McpServerOptions>));

        Assert.Same(builder, builder.WithAgentCat("proj_a"));
        Assert.Same(builder, builder.WithAgentCat("proj_b"));

        Assert.Single(services, d => d.ServiceType == typeof(IAgentCat));
        Assert.Equal(postConfigures + 2, services.Count(d => d.ServiceType == typeof(IPostConfigureOptions<McpServerOptions>)));
        await using var provider = services.BuildServiceProvider();
        var agentCat = provider.GetRequiredService<IAgentCat>();
        Assert.Same(agentCat, provider.GetRequiredService<IAgentCat>());
        Assert.IsType<AgentCatClient>(agentCat);

        await agentCat.PublishCustomEventAsync("ses_di", "proj_di", new CustomEventData { ResourceName = "di-client" });
        Assert.True(await agentCat.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        var e = Assert.Single(rt.Sender.Sent);
        Assert.Equal("ses_di", e.SessionId);
        Assert.Equal("di-client", e.ResourceName);
        Assert.Contains(CustomEvent.Published("ses_di"), rt.Log.Lines);

        var earlier = new AgentCatClient(rt.Runtime);
        var own = new ServiceCollection();
        own.AddSingleton<IAgentCat>(earlier);
        own.AddMcpServer().WithAgentCat("proj_a");
        await using var ownProvider = own.BuildServiceProvider();
        Assert.Same(earlier, ownProvider.GetRequiredService<IAgentCat>());
    }

    [Fact]
    public async Task Configure_runs_once_and_a_throwing_configure_installs_nothing()
    {
        var calls = 0;
        using (var rt = new TestRuntime())
        {
            await using var provider = Provider(rt, b => b.WithAgentCat("proj_test", o =>
            {
                Interlocked.Increment(ref calls);
                o.EnableTracing = false;
            }));
            var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();

            var instances = Enumerable.Range(0, 5).Select(_ => factory.Create("")).ToArray();

            Assert.Equal(1, calls);
            Assert.True(EngineInstaller.TryGetTracker(instances[0], out var tracker));
            Assert.False(tracker.Settings.EnableTracing);
            Assert.All(instances, o => AssertSeams(o, tracker));
            Assert.Equal(new[] { Started("proj_test"), Complete("proj_test", tracing: false) }, rt.Log.Lines);
        }

        using (var rt = new TestRuntime())
        {
            var throwingCalls = 0;
            await using var provider = Provider(rt, b => b.WithAgentCat("proj_test", _ =>
            {
                Interlocked.Increment(ref throwingCalls);
                throw new InvalidOperationException("configure boom");
            }));
            var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();

            var instances = Enumerable.Range(0, 5).Select(_ => factory.Create("")).ToArray();

            Assert.Equal(1, throwingCalls);
            Assert.All(instances, AssertUntouched);
            Assert.Equal(new[] { FailedToTrack + "InvalidOperationException: configure boom" }, rt.Log.Lines);
        }
    }

    [Fact]
    public async Task Alternate_handler_warns_once_per_WithAgentCat_call()
    {
        Assert.SkipWhen(McpHarness.McpMajor < 2, "Handlers.CallToolWithAlternateHandler exists only on ModelContextProtocol 2.x.");
        using var rt = new TestRuntime();
        await using var provider = Provider(rt, b =>
        {
            b.Services.Configure<McpServerOptions>(Mcp.SetAlternateHandler);
            b.WithAgentCat("proj_a").WithAgentCat("proj_b");
        });
        var factory = provider.GetRequiredService<IOptionsFactory<McpServerOptions>>();

        var instances = Enumerable.Range(0, 5).Select(_ => factory.Create("")).ToArray();

        Assert.All(instances, AssertUntouched);
        Assert.Equal(new[] { Started("proj_a"), AlternateHandlerWarning, Started("proj_b"), AlternateHandlerWarning }, rt.Log.Lines);
    }

    [Fact]
    public async Task PostConfigure_drops_the_configure_delegate_once_the_decision_is_made()
    {
        using var rt = new TestRuntime();
        var (postConfigure, captured) = Registration(rt);

        GC.Collect();
        Assert.True(captured.IsAlive, "The object is alive before the decision.");
        postConfigure.PostConfigure("", new McpServerOptions());
        postConfigure.PostConfigure("", new McpServerOptions());

        Assert.Empty(await Retention.LiveAfterCollectingAsync(new[] { ("customer's configure delegate", captured) }));
        GC.KeepAlive(postConfigure);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (AgentCatPostConfigure PostConfigure, WeakReference Captured) Registration(TestRuntime rt)
    {
        var big = new object();
        var postConfigure = new AgentCatPostConfigure("proj_a", Capturing(big), () => rt.Runtime);
        return (postConfigure, new WeakReference(big));
    }

    // A separate method, so the runtime lambda above does not share a closure with the captured object.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Action<AgentCatOptions> Capturing(object big) => o => o.GetHashCode().Equals(big.GetHashCode());

    [Fact]
    public void PostConfigure_never_throws()
    {
        var noRuntime = new AgentCatPostConfigure(
            "proj_a", null, () => throw new InvalidOperationException("no runtime"), () => throw new InvalidOperationException("no fallback"));
        var options = new McpServerOptions();
        Assert.Null(Record.Exception(() =>
        {
            noRuntime.PostConfigure("", options);
            noRuntime.PostConfigure("", new McpServerOptions());
        }));
        AssertUntouched(options);

        // Null options decide nothing: the next instance still installs.
        using (var rt = new TestRuntime())
        {
            var postConfigure = new AgentCatPostConfigure("proj_a", null, () => rt.Runtime);
            var first = new McpServerOptions();
            Assert.Null(Record.Exception(() =>
            {
                postConfigure.PostConfigure("", null!);
                postConfigure.PostConfigure("", first);
            }));
            Assert.True(EngineInstaller.IsInstalled(first));
        }

        // A log that throws on every line: the started line fails before the install.
        using (var rt = new TestRuntime())
        {
            rt.Log.ThrowWhen = _ => true;
            var postConfigure = new AgentCatPostConfigure("proj_a", null, () => rt.Runtime);
            var instances = new[] { new McpServerOptions(), new McpServerOptions() };
            Assert.Null(Record.Exception(() => Array.ForEach(instances, o => postConfigure.PostConfigure("", o))));
            Assert.All(instances, AssertUntouched);
        }

        // A log that throws after the install: the installed tracker still serves every later instance.
        using (var rt = new TestRuntime())
        {
            rt.Log.ThrowWhen = line => line.StartsWith("AgentCat setup complete", StringComparison.Ordinal);
            var postConfigure = new AgentCatPostConfigure("proj_a", null, () => rt.Runtime);
            var instances = new[] { new McpServerOptions(), new McpServerOptions(), new McpServerOptions() };
            Assert.Null(Record.Exception(() => Array.ForEach(instances, o => postConfigure.PostConfigure("", o))));
            Assert.True(EngineInstaller.TryGetTracker(instances[0], out var tracker));
            Assert.All(instances, o => AssertSeams(o, tracker));
        }

        // A throwing configure, and a log that throws on its failure line.
        using (var rt = new TestRuntime())
        {
            rt.Log.ThrowWhen = _ => true;
            var postConfigure = new AgentCatPostConfigure("proj_a", _ => throw new InvalidOperationException("configure boom"), () => rt.Runtime);
            var instance = new McpServerOptions();
            Assert.Null(Record.Exception(() => postConfigure.PostConfigure("", instance)));
            AssertUntouched(instance);
        }

        // A later instance that cannot take the filters, with a log that throws on the failure line.
        using (var rt = new TestRuntime())
        {
            var postConfigure = new AgentCatPostConfigure("proj_a", null, () => rt.Runtime);
            var first = new McpServerOptions();
            postConfigure.PostConfigure("", first);
            rt.Log.ThrowWhen = line => line.StartsWith(FailedToTrack, StringComparison.Ordinal);
            var readOnly = new McpServerOptions();
            readOnly.Filters.Request.CallToolFilters = new ReadOnlyCollection<McpRequestFilter<CallToolRequestParams, CallToolResult>>(
                new List<McpRequestFilter<CallToolRequestParams, CallToolResult>>());
            var later = new McpServerOptions();

            Assert.Null(Record.Exception(() =>
            {
                postConfigure.PostConfigure("", readOnly);
                postConfigure.PostConfigure("", later);
            }));

            Assert.True(EngineInstaller.TryGetTracker(first, out var tracker));
            Assert.False(EngineInstaller.IsInstalled(readOnly));
            AssertSeams(later, tracker);
        }
    }

    [Fact]
    public void Null_builder_throws_ArgumentNullException()
    {
        IMcpServerBuilder builder = null!;

        var thrown = Assert.Throws<ArgumentNullException>(() => builder.WithAgentCat("proj_a"));

        Assert.Equal("builder", thrown.ParamName);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Server_form_custom_event_finds_the_DI_tracker(string? protocolVersion)
    {
        using var rt = new TestRuntime();
        await using var h = new McpHarness();
        h.ConfigureServices(s => s.AddSingleton(rt.Runtime));
        h.ConfigureBuilder(b =>
        {
            b.WithTools<CustomEventTools>().WithAgentCat("proj_test");
            b.Services.Configure<McpServerOptions>(o => o.ServerInfo = Info("""{"name":"di-server","version":"1.2.3"}"""));
        });
        await h.StartAsync(protocolVersion, withServices: true);
        var injected = h.Services.GetRequiredService<IAgentCat>();

        var result = await h.CallToolAsync("custom", """{"sid":"ses_di_custom"}""");

        Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "client:" + RuntimeHelpers.GetHashCode(injected));
        var events = await rt.FlushAsync();
        Assert.Equal(2, events.Count);
        var custom = Assert.Single(events, e => e.EventType == "agentcat:custom");
        Assert.Equal("ses_di_custom", custom.SessionId);
        Assert.Equal("proj_test", custom.ProjectId);
        Assert.Equal("checkout", custom.ResourceName);
        Assert.Equal("paid", custom.UserIntent);
        Assert.Equal("di-server", custom.ServerName);
        Assert.Equal("1.2.3", custom.ServerVersion);
        Assert.Equal("harness-client", custom.ClientName);
        Assert.Single(events, e => e.EventType == "mcp:tools/call" && e.ResourceName == "custom");
        Assert.Contains(CustomEvent.Published("ses_di_custom"), rt.Log.Lines);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<(string Label, WeakReference Reference)>> ServeAndDropAsync(
        IOptionsFactory<McpServerOptions> factory,
        TestRuntime rt)
    {
        var references = new List<(string Label, WeakReference Reference)>();
        for (var i = 0; i < 20; i++)
        {
            var options = factory.Create("");
            await using var h = new McpHarness().UseOptions(options);
            await h.StartAsync();
            references.Add(("served options #" + i, new WeakReference(options)));
            references.Add(("server #" + i, new WeakReference(h.Server)));

            var tools = (await h.ListToolsAsync()).Tools;
            Assert.Single(tools, t => t.Name == "get_more_tools");
            var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"retention"}""");
            Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo:hi");
        }

        for (var i = 0; i < 100; i++)
        {
            var options = factory.Create("");
            Assert.True(EngineInstaller.IsInstalled(options));
            references.Add(("installed options #" + i, new WeakReference(options)));
        }

        Assert.Equal(20, (await rt.FlushAsync()).Count);
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<(string Label, WeakReference Reference)>> ServeWhileSendsHangAsync(IOptionsFactory<McpServerOptions> factory, int servers)
    {
        var references = new List<(string Label, WeakReference Reference)>();
        for (var i = 0; i < servers; i++)
        {
            var options = factory.Create("");
            await using var h = new McpHarness().UseOptions(options);
            await h.StartAsync();
            references.Add(("options #" + i, new WeakReference(options)));

            var result = await h.CallToolAsync("echo", """{"msg":"hi","context":"queued"}""");
            Assert.Contains(result.Content, c => c is TextContentBlock t && t.Text == "echo:hi");
        }

        return references;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static ServiceProvider Provider(TestRuntime rt, Action<IMcpServerBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(rt.Runtime);
        configure(services.AddMcpServer().WithTools(new[]
        {
            McpServerTool.Create((string msg) => "echo:" + msg, new McpServerToolCreateOptions { Name = "echo" }),
        }));
        return services.BuildServiceProvider();
    }

    private static void AssertSeams(McpServerOptions options, AgentCatTracker tracker)
    {
        Assert.True(EngineInstaller.TryGetTracker(options, out var installed));
        Assert.Same(tracker, installed);
        Assert.Same(tracker.Seams.List, Assert.Single(options.Filters.Request.ListToolsFilters));
        Assert.Collection(
            options.Filters.Request.CallToolFilters,
            f => Assert.Same(tracker.Seams.Outer, f),
            f => Assert.Same(tracker.Seams.Inner, f));
        Assert.Same(tracker.Seams.Outgoing, Assert.Single(options.Filters.Message.OutgoingFilters));
    }

    private static void AssertUntouched(McpServerOptions options)
    {
        Assert.False(EngineInstaller.IsInstalled(options));
        Assert.Empty(options.Filters.Request.ListToolsFilters);
        Assert.Empty(options.Filters.Request.CallToolFilters);
        Assert.Empty(options.Filters.Message.OutgoingFilters);
    }

    private static bool HasContext(Tool tool)
        => tool.InputSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("context", out _);

    private static Implementation Info(string json)
        => (Implementation)JsonSerializer.Deserialize(json, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Implementation)))!;

    public sealed class EchoTools
    {
        [McpServerTool(Name = "echo")]
        public static string Echo(string msg) => "echo:" + msg;
    }

    public sealed class CustomEventTools
    {
        [McpServerTool(Name = "custom")]
        public static async Task<string> Custom(McpServer server, IAgentCat agentCat, string sid)
        {
            await agentCat.PublishCustomEventAsync(server, "proj_caller", new CustomEventData
            {
                SessionId = sid,
                ResourceName = "checkout",
                Message = "paid",
            });
            return "client:" + RuntimeHelpers.GetHashCode(agentCat);
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<McpServerOptions>
    {
        internal static readonly ReferenceComparer Instance = new();

        public bool Equals(McpServerOptions? x, McpServerOptions? y) => ReferenceEquals(x, y);

        public int GetHashCode(McpServerOptions obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class TestRuntime : IDisposable
    {
        internal TestRuntime() => Runtime = new AgentCatRuntime(Log, Sender);

        internal RecordingLog Log { get; } = new();

        internal RecordingSender Sender { get; } = new();

        internal AgentCatRuntime Runtime { get; }

        internal async Task<IReadOnlyList<AgentCatEvent>> FlushAsync()
        {
            var flushed = Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound);
            await FakeAgentCatApi.WithinAsync(flushed, FakeAgentCatApi.WaitBound);
            Assert.True(await flushed, "The event pipeline did not drain.");
            return Sender.Sent;
        }

        public void Dispose() => Runtime.Pipeline.Dispose();
    }
}
