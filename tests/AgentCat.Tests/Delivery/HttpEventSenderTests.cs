using System.Net;
using System.Net.Http;
using AgentCat.Internal;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Text;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Delivery;

public class HttpEventSenderTests
{
    private static AgentCatEvent Event() => new()
    {
        Id = "evt_1",
        SessionId = "ses_1",
        ProjectId = "proj_1",
        EventType = "mcp:tools/call",
        Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 123, TimeSpan.Zero),
        Parameters = System.Text.Json.Nodes.JsonNode.Parse("""{"city":"Zürich"}"""),
    };

    private sealed class CapturedRequest
    {
        internal HttpMethod? Method { get; set; }

        internal Uri? Uri { get; set; }

        internal string? ContentType { get; set; }

        internal long? ContentLength { get; set; }

        internal string? UserAgent { get; set; }

        internal bool HasAuthorization { get; set; }

        internal bool? ExpectContinue { get; set; }

        internal byte[] Body { get; set; } = Array.Empty<byte>();
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<CapturedRequest> Captured { get; } = new();

        internal List<HttpResponseMessage> Responses { get; } = new();

#pragma warning disable CA2016
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest
            {
                Method = request.Method,
                Uri = request.RequestUri,
                ContentType = request.Content?.Headers.ContentType?.ToString(),
                ContentLength = request.Content?.Headers.ContentLength,
                UserAgent = request.Headers.UserAgent.ToString(),
                HasAuthorization = request.Headers.Authorization is not null,
                ExpectContinue = request.Headers.ExpectContinue,
                Body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false),
            };
            lock (Captured)
            {
                Captured.Add(captured);
            }

            var response = await respond(request, cancellationToken).ConfigureAwait(false);
            lock (Captured)
            {
                Responses.Add(response);
            }

            return response;
        }
