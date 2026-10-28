using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;
using AgentCat.Internal.Logging;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentCat.Tests.E2E;

// The stdio server as a process the test starts and ends itself, with a client over the process's own pipes. The C#
// stdio client never closes the server's stdin, kills the server when it disposes and skips stdout lines that are not
// JSON, so only this harness can close stdin, send a signal, read the exit code and see a polluted stdout. The server
// writes its AgentCat log to a file of its own, which the test can read after the exit.
internal sealed class StdioServerProcess : IAsyncDisposable
{
    internal static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(10);

    private static readonly Regex LogLineShape = new(
        @"^\[\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z\] (\[[^\]]*\]) (.*)$",
        RegexOptions.CultureInvariant);

    private readonly Process _process;
    private readonly List<string> _stderr = new();
    private readonly List<(string Line, TaskCompletionSource<bool> Seen)> _stderrWaiters = new();
    private StdoutTee? _stdout;
    private McpClient? _client;

    private StdioServerProcess(Process process, string logPath)
    {
        _process = process;
        LogPath = logPath;
    }

    internal string LogPath { get; }

    internal int ExitCode => _process.ExitCode;

    internal StdoutTee Stdout => _stdout ?? throw new InvalidOperationException("The server did not start.");

    internal McpClient Client => _client ?? throw new InvalidOperationException("No client is connected.");

