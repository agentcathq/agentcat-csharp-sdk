using System.Net;
using System.Net.Http;

namespace AgentCat.Tests.TestSupport;

internal sealed class RecordedHttpRequest
{
    internal RecordedHttpRequest(
        HttpMethod method,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        byte[] body,
        CancellationToken cancellationToken)
    {
        Method = method;
        Uri = uri;
        Headers = headers;
        Body = body;
        CancellationToken = cancellationToken;
    }

    internal HttpMethod Method { get; }

    internal Uri Uri { get; }

    internal string Path => Uri.AbsolutePath;

    // The request headers, then the content headers, each value as HTTP writes it.
    internal IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    internal byte[] Body { get; }

    internal CancellationToken CancellationToken { get; }

    // The value of the first header of that name, in any case; null when there is none.
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

// Records every request and answers it through Respond (200 at once by default). It never reaches the network.
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    private static readonly string[] LineBreaks = { "\r\n", "\n" };

    private readonly object _gate = new();
    private readonly List<RecordedHttpRequest> _requests = new();

    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }
        = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    internal IReadOnlyList<RecordedHttpRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    // Answers every request with the status once the gate completes.
    internal static RecordingHttpHandler Gated(TaskCompletionSource<bool> gate, HttpStatusCode status = HttpStatusCode.OK)
        => new()
        {
            Respond = async (_, _) =>
            {
                await gate.Task.ConfigureAwait(false);
                return new HttpResponseMessage(status);
            },
        };

    // Never answers, and ignores cancellation.
    internal static RecordingHttpHandler Silent()
        => new() { Respond = static (_, _) => new TaskCompletionSource<HttpResponseMessage>().Task };

#pragma warning disable CA2016
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? Array.Empty<byte>()
            : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var headers = new List<KeyValuePair<string, string>>();
        AddHeaders(headers, request.Headers.ToString());
        if (request.Content is not null)
        {
            AddHeaders(headers, request.Content.Headers.ToString());
        }

        lock (_gate)
        {
            _requests.Add(new RecordedHttpRequest(request.Method, request.RequestUri!, headers, body, cancellationToken));
        }

        return Respond(request, cancellationToken);
    }
#pragma warning restore CA2016

    // HttpHeaders.ToString writes one "Name: value" line per header (Environment.NewLine after each), with the values
    // joined as on the wire.
    private static void AddHeaders(List<KeyValuePair<string, string>> headers, string lines)
    {
        foreach (var line in lines.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            headers.Add(new KeyValuePair<string, string>(line.Substring(0, colon), line.Substring(colon + 1).Trim()));
        }
    }
}
