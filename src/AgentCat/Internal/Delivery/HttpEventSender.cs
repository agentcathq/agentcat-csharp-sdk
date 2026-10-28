using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Delivery;

internal sealed class HttpEventSender : IEventSender, IDisposable
{
    internal static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _attemptTimeout;
    private volatile string _endpoint = ApiBaseUrl.EventsEndpoint(ApiBaseUrl.Default);

    internal HttpEventSender(HttpMessageHandler? handler = null, TimeSpan? attemptTimeout = null)
    {
        if (handler is null)
        {
            _client = AgentCatHttp.Client;
        }
        else
        {
            _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsClient = true;
        }

        _attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
    }

    internal string Endpoint => _endpoint;

    internal void SetBaseUrl(string baseUrl) => _endpoint = ApiBaseUrl.EventsEndpoint(baseUrl);

    public async Task SendAsync(AgentCatEvent e, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] body;
        try
        {
            body = WireBody.Build(e);
        }
        catch (Exception ex)
        {
            throw new EventSendException(ErrorText.Of(ex), ex, retryable: false);
        }

        var endpoint = _endpoint;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_attemptTimeout);
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            request.Headers.ExpectContinue = false;
            request.Headers.TryAddWithoutValidation("User-Agent", SdkInfo.UserAgent);
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new EventSendException("HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (EventSendException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            var ms = (long)_attemptTimeout.TotalMilliseconds;
            throw new EventSendException("Request timed out after " + ms.ToString(CultureInfo.InvariantCulture) + " ms");
        }
        catch (Exception ex)
        {
            throw new EventSendException(ErrorText.Of(ex), ex);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

#if NET
    // The AgentCat API closes a connection after 60 s idle, and a POST sent on a connection while the server closes it
    // fails without a retry by .NET. The pool scavenges idle connections every IdleTimeout / 4, so 30 s retires them by
    // 37.5 s, before the server's close.
    internal static SocketsHttpHandler CreateDefaultHandler()
    {
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ActivityHeadersPropagator = null,
        };
    }
#else
    internal static HttpClientHandler CreateDefaultHandler()
    {
        var handler = new HttpClientHandler();
        try
        {
            handler.MaxConnectionsPerServer = 5;
        }
        catch (PlatformNotSupportedException)
        {
        }

        return handler;
    }
#endif
}