#pragma warning restore CA2016

        internal static ScriptedHandler Status(HttpStatusCode status)
            => new((_, _) => Task.FromResult(new HttpResponseMessage(status)));
    }

    [Fact]
    public async Task Posts_the_wire_body_with_TS_headers()
    {
        var handler = ScriptedHandler.Status(HttpStatusCode.Accepted);
        using var sender = new HttpEventSender(handler);
        var e = Event();
        await sender.SendAsync(e, CancellationToken.None);

        var request = Assert.Single(handler.Captured);
        var expected = WireBody.Build(e);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.agentcat.com/v1/public/events/", request.Uri!.ToString());
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal(expected.Length, request.ContentLength);
        Assert.Equal(SdkInfo.UserAgent, request.UserAgent);
        Assert.False(request.HasAuthorization);
        Assert.Equal(false, request.ExpectContinue);
        Assert.Equal(expected, request.Body);
    }

    [Fact]
    public async Task Posts_to_the_base_URL_set_last()
    {
        var handler = ScriptedHandler.Status(HttpStatusCode.Accepted);
        using var sender = new HttpEventSender(handler);
        sender.SetBaseUrl("https://one.example.com");
        sender.SetBaseUrl("https://two.example.com/p/");
        await sender.SendAsync(Event(), CancellationToken.None);
        Assert.Equal("https://two.example.com/p/v1/public/events/", Assert.Single(handler.Captured).Uri!.ToString());
    }

    [Theory]
    [InlineData(202)]
    [InlineData(503)]
    public async Task The_response_is_disposed_on_success_and_on_failure(int status)
    {
        var content = new DisposeRecordingContent();
        var handler = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content }));
        using var sender = new HttpEventSender(handler);
        try
        {
            await sender.SendAsync(Event(), CancellationToken.None);
        }
        catch (EventSendException)
        {
        }

        Assert.True(content.Disposed);
    }

    private sealed class DisposeRecordingContent : HttpContent
    {
        internal bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(503)]
    public async Task Status_400_401_503_fail_with_HTTP_status(int status)
    {
        var handler = ScriptedHandler.Status((HttpStatusCode)status);
        using var sender = new HttpEventSender(handler);
        var ex = await Assert.ThrowsAsync<EventSendException>(() => sender.SendAsync(Event(), CancellationToken.None));
        Assert.Equal("HTTP " + status, ex.Message);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task Network_errors_fail_with_the_exception_text()
    {
        var failure = new HttpRequestException("connection refused");
        var handler = new ScriptedHandler((_, _) => Task.FromException<HttpResponseMessage>(failure));
        using var sender = new HttpEventSender(handler);
        var ex = await Assert.ThrowsAsync<EventSendException>(() => sender.SendAsync(Event(), CancellationToken.None));
        Assert.Equal("HttpRequestException: connection refused", ex.Message);
        Assert.Same(failure, ex.InnerException);
        Assert.True(ex.Retryable);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("event type")]
    public async Task Body_build_failures_are_send_failures_that_are_not_retryable(string broken)
    {
        var handler = ScriptedHandler.Status(HttpStatusCode.Accepted);
        using var sender = new HttpEventSender(handler);
        var e = Event();
        if (broken == "project")
        {
            e.ProjectId = "";
        }
        else
        {
            e.EventType = "bogus";
        }

        var ex = await Assert.ThrowsAsync<EventSendException>(() => sender.SendAsync(e, CancellationToken.None));
        Assert.IsType<ArgumentException>(ex.InnerException, exactMatch: false);
        Assert.Equal(ErrorText.Of(ex.InnerException!), ex.Message);
        Assert.False(ex.Retryable);
        Assert.Empty(handler.Captured);
    }

    [Fact]
    public async Task Attempt_timeout_fails_with_the_timeout_text()
    {
        var handler = new ScriptedHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var sender = new HttpEventSender(handler, TimeSpan.FromMilliseconds(200));
        var send = sender.SendAsync(Event(), CancellationToken.None);
        await FakeAgentCatApi.WithinAsync(send, TimeSpan.FromSeconds(2));
        var ex = await Assert.ThrowsAsync<EventSendException>(() => send);
        Assert.Equal("Request timed out after 200 ms", ex.Message);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_send_failure()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, token) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        using var sender = new HttpEventSender(handler);
        using var cts = new CancellationTokenSource();
        var send = sender.SendAsync(Event(), cts.Token);
        await FakeAgentCatApi.WithinAsync(started.Task, FakeAgentCatApi.WaitBound);
        cts.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.IsNotType<EventSendException>(ex);
    }

    [Fact]
    public async Task A_token_cancelled_before_the_call_rethrows_without_sending()
    {
        var handler = ScriptedHandler.Status(HttpStatusCode.Accepted);
        using var sender = new HttpEventSender(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Event(), cts.Token));
        Assert.Empty(handler.Captured);
    }

    [Fact]
    public async Task Body_goes_out_with_content_length_on_a_real_socket()
    {
        using var api = new FakeAgentCatApi();
        using var sender = new HttpEventSender();
        sender.SetBaseUrl(api.BaseUrl);
        var e = Event();
        await sender.SendAsync(e, CancellationToken.None);

        var request = Assert.Single(await api.WaitForRequestsAsync(1));
        var expected = WireBody.Build(e);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/public/events/", request.Path);
        Assert.True(request.HadContentLength);
        Assert.Equal(expected.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Header("Content-Length"));
        Assert.Null(request.Header("Transfer-Encoding"));
        Assert.Null(request.Header("Expect"));
        Assert.Null(request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.Equal(SdkInfo.UserAgent, request.Header("User-Agent"));
        Assert.Equal(expected, request.Body);
    }

    [Fact]
    public async Task Real_socket_statuses_map_to_failures()
    {
        using var api = new FakeAgentCatApi().Respond(503, 202);
        using var sender = new HttpEventSender();
        sender.SetBaseUrl(api.BaseUrl);
        var ex = await Assert.ThrowsAsync<EventSendException>(() => sender.SendAsync(Event(), CancellationToken.None));
        Assert.Equal("HTTP 503", ex.Message);
        await sender.SendAsync(Event(), CancellationToken.None);
    }

    [Fact]
    public async Task A_hanging_server_ends_in_the_timeout_failure()
    {
        using var api = new FakeAgentCatApi().Respond(FakeAgentCatApi.Hang);
        using var sender = new HttpEventSender(attemptTimeout: TimeSpan.FromMilliseconds(300));
        sender.SetBaseUrl(api.BaseUrl);
        var send = sender.SendAsync(Event(), CancellationToken.None);
        await FakeAgentCatApi.WithinAsync(send, FakeAgentCatApi.WaitBound);
        var ex = await Assert.ThrowsAsync<EventSendException>(() => send);
        Assert.Equal("Request timed out after 300 ms", ex.Message);
    }

#if NET
    [Fact]
    public void The_default_handler_retires_idle_connections_before_the_api_closes_them_at_60_seconds()
    {
        using var handler = HttpEventSender.CreateDefaultHandler();
        Assert.Equal(TimeSpan.FromSeconds(30), handler.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
        Assert.Null(handler.ActivityHeadersPropagator);
    }
#else
    [Fact]
    public void The_framework_handler_allows_five_connections_per_server()
    {
        using var handler = HttpEventSender.CreateDefaultHandler();
        Assert.Equal(5, handler.MaxConnectionsPerServer);
    }
#endif

    [Fact]
    public async Task SetBaseUrl_is_safe_while_sends_run_on_five_threads()
    {
        using var api = new FakeAgentCatApi();
        using var sender = new HttpEventSender();
        sender.SetBaseUrl(api.BaseUrl);
        var sends = Enumerable.Range(0, 5).Select(async _ =>
        {
            for (var i = 0; i < 4; i++)
            {
                sender.SetBaseUrl(api.BaseUrl);
                await sender.SendAsync(Event(), CancellationToken.None);
            }
        }).ToArray();
        await FakeAgentCatApi.WithinAsync(Task.WhenAll(sends), FakeAgentCatApi.WaitBound);
        await Task.WhenAll(sends);
        Assert.Equal(20, api.Requests.Count);
    }
}
