#pragma warning disable CA1861, CA1305, CA1872, CA1850, CA1837, CA2016

using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Tests.TestSupport;
using SdkDiagnostics = AgentCat.Internal.Diagnostics.Diagnostics;

namespace AgentCat.Tests.Diagnostics;

internal sealed class PostedRequest
{
    internal PostedRequest(string method, string url, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        Method = method;
        Url = url;
        Headers = headers;
        Body = body;
    }

    internal string Method { get; }

    internal string Url { get; }

    internal IReadOnlyDictionary<string, string> Headers { get; }

    internal byte[] Body { get; }

    internal JsonNode Json => JsonNode.Parse(Body)!;

    internal JsonArray Records => Json["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]!.AsArray();

    internal Dictionary<string, string> ResourceAttributes => Json["resourceLogs"]![0]!["resource"]!["attributes"]!.AsArray()
        .ToDictionary(a => (string)a!["key"]!, a => (string)a!["value"]!["stringValue"]!);
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<PostedRequest> _requests = new();

    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }

    internal IReadOnlyList<PostedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(header.Key == "User-Agent" ? " " : ",", header.Value);
        }

        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync();
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        lock (_gate)
        {
            _requests.Add(new PostedRequest(request.Method.Method, request.RequestUri!.ToString(), headers, body));
        }

        if (Respond is not null)
        {
            return await Respond(request, cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

internal sealed class FakeTimers
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = new();

    internal IReadOnlyList<FakeTimer> Started
    {
        get
        {
            lock (_gate)
            {
                return _timers.ToArray();
            }
        }
    }

    internal IDisposable Start(TimeSpan delay, Action callback)
    {
        var timer = new FakeTimer(delay, callback);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }
}

internal sealed class FakeTimer : IDisposable
{
    internal FakeTimer(TimeSpan delay, Action callback)
    {
        Delay = delay;
        Callback = callback;
    }

    internal TimeSpan Delay { get; }

    internal Action Callback { get; }

    internal bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

// A Diagnostics with every dependency injected: the environment, the test-run answer, the clock, the log sink slot,
// the HTTP handler and the timers.
internal sealed class DiagnosticsHarness
{
    private readonly Dictionary<string, string> _environment = new();

    internal DiagnosticsHarness(bool inTestRun = false, bool useFakeTimers = true, TimeSpan? batchDelay = null, HttpClient? client = null)
    {
        InTestRun = inTestRun;
        Client = client ?? new HttpClient(Handler);
        Diagnostics = new SdkDiagnostics(
            name => _environment.TryGetValue(name, out var value) ? value : null,
            () => InTestRun,
            Clock,
            sink => Sink = sink,
            Client,
            useFakeTimers ? Timers.Start : null,
            batchDelay);
    }

    internal bool InTestRun { get; set; }

    internal RecordingHandler Handler { get; } = new();

    internal HttpClient Client { get; }

    internal ManualClock Clock { get; } = new();

    internal FakeTimers Timers { get; } = new();

    internal SdkDiagnostics Diagnostics { get; }

    // What the runtime log would call for each line: null until diagnostics are enabled.
    internal Action<string>? Sink { get; private set; }

    internal DiagnosticsHarness Set(string name, string? value)
    {
        if (value is null)
        {
            _environment.Remove(name);
        }
        else
        {
            _environment[name] = value;
        }

        return this;
    }

    internal DiagnosticsHarness ForceEnabled() => Set("AGENTCAT_DISABLE_DIAGNOSTICS", "false");

    internal void Write(string line) => Sink?.Invoke(line);

    // Starts diagnostics with the project and returns the harness.
    internal DiagnosticsHarness Started(string? projectId = "proj_1", bool disabled = false)
    {
        Diagnostics.Init(projectId, disabled);
        return this;
    }

    internal Task FlushAsync() => Diagnostics.FlushAsync(TimeSpan.FromSeconds(5));
}
