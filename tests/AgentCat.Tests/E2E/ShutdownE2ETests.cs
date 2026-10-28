using System.Diagnostics;
using System.Globalization;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol.Protocol;

namespace AgentCat.Tests.E2E;

// A stdio server ended by a signal or by a closed stdin, as a supervisor, `docker stop` or an exiting client ends it.
// The test process records AgentCat's shutdown handlers instead of attaching them, so only these tests run the real
// hosted flush, ProcessExit handler and signal handlers.
public class ShutdownE2ETests
{
    private const string PosixOnly = "POSIX signals: the signal tests run on Linux and macOS only.";

    private const string ExitTimeOnPosix = "The exit-time tests run on Linux and macOS only.";

    // How long the fake AgentCat API holds its answer, so that the event's POST is still waiting when the server
    // is told to stop.
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);

    // How long the slow tool waits. The host stop cancels it at once, so only a failing run waits this long.
    private static readonly TimeSpan SlowCall = TimeSpan.FromSeconds(5);

    // The whole stop waits about 6 s at most with an API that never answers (5 s for events, then at most 1 s for
    // diagnostics, which the test servers turn off), however many stop paths run. The rest is process overhead.
    private static readonly TimeSpan ExitBudget = TimeSpan.FromSeconds(7.5);

    // SIGTERM while a call runs, on a server with a .NET host. The host stops gracefully, the call ends during the
    // stop, and the flush after the stop sends its event.
    [Fact]
    public async Task Sigterm_during_a_call_delivers_its_event()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), PosixOnly);
        using var api = new FakeAgentCatApi();
        await using var server = StdioServerProcess.Start(api);
        await server.ConnectAsync();
        var call = server.CallToolAsync("""{"name":"slow","arguments":{"ms":5000,"context":"stopped during the call"}}""");
        await server.WaitForStderrAsync(E2EToolkit.SlowStartedLine, LaneInstance.CallBound);

        await server.SignalAsync("TERM");
        var signalled = DateTimeOffset.UtcNow;
        await server.WaitForExitAsync();

        Assert.Equal(0, server.ExitCode);
        await Ended(call);
        var sent = Assert.Single(api.Requests.Select(WireEvent.Parse));
        Assert.Equal("slow", sent.ResourceName);
        Assert.Equal("stopped during the call", sent.UserIntent);

        // The event's timestamp is the start of the call: the signal came while the tool still waited.
        Assert.True(signalled < sent.Timestamp + SlowCall, "SIGTERM came at " + signalled.ToString("O", CultureInfo.InvariantCulture) + ", after the call: " + sent);
        Assert.Single(server.LogMessages(), m => m.StartsWith(ExpectedLog.SentPrefix + sent.Id + " |", StringComparison.Ordinal));
    }

    // A server without a .NET host gets SIGTERM after a call, while the call's event waits for the API's answer.
    // AgentCat's signal handler waits for the answer, then the default action ends the process with 143 (128 +
    // SIGTERM). .NET 10 raises no ProcessExit on SIGTERM, so only the signal handler sends there.
    [Fact]
    public async Task Sigterm_without_a_host_delivers_queued_events()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), PosixOnly);
        using var api = new FakeAgentCatApi { AnswerDelay = Hold };
        await using var server = StdioServerProcess.Start(api, NoHost);
        var sent = await QueueOneEventAsync(server, api, "queued at the signal");
        var sentLine = ExpectedLog.SentPrefix + sent.Id + " |";
        Assert.DoesNotContain(server.LogMessages(), m => m.StartsWith(sentLine, StringComparison.Ordinal));

        await server.SignalAsync("TERM");
        var signalled = DateTimeOffset.UtcNow;
        await server.WaitForExitAsync();

        // The POST started after the call, so its answer came after the event's timestamp plus the hold: the signal came
        // before the answer, and only a handler that waited can have written the success line.
        Assert.True(signalled < sent.Timestamp + Hold, "SIGTERM came at " + signalled.ToString("O", CultureInfo.InvariantCulture) + ", after the held answer: " + sent);
        Assert.Equal(143, server.ExitCode);
        Assert.Single(api.Requests);
        Assert.Equal("echo", sent.ResourceName);
        var messages = server.LogMessages();
        Assert.Single(messages, m => m.StartsWith("AgentCat setup started | project " + E2EToolkit.ProjectId + " | sdk v" + Lanes.McpMajor + "/options |", StringComparison.Ordinal));
        Assert.Single(messages, m => m.StartsWith(sentLine, StringComparison.Ordinal));
    }

    // stdin closes right after a call, while the call's event waits for the API's answer: the host stops, and the
    // process exits only after the answer. The API answers only after the host's stop has started, so without the
    // process-exit handler, only the flush after the host stop can wait for it.
    [Fact]
    public async Task Stdin_close_flushes_the_hosted_server()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var api = new FakeAgentCatApi { AnswerGate = answer.Task };
        await using var server = StdioServerProcess.Start(api, environment => environment[E2EToolkit.ShutdownHandlersVariable] = E2EToolkit.OffValue);
        await server.ConnectAsync();
        E2EAssert.Succeeded(await server.CallToolAsync("""{"name":"echo","arguments":{"msg":"hi","context":"before stdin closed"}}"""));

        await server.CloseStdinAsync();
        await server.WaitForStderrAsync(E2EToolkit.HostStoppingLine, StdioServerProcess.ExitBound);
        answer.SetResult(true);
        await server.WaitForExitAsync();

        Assert.Equal(0, server.ExitCode);
        var sent = Assert.Single(api.Requests.Select(WireEvent.Parse));
        Assert.Equal("echo", sent.ResourceName);
        Assert.Equal("before stdin closed", sent.UserIntent);
        Assert.Single(server.LogMessages(), m => m.StartsWith(ExpectedLog.SentPrefix + sent.Id + " |", StringComparison.Ordinal));
    }

    // stdin closes while the call's event waits for the API's answer, on a server without a .NET host: Main returns
    // at once, and only the process-exit handler can wait for the answer. The API answers only after the process exit
    // has started, so the answer finds a process that only the handler keeps alive, and has its whole stop window.
    [Fact]
    public async Task Stdin_close_without_a_host_drains_at_process_exit()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var api = new FakeAgentCatApi { AnswerGate = answer.Task };
        await using var server = StdioServerProcess.Start(api, NoHost);
        var sent = await QueueOneEventAsync(server, api, "pending at stdin close");
        var sentLine = ExpectedLog.SentPrefix + sent.Id + " |";
        Assert.DoesNotContain(server.LogMessages(), m => m.StartsWith(sentLine, StringComparison.Ordinal));

        await server.CloseStdinAsync();
        await server.WaitForStderrAsync(E2EToolkit.ProcessExitLine, StdioServerProcess.ExitBound);
        answer.SetResult(true);
        await server.WaitForExitAsync();

        Assert.Equal(0, server.ExitCode);
        Assert.Single(api.Requests);
        Assert.Single(server.LogMessages(), m => m.StartsWith(sentLine, StringComparison.Ordinal));
    }

    // The same with DisableShutdownHandlers: nothing waits for the answer, so the process exits without it.
    [Fact]
    public async Task Stdin_close_without_a_host_with_shutdown_handlers_off_does_not_drain()
    {
        using var api = new FakeAgentCatApi { AnswerDelay = Hold };
        await using var server = StdioServerProcess.Start(api, environment =>
        {
            NoHost(environment);
            environment[E2EToolkit.ShutdownHandlersVariable] = E2EToolkit.OffValue;
        });
        var sent = await QueueOneEventAsync(server, api, "pending at stdin close");

        await server.CloseStdinAsync();
        await server.WaitForExitAsync();

        Assert.Equal(0, server.ExitCode);
        Assert.Single(api.Requests);
        Assert.DoesNotContain(server.LogMessages(), m => m.StartsWith(ExpectedLog.SentPrefix + sent.Id + " |", StringComparison.Ordinal));
    }

    // With an API that never answers, the flush after the host stop uses the whole 5 s for events. The process-exit
    // handler that runs next finds the deadline passed and does not wait for events again.
    [Fact]
    public async Task Hosted_stdin_close_with_an_unreachable_api_exits_within_the_budget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), ExitTimeOnPosix);
        using var api = new FakeAgentCatApi { DefaultStatus = FakeAgentCatApi.Hang };
        await using var server = StdioServerProcess.Start(api);
        await QueueOneEventAsync(server, api, "unanswered at stdin close");

        var stop = Stopwatch.StartNew();
        await server.CloseStdinAsync();
        await server.WaitForExitAsync();

        AssertWithinBudget(stop.Elapsed, server);
        Assert.Equal(0, server.ExitCode);
    }

    // The host takes SIGTERM and stops gracefully: the flush after the stop, then the process-exit handler.
    [Fact]
    public async Task Hosted_sigterm_with_an_unreachable_api_exits_within_the_budget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), PosixOnly);
        using var api = new FakeAgentCatApi { DefaultStatus = FakeAgentCatApi.Hang };
        await using var server = StdioServerProcess.Start(api);
        await QueueOneEventAsync(server, api, "unanswered at the signal");

        var stop = Stopwatch.StartNew();
        await server.SignalAsync("TERM");
        await server.WaitForExitAsync();

        AssertWithinBudget(stop.Elapsed, server);
        Assert.Equal(0, server.ExitCode);
    }

    // AgentCat's signal handler, then, on .NET 8, the process-exit handler that SIGTERM raises there.
    [Fact]
    public async Task Sigterm_without_a_host_with_an_unreachable_api_exits_within_the_budget()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), PosixOnly);
        using var api = new FakeAgentCatApi { DefaultStatus = FakeAgentCatApi.Hang };
        await using var server = StdioServerProcess.Start(api, NoHost);
        await QueueOneEventAsync(server, api, "unanswered at the signal");

        var stop = Stopwatch.StartNew();
        await server.SignalAsync("TERM");
        await server.WaitForExitAsync();

        AssertWithinBudget(stop.Elapsed, server);
        Assert.Equal(143, server.ExitCode);
    }

    private static void NoHost(Dictionary<string, string?> environment) => environment[E2EToolkit.HostVariable] = E2EToolkit.NoHostValue;

    // Connects, makes one echo call and waits until its event's POST has reached the API: the event is in flight.
    private static async Task<WireEvent> QueueOneEventAsync(StdioServerProcess server, FakeAgentCatApi api, string context)
    {
        await server.ConnectAsync();
        E2EAssert.Succeeded(await server.CallToolAsync("{\"name\":\"echo\",\"arguments\":{\"msg\":\"hi\",\"context\":\"" + context + "\"}}"));
        await api.WaitForRequestsAsync(1, LaneInstance.EventBound);
        var sent = WireEvent.Parse(api.Requests[0], 0);
        Assert.Equal(context, sent.UserIntent);
        return sent;
    }

    internal static void AssertWithinBudget(TimeSpan elapsed, StdioServerProcess server)
    {
        TestContext.Current.TestOutputHelper?.WriteLine("exit after " + elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s");
        Assert.True(elapsed < ExitBudget, "The server exited " + elapsed + " after the stop started, past " + ExitBudget + ". Its stderr:\n" + server.StderrText());
    }

    // The client's call ends when the server exits: with a result, or with the error of a closed connection.
    private static async Task Ended(Task<CallToolResult> call)
    {
        await FakeAgentCatApi.WithinAsync(call.ContinueWith(_ => { }, TaskScheduler.Default), StdioServerProcess.ExitBound);
        _ = call.Exception;
    }
}
