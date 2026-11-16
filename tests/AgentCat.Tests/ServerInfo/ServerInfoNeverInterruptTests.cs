#pragma warning disable CA1861
using AgentCat.Tests.TestSupport;
using ModelContextProtocol.Protocol;

namespace AgentCat.Tests.ServerInfo;

// Each case watches the whole process: both console streams, the task exceptions that nothing observed and the
// exceptions that nothing handled. So these tests run alone.
[Collection(SharedRuntime.Name)]
public class ServerInfoNeverInterruptTests
{
    private const string Boom = "heartbeat-test-boom";

    private sealed class ProcessWatch : IDisposable
    {
        private readonly TextWriter _out = Console.Out;
        private readonly TextWriter _error = Console.Error;
        private readonly StringWriter _capturedOut = new();
        private readonly StringWriter _capturedError = new();
        private readonly List<Exception> _unobserved = new();
        private readonly List<object> _unhandled = new();

        internal ProcessWatch()
        {
            // What earlier tests left behind is finalized now, before the watch starts.
            Collect();
            Console.SetOut(TextWriter.Synchronized(_capturedOut));
            Console.SetError(TextWriter.Synchronized(_capturedError));
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        }

        // Call when the heartbeat work has ended.
        internal void AssertQuiet()
        {
            Collect();
            lock (_unobserved)
            {
                Assert.Empty(_unobserved);
                Assert.Empty(_unhandled);
            }

            Console.Out.Flush();
            Console.Error.Flush();
            Assert.Equal("", _capturedOut.ToString());
            Assert.Equal("", _capturedError.ToString());
        }

        public void Dispose()
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            Console.SetOut(_out);
            Console.SetError(_error);
        }