    internal IReadOnlyList<string> Stderr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToArray();
            }
        }
    }

    // Starts `dotnet <stdio server>` with the stdio lane environment of the scenario and a log file of its own.
    // configure changes the environment before the start; a null value removes the variable from the server's
    // environment only.
    internal static StdioServerProcess Start(
        FakeAgentCatApi api,
        Action<Dictionary<string, string?>>? configure = null,
        string scenario = Scenarios.BaselineDefaults)
    {
        var logPath = Lanes.NewLogPath();
        var environment = Lanes.StdioEnvironment(scenario, api.BaseUrl, handlerTools: false, logPath);
        configure?.Invoke(environment);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Lanes.ServerDll);
        foreach (var variable in environment)
        {
            if (variable.Value is null)
            {
                startInfo.Environment.Remove(variable.Key);
            }
            else
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        var process = new Process { StartInfo = startInfo };
        var server = new StdioServerProcess(process, logPath);
        process.ErrorDataReceived += (_, e) => server.OnStderr(e.Data);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("dotnet did not start.");
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }

        process.BeginErrorReadLine();
        server._stdout = new StdoutTee(process.StandardOutput.BaseStream);
        return server;
    }

    // The lines of a server's AgentCat log; empty when the server wrote none. The server holds the file only while it
    // writes a line, so a read that meets a write tries again.
    internal static IReadOnlyList<string> ReadLog(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllLines(path);
            }
            catch (FileNotFoundException)
            {
                return Array.Empty<string>();
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(10);
            }
        }
    }

    // The messages of a server's AgentCat log, without the time and the version prefix. Every line has the log line
    // shape and the prefix this test process builds: the server loads the same AgentCat, runtime and MCP versions.
    internal static IReadOnlyList<string> LogMessages(string path)
    {
        var messages = new List<string>();
        foreach (var line in ReadLog(path))
        {
            var match = LogLineShape.Match(line);
            Assert.True(match.Success, "Not an AgentCat log line: " + line);
            Assert.Equal(LogLine.Prefix, match.Groups[1].Value);
            messages.Add(match.Groups[2].Value);
        }

        return messages;
    }

    internal static void DeleteLog(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static async Task<T> Bounded<T>(string operation, Func<CancellationToken, Task<T>> call, TimeSpan bound)
    {
        var test = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(test);
        cts.CancelAfter(bound);
        try
        {
            return await call(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !test.IsCancellationRequested)
        {
            throw new TimeoutException(operation + " did not finish within " + bound + ".");
        }
    }

    internal IReadOnlyList<string> LogMessages() => LogMessages(LogPath);

    internal async Task<McpClient> ConnectAsync()
    {
        try
        {
            _client = await Bounded(
                "McpClient.CreateAsync",
                ct => McpClient.CreateAsync(new StreamClientTransport(_process.StandardInput.BaseStream, Stdout.Reader), Lanes.ClientOptions(null), cancellationToken: ct),
                LaneInstance.StdioConnectBound);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The stdio server did not start. Its stderr:\n" + StderrText(), ex);
        }

        return _client;
    }

    internal Task<ListToolsResult> ListToolsAsync()
        => Bounded("tools/list", ct => Client.ListToolsAsync(Mcp.ListParams(null), ct).AsTask(), LaneInstance.CallBound);

    // The params as JSON.
    internal Task<CallToolResult> CallToolAsync(string paramsJson)
        => Bounded("tools/call " + paramsJson, ct => Client.CallToolAsync(Mcp.CallToolParams(paramsJson), ct).AsTask(), LaneInstance.CallBound);

    // Disposes the client, then closes the server's stdin: how an MCP client that exits ends a stdio server.
    internal async Task CloseStdinAsync()
    {
        if (_client is { } client)
        {
            _client = null;
            await client.DisposeAsync();
        }

        _process.StandardInput.Close();
    }

    // Sends the signal with kill(1), as a supervisor or `docker stop` does.
    internal async Task SignalAsync(string signal)
    {
        var startInfo = new ProcessStartInfo("kill")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-" + signal);
        startInfo.ArgumentList.Add(_process.Id.ToString(CultureInfo.InvariantCulture));
        using var kill = Process.Start(startInfo) ?? throw new InvalidOperationException("kill did not start.");
        var error = kill.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await FakeAgentCatApi.WithinAsync(kill.WaitForExitAsync(TestContext.Current.CancellationToken), ExitBound);
        Assert.True(kill.ExitCode == 0, "kill -" + signal + " " + _process.Id + " failed: " + await error);
    }

    internal async Task WaitForExitAsync()
    {
        var test = TestContext.Current.CancellationToken;
        using var exit = CancellationTokenSource.CreateLinkedTokenSource(test);
        exit.CancelAfter(ExitBound);
        try
        {
            await _process.WaitForExitAsync(exit.Token);
        }
        catch (OperationCanceledException) when (!test.IsCancellationRequested)
        {
            Assert.Fail("The server did not exit within " + ExitBound + ". Its stderr:\n" + StderrText());
        }
    }

    // Waits until the server has written the line to stderr.
    internal async Task WaitForStderrAsync(string line, TimeSpan bound)
    {
        TaskCompletionSource<bool> seen;
        lock (_stderr)
        {
            if (_stderr.Contains(line))
            {
                return;
            }

            seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stderrWaiters.Add((line, seen));
        }

        try
        {
            await FakeAgentCatApi.WithinAsync(seen.Task, bound);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("The server did not write \"" + line + "\" to stderr within " + bound + ". Its stderr:\n" + StderrText(), ex);
        }
    }

    internal string StderrText() => string.Join("\n", Stderr);

    public async ValueTask DisposeAsync()
    {
        if (_client is { } client)
        {
            _client = null;
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception)
            {
                // The server may have closed the pipes already.
            }
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
        DeleteLog(LogPath);
    }

    private void OnStderr(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_stderr)
        {
            _stderr.Add(line);
            for (var i = _stderrWaiters.Count - 1; i >= 0; i--)
            {
                if (_stderrWaiters[i].Line == line)
                {
                    _stderrWaiters[i].Seen.TrySetResult(true);
                    _stderrWaiters.RemoveAt(i);
                }
            }
        }
    }
}

// Records every byte the server writes to stdout, and hands the same bytes to the client.
internal sealed class StdoutTee
{
    private readonly List<byte> _recorded = new();
    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));

    internal StdoutTee(Stream stdout)
    {
        Reader = _pipe.Reader.AsStream();
        Completion = PumpAsync(stdout);
    }

    internal Stream Reader { get; }

    // Completes when the server closes stdout.
    internal Task Completion { get; }

    internal string Text
    {
        get
        {
            lock (_recorded)
            {
                return Encoding.UTF8.GetString(_recorded.ToArray());
            }
        }
    }

    private async Task PumpAsync(Stream stdout)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await stdout.ReadAsync(buffer.AsMemory())) > 0)
            {
                lock (_recorded)
                {
                    _recorded.AddRange(new ArraySegment<byte>(buffer, 0, read));
                }

                await _pipe.Writer.WriteAsync(buffer.AsMemory(0, read));
            }

            await _pipe.Writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            await _pipe.Writer.CompleteAsync(ex);
            throw;
        }
    }
}
