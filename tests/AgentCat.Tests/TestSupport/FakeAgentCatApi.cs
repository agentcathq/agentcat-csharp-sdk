using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgentCat.Tests.TestSupport;

#pragma warning disable CA1835

internal sealed class RecordedRequest
{
    internal RecordedRequest(string method, string path, IReadOnlyList<KeyValuePair<string, string>> headers, byte[] body, bool hadContentLength, string rawHead)
    {
        Method = method;
        Path = path;
        Headers = headers;
        Body = body;
        HadContentLength = hadContentLength;
        RawHead = rawHead;
    }

    internal string Method { get; }

    internal string Path { get; }

    internal IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    internal byte[] Body { get; }

    internal bool HadContentLength { get; }

    internal string RawHead { get; }

    internal string? Header(string name)
    {
        foreach (var header in Headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }
}

internal sealed class FakeAgentCatApi : IDisposable
{
    internal static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _disposed = new();
    private readonly object _gate = new();
    private readonly Queue<int> _script = new();
    private readonly List<RecordedRequest> _requests = new();
    private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _waiters = new();
    private readonly List<TcpClient> _clients = new();

    internal const int Hang = -1;

    private static readonly string[] CrLf = { "\r\n" };

    internal FakeAgentCatApi()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    internal int Port { get; }

    internal string BaseUrl => "http://127.0.0.1:" + Port;

    internal int DefaultStatus { get; set; } = 202;

    // How long the fake holds each answer after it has recorded the request. Set it before the first request.
    internal TimeSpan AnswerDelay { get; set; }

    // When set, the fake also holds each answer until this task completes. Set it before the first request.
    internal Task? AnswerGate { get; set; }

    internal IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    internal FakeAgentCatApi Respond(params int[] statuses)
    {
        lock (_gate)
        {
            foreach (var status in statuses)
            {
                _script.Enqueue(status);
            }
        }

        return this;
    }

    internal Task<IReadOnlyList<RecordedRequest>> WaitForRequestsAsync(int count) => WaitForRequestsAsync(count, WaitBound);

    internal async Task<IReadOnlyList<RecordedRequest>> WaitForRequestsAsync(int count, TimeSpan bound)
    {
        TaskCompletionSource<bool> signal;
        lock (_gate)
        {
            if (_requests.Count >= count)
            {
                return _requests.ToArray();
            }

            signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, signal));
        }

        await WithinAsync(signal.Task, bound).ConfigureAwait(false);
        return Requests;
    }

    internal static async Task WithinAsync(Task task, TimeSpan bound)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(task, Task.Delay(bound, stop.Token)).ConfigureAwait(false);
        if (finished != task)
        {
            throw new TimeoutException("Task did not finish within " + bound);
        }

        stop.Cancel();
    }

    public void Dispose()
    {
        _disposed.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        lock (_gate)
        {
            foreach (var client in _clients)
            {
                client.Close();
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_disposed.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            lock (_gate)
            {
                _clients.Add(client);
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            int status;
            lock (_gate)
            {
                _requests.Add(request);
                status = _script.Count > 0 ? _script.Dequeue() : DefaultStatus;
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_requests.Count >= _waiters[i].Count)
                    {
                        _waiters[i].Signal.TrySetResult(true);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            if (status == Hang)
            {
                await Task.Delay(Timeout.Infinite, _disposed.Token).ConfigureAwait(false);
                return;
            }

            if (AnswerDelay > TimeSpan.Zero)
            {
                await Task.Delay(AnswerDelay, _disposed.Token).ConfigureAwait(false);
            }

            if (AnswerGate is { } gate)
            {
                await Task.WhenAny(gate, Task.Delay(Timeout.Infinite, _disposed.Token)).ConfigureAwait(false);
                _disposed.Token.ThrowIfCancellationRequested();
            }

            var reply = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(reply, 0, reply.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            client.Close();
        }
    }

    private static async Task<RecordedRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var received = new MemoryStream();
        var chunk = new byte[4096];
        var headEnd = -1;
        while (headEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            received.Write(chunk, 0, read);
            headEnd = IndexOfBlankLine(received.GetBuffer(), (int)received.Length);
        }

        var buffer = received.GetBuffer();
        var rawHead = Encoding.ASCII.GetString(buffer, 0, headEnd);
        var lines = rawHead.Split(CrLf, StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        var headers = new List<KeyValuePair<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            headers.Add(new KeyValuePair<string, string>(lines[i].Substring(0, colon), lines[i].Substring(colon + 1).Trim()));
        }

        var hadContentLength = false;
        var contentLength = 0;
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                hadContentLength = true;
                contentLength = int.Parse(header.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        var bodyStart = headEnd + 4;
        var body = new byte[contentLength];
        var have = Math.Min(contentLength, (int)received.Length - bodyStart);
        Array.Copy(buffer, bodyStart, body, 0, have);
        while (have < contentLength)
        {
            var read = await stream.ReadAsync(body, have, contentLength - have).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            have += read;
        }

        return new RecordedRequest(requestLine[0], requestLine[1], headers, body, hadContentLength, rawHead);
    }

    private static int IndexOfBlankLine(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }
}