        private static void Collect()
        {
            for (var i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (_unobserved)
            {
                _unobserved.Add(e.Exception);
            }
        }

        private void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            lock (_unobserved)
            {
                _unhandled.Add(e.ExceptionObject);
            }
        }
    }

    private static string TextOf(CallToolResult result) => HeartbeatLab.TextOf(result);

    [Fact]
    public async Task V10_an_api_that_never_answers_does_not_hold_the_tool_call()
    {
        using var watch = new ProcessWatch();
        await using var lab = new HeartbeatLab();
        lab.Api.Respond(RecordingServerInfoSender.Hang);
        lab.Heartbeat.SendTimeout = TimeSpan.FromMilliseconds(200);
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        var result = await server.CallToolAsync("alpha");
        await lab.Api.WaitForAsync(1);
        var next = await server.CallToolAsync("gamma");

        Assert.Equal("ok", TextOf(result));
        Assert.Equal("ok", TextOf(next));
        await lab.WaitForLineAsync("Server info heartbeat not sent: the send failed: the API did not answer within 200 ms");
        Assert.Equal(1, lab.Api.Count);
        Assert.Equal(1, lab.Heartbeat.Guard.Begun);
        watch.AssertQuiet();
    }

    [Fact]
    public async Task V10_failing_list_handlers_cost_only_their_lists_and_the_tool_call_gets_its_result()
    {
        using var watch = new ProcessWatch();
        await using var lab = new HeartbeatLab();
        var harness = HeartbeatLab.DemoServer().Configure(o =>
        {
            HeartbeatLab.SetPrompts(o, (_, _) => new ValueTask<ListPromptsResult>(Task.FromException<ListPromptsResult>(new InvalidOperationException(Boom))));
            o.Handlers.ListResourcesHandler = (_, _) => throw new InvalidOperationException(Boom);
        });
        var server = await lab.StartAsync(harness);

        var result = await server.CallToolAsync("alpha");

        Assert.Equal("ok", TextOf(result));
        var features = (await lab.SingleBodyAsync())["features"]!.AsObject();
        Assert.Equal(new[] { "tools", "resourceTemplates" }, features.Select(p => p.Key).ToArray());
        watch.AssertQuiet();
    }

    [Fact]
    public async Task V10_a_failing_tool_list_handler_sends_nothing_and_the_tool_call_gets_its_result()
    {
        using var watch = new ProcessWatch();
        await using var lab = new HeartbeatLab();
        var failing = false;
        var harness = HeartbeatLab.DemoServer().Configure(o =>
        {
            var list = o.Handlers.ListToolsHandler!;
            o.Handlers.ListToolsHandler = (context, token)
                => Volatile.Read(ref failing) ? throw new InvalidOperationException(Boom) : list(context, token);
        });
        var server = await lab.StartAsync(harness);
        await lab.FirstHeartbeatAsync(server);

        Volatile.Write(ref failing, true);
        var result = await server.CallToolAsync("alpha");
        await lab.IdleAsync();

        Assert.Equal("ok", TextOf(result));
        Assert.Equal(1, lab.Api.Count);
        Assert.Equal("Server info heartbeat not sent: the tools list failed: InvalidOperationException", lab.HeartbeatLines().Last());
        watch.AssertQuiet();
    }

    [Fact]
    public async Task V10_a_list_handler_that_never_returns_does_not_hold_the_tool_call()
    {
        using var watch = new ProcessWatch();
        await using var lab = new HeartbeatLab();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<ListPromptsResult>();
        var harness = HeartbeatLab.DemoServer().Configure(o => HeartbeatLab.SetPrompts(o, (_, _) =>
        {
            entered.TrySetResult(true);
            return new ValueTask<ListPromptsResult>(never.Task);
        }));
        lab.Heartbeat.ListTimeout = TimeSpan.FromMilliseconds(200);
        var server = await lab.StartAsync(harness);

        var result = await server.CallToolAsync("alpha");

        Assert.Equal("ok", TextOf(result));
        await FakeAgentCatApi.WithinAsync(entered.Task, FakeAgentCatApi.WaitBound);
        var features = (await lab.SingleBodyAsync())["features"]!.AsObject();
        Assert.False(features.ContainsKey("prompts"));
        Assert.Equal(4, features["tools"]!.AsArray().Count);
        Assert.Contains("Server info heartbeat leaves out the prompts list: prompts/list did not finish within 200 ms", lab.Log.Lines);
        watch.AssertQuiet();
    }

    [Fact]
    public async Task V10_a_list_handler_that_blocks_its_thread_does_not_hold_a_tool_call()
    {
        using var watch = new ProcessWatch();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lab = new HeartbeatLab();
        try
        {
            var harness = HeartbeatLab.DemoServer().Configure(o => HeartbeatLab.SetPrompts(o, (_, _) =>
            {
                entered.TrySetResult(true);
                release.Wait(CancellationToken.None);
                return new ValueTask<ListPromptsResult>(HeartbeatLab.Json<ListPromptsResult>("{\"prompts\":[]}"));
            }));
            var server = await lab.StartAsync(harness);

            var first = await server.CallToolAsync("alpha");
            await FakeAgentCatApi.WithinAsync(entered.Task, FakeAgentCatApi.WaitBound);
            var whileBlocked = await server.CallToolAsync("gamma");
            var listed = await server.ListToolsAsync();

            Assert.Equal("ok", TextOf(first));
            Assert.Equal("ok", TextOf(whileBlocked));
            Assert.Equal(4, listed.Tools.Count);
            Assert.Equal(1, lab.Heartbeat.Guard.Begun);
            Assert.Equal(0, lab.Api.Count);
        }
        finally
        {
            release.Set();
        }

        await lab.SingleBodyAsync();
        watch.AssertQuiet();
    }

    [Fact]
    public async Task V10_the_tool_call_returns_while_the_tool_list_of_the_heartbeat_is_still_running()
    {
        using var watch = new ProcessWatch();
        await using var lab = new HeartbeatLab();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = false;
        var harness = HeartbeatLab.DemoServer().Configure(o =>
        {
            var list = o.Handlers.ListToolsHandler!;
            o.Handlers.ListToolsHandler = async (context, token) =>
            {
                if (Volatile.Read(ref holding))
                {
                    entered.TrySetResult(true);
                    await release.Task;
                }

                return await list(context, token);
            };
        });
        var server = await lab.StartAsync(harness);
        await lab.FirstHeartbeatAsync(server);

        Volatile.Write(ref holding, true);
        var result = await server.CallToolAsync("alpha");

        Assert.Equal("ok", TextOf(result));
        await FakeAgentCatApi.WithinAsync(entered.Task, FakeAgentCatApi.WaitBound);
        Assert.Equal(2, lab.Heartbeat.Guard.Begun);
        Assert.Equal(1, lab.Api.Count);
        release.SetResult(true);
        Assert.Equal(2, (await lab.Api.WaitForAsync(2)).Count);
        await lab.IdleAsync();
        watch.AssertQuiet();
    }

    [Fact]
    public async Task V10_a_failing_redaction_function_sends_nothing_and_the_tool_call_gets_its_result()
    {
        using var watch = new ProcessWatch();
        await using var lab = new HeartbeatLab();
        var options = new AgentCatOptions
        {
            RedactSensitiveInformation = (text, _) => text.Length > 1000 ? throw new InvalidOperationException(Boom) : new ValueTask<string>(text),
        };
        var server = await lab.StartAsync(HeartbeatLab.DemoServer(), options: options);

        var result = await server.CallToolAsync("alpha");
        await lab.IdleAsync();

        Assert.Equal("ok", TextOf(result));
        Assert.Equal(0, lab.Api.Count);
        Assert.Equal("Server info heartbeat not sent: the redaction function failed: InvalidOperationException", Assert.Single(lab.HeartbeatLines()));
        watch.AssertQuiet();
    }

    [Fact]
    public async Task V10_a_500_ends_the_attempt_without_a_second_post()
    {
        using var watch = new ProcessWatch();
        await using var lab = new HeartbeatLab();
        lab.Api.Respond(500);
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        var result = await server.CallToolAsync("alpha");
        await lab.IdleAsync();
        var next = await server.CallToolAsync("gamma");
        await server.ListToolsAsync();

        Assert.Equal("ok", TextOf(result));
        Assert.Equal("ok", TextOf(next));
        Assert.Equal(1, lab.Api.Count);
        Assert.Equal(1, lab.Heartbeat.Guard.Begun);
        Assert.Equal("Server info heartbeat not sent: HTTP 500", Assert.Single(lab.HeartbeatLines()));
        watch.AssertQuiet();
    }

    [Fact]
    public async Task V10_the_whole_build_has_a_time_limit()
    {
        using var watch = new ProcessWatch();
        await using var lab = new HeartbeatLab();
        var never = new TaskCompletionSource<string>();
        lab.Heartbeat.BuildTimeout = TimeSpan.FromMilliseconds(100);
        var options = new AgentCatOptions { RedactSensitiveInformation = (_, _) => new ValueTask<string>(never.Task) };
        var server = await lab.StartAsync(HeartbeatLab.DemoServer(), options: options);

        var result = await server.ListToolsAsync();
        await lab.IdleAsync();

        Assert.Equal(4, result.Tools.Count);
        Assert.Equal(0, lab.Api.Count);
        Assert.Equal(
            "Server info heartbeat not sent: the lists and the redaction did not finish within 100 ms",
            Assert.Single(lab.HeartbeatLines()));
        watch.AssertQuiet();
    }
}
