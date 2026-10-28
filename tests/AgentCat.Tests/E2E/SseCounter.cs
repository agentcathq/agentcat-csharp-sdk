namespace AgentCat.Tests.E2E;

// Counts the content types of POST responses on the client side of an in-process HTTP lane. Every POST that carries a
// JSON-RPC request gets text/event-stream on every MCP version; a notification POST gets 202 with no content type.
internal sealed class SseCounter(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private int _ssePosts;
    private int _jsonPosts;

    internal int SsePosts => Volatile.Read(ref _ssePosts);

    internal int JsonPosts => Volatile.Read(ref _jsonPosts);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (request.Method == HttpMethod.Post)
        {
            switch (response.Content.Headers.ContentType?.MediaType)
            {
                case "text/event-stream":
                    Interlocked.Increment(ref _ssePosts);
                    break;
                case "application/json":
                    Interlocked.Increment(ref _jsonPosts);
                    break;
            }
        }

        return response;
    }
}
