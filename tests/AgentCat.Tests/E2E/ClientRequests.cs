using System.Globalization;

namespace AgentCat.Tests.E2E;

// The HTTP requests of one MCP client of an in-process lane, as the client sent them. Each request gets its number in
// StampHeader, so the headers that the server captured name the request they came from.
internal sealed class ClientRequests
{
    internal const string StampHeader = "X-AgentCat-E2E-Request";

    private readonly List<SentRequest> _sent = new();
    private int _next;

    // In the order the client started them.
    internal IReadOnlyList<SentRequest> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    internal SentRequest this[string stamp] => Assert.Single(Sent, r => r.Stamp == stamp);

    internal HttpMessageHandler Wrap(HttpMessageHandler inner) => new Stamping(this, inner);

    private sealed class Stamping(ClientRequests owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var stamp = Interlocked.Increment(ref owner._next).ToString(CultureInfo.InvariantCulture);
            request.Headers.TryAddWithoutValidation(StampHeader, stamp);
            var sent = new SentRequest(stamp, request.Method.Method, request.RequestUri!.PathAndQuery);
            lock (owner._sent)
            {
                owner._sent.Add(sent);
            }

            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                // After the send: the inner handler may add headers of its own, such as Transfer-Encoding.
                sent.Headers = SentRequest.HeadersOf(request);
            }
        }
    }
}

// One request of a client: its stamp, method and path, and the headers it carried, named and joined as the capture
// rules name and join them.
internal sealed class SentRequest(string stamp, string method, string pathAndQuery)
{
    private IReadOnlyDictionary<string, string>? _headers;

    internal string Stamp { get; } = stamp;

    internal string Method { get; } = method;

    internal string PathAndQuery { get; } = pathAndQuery;

    internal IReadOnlyDictionary<string, string> Headers
    {
        get => Volatile.Read(ref _headers) ?? throw new InvalidOperationException("Request " + Stamp + " has not been sent yet.");
        set => Volatile.Write(ref _headers, value);
    }

    internal static IReadOnlyDictionary<string, string> HeadersOf(HttpRequestMessage request)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var all = request.Content is null ? request.Headers : request.Headers.Concat(request.Content.Headers);
        foreach (var header in all)
        {
            var name = header.Key.ToLowerInvariant();
            headers[name] = string.Join(name == "cookie" ? "; " : ", ", header.Value);
        }

        return headers;
    }

    public override string ToString() => Stamp + " " + Method + " " + PathAndQuery;
}
