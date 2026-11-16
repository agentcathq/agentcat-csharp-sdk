using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.ServerInfo;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Delivery;

internal sealed class HttpEventSender : IEventSender, IServerInfoSender, IDisposable
{
    internal static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _attemptTimeout;
    private volatile string _endpoint = ApiBaseUrl.EventsEndpoint(ApiBaseUrl.Default);
    private volatile string _serverInfoEndpoint = ApiBaseUrl.ServerInfoEndpoint(ApiBaseUrl.Default);

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

    internal string ServerInfoEndpoint => _serverInfoEndpoint;

    internal void SetBaseUrl(string baseUrl)
    {
        _endpoint = ApiBaseUrl.EventsEndpoint(baseUrl);
        _serverInfoEndpoint = ApiBaseUrl.ServerInfoEndpoint(baseUrl);
    }

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
            var status = await PostAsync(endpoint, body, attempt.Token).ConfigureAwait(false);
            if (status < 200 || status > 299)
            {
                throw new EventSendException("HTTP " + status.ToString(CultureInfo.InvariantCulture));
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

    // One POST of the body to the server info endpoint. The caller sets the time limit through the token.
    public Task<int> PostServerInfoAsync(byte[] body, CancellationToken cancellationToken)
        => PostAsync(_serverInfoEndpoint, body, cancellationToken);

    private async Task<int> PostAsync(string endpoint, byte[] body, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.ExpectContinue = false;
        request.Headers.TryAddWithoutValidation("User-Agent", SdkInfo.UserAgent);
        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        return (int)response.StatusCode;
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
