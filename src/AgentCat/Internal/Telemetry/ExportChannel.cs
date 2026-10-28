using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Telemetry;

/// <summary>One request of an exporter, built in full before it is sent.</summary>
internal sealed class ExportRequest
{
    internal ExportRequest(string url, string contentType, byte[] body)
    {
        Url = url;
        ContentType = contentType;
        Body = body;
    }

    internal HttpMethod Method { get; init; } = HttpMethod.Post;

    /// <summary>Parsed when the request starts, so a URL that is not one fails only this request.</summary>
    internal string Url { get; }

    /// <summary>
    /// Set after the <c>User-Agent</c> and <c>Content-Type</c> defaults, in this order: a name that is set again, a
    /// default's name included, keeps the last value. A name HTTP cannot carry is left out.
    /// </summary>
    internal IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    internal string ContentType { get; }

    internal byte[] Body { get; }

    /// <summary>Whether to read the response body when the status is not 2xx, for the failure line.</summary>
    internal bool ReadsFailureBody { get; init; }

    /// <summary>Runs once when the request ends, never for a request dropped from a full queue. It may send more requests.</summary>
    internal Action<ExportResponse>? Completed { get; init; }
}

/// <summary>How an exporter request ended: the answer of the server, or the error that kept it from answering.</summary>
internal sealed class ExportResponse
{
    private ExportResponse(int status, string statusText, string? body, Exception? exception, string? error)
    {
        Status = status;
        StatusText = statusText;
        Body = body;
        Exception = exception;
        Error = error;
    }

    /// <summary>The HTTP status; 0 when no answer arrived.</summary>
    internal int Status { get; }

    /// <summary>The reason phrase of the status line (<c>fetch</c>'s <c>statusText</c>); empty when no answer arrived.</summary>
    internal string StatusText { get; }

    /// <summary>The body of an answer that is not 2xx, when the request asked for it; otherwise null.</summary>
    internal string? Body { get; }

    internal Exception? Exception { get; }

    /// <summary>The error for log lines: <c>Request timed out after 10000 ms</c>, else the exception's type and message.</summary>
    internal string? Error { get; }

    /// <summary>An answer with a 2xx status (<c>fetch</c>'s <c>response.ok</c>).</summary>
    internal bool Ok => Error is null && Status >= 200 && Status <= 299;

    internal static ExportResponse Answered(int status, string statusText, string? body) => new(status, statusText, body, null, null);

    internal static ExportResponse Failed(Exception exception, string error) => new(0, "", null, exception, error);
}

/// <summary>
/// Sends the requests of the exporters of one runtime: one attempt each, which ends after <see cref="RequestTimeout"/>;
/// at most <see cref="MaxInFlight"/> at once and <see cref="MaxWaiting"/> waiting, where each request beyond them drops
/// the oldest waiting one. The drops are reported in a WARN line at most once per <see cref="DropLineInterval"/>, with
/// the number dropped since the last line; the first drop is reported at once, and drops not yet reported when the
/// channel becomes idle are reported then. <see cref="Send"/> never throws, and a request starts without the caller's
/// current <see cref="Activity"/> and without flowing the caller's execution context to the threads it uses. It does
/// not wait for the request. A request that ends before its first wait, such as one whose URL is not a URL, runs its
/// <see cref="ExportRequest.Completed"/> callback and the waiting requests that follow it inside <see cref="Send"/>,
/// on the caller's thread: the callback sees the caller's <see cref="AsyncLocal{T}"/> values but no current
/// <see cref="Activity"/>. The runtime's flush and stop paths wait for this channel.
/// </summary>
internal sealed class ExportChannel
{
    internal const int MaxInFlight = 10;

    internal const int MaxWaiting = 1000;

    /// <summary>The most of a failure body that is read; the rest is left unread.</summary>
    internal const int MaxFailureBodyBytes = 64 * 1024;

    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan DropLineInterval = TimeSpan.FromSeconds(1);

    private static readonly string TimedOutText =
        "Request timed out after " + ((long)RequestTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";

    private readonly IAgentCatLog _log;
    private readonly IPipelineClock _clock;
    private readonly object _gate = new();
    private readonly Queue<ExportRequest> _waiting = new();
    private int _inFlight;
    private TaskCompletionSource<bool>? _idle;

    // The drops not reported yet, and the clock time of the last drop line (null before the first).
    private int _unreportedDrops;
    private DateTimeOffset? _lastDropLine;

    /// <summary>A channel over <paramref name="handler"/>, or over <see cref="AgentCatHttp.Client"/> without one.</summary>
    internal ExportChannel(IAgentCatLog log, IPipelineClock clock, HttpMessageHandler? handler = null)
    {
        _log = log;
        _clock = clock;
        Client = handler is null ? AgentCatHttp.Client : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private ExportChannel()
    {
        _log = NullAgentCatLog.Instance;
        _clock = SystemPipelineClock.Instance;
    }

    /// <summary>A channel that drops every request: a recording runtime exports nothing.</summary>
    internal static ExportChannel Discarding { get; } = new();

    /// <summary>The client the requests go through; null for <see cref="Discarding"/>.</summary>
    internal HttpClient? Client { get; }

    internal int InFlightCount
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    internal int WaitingCount
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count;
            }
        }
    }

    internal static string DroppedLine(int count)
        => "WARN: AgentCat telemetry export queue full, dropped the oldest " + count.ToString(CultureInfo.InvariantCulture) + " request(s)";

    /// <summary>Starts the request, or queues it while <see cref="MaxInFlight"/> requests are in flight. Never throws.</summary>
    internal void Send(ExportRequest request)
    {
        try
        {
            if (request is null || Client is null)
            {
                return;
            }

            bool start;
            var report = 0;
            lock (_gate)
            {
                start = _inFlight < MaxInFlight;
                if (start)
                {
                    _inFlight++;
                }
                else
                {
                    var full = _waiting.Count >= MaxWaiting;
                    if (full)
                    {
                        _waiting.Dequeue();
                    }

                    _waiting.Enqueue(request);
                    if (full)
                    {
                        _unreportedDrops++;
                        report = TakeDropsToReport(whenIdle: false);
                    }
                }
            }

            if (start)
            {
                Start(request);
            }
            else if (report > 0)
            {
                WriteDropLine(report);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Completes when no request is in flight or waiting.</summary>
    internal Task WhenIdle()
    {
        lock (_gate)
        {
            if (_inFlight == 0 && _waiting.Count == 0)
            {
                return Task.CompletedTask;
            }

            _idle ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }

    /// <summary>
    /// True when no request is left within <paramref name="budget"/>; a cancelled token ends the wait like an expired
    /// budget, and the requests go on. Never throws.
    /// </summary>
    internal async Task<bool> WaitIdleAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        try
        {
            var idle = WhenIdle();
            if (idle.IsCompleted)
            {
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            using var wait = new WaitBudget(budget, _clock, cancellationToken);
            return await wait.CompletesFirstAsync(idle).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Complete(ExportRequest request, ExportResponse response)
    {
        try
        {
            request.Completed?.Invoke(response);
        }
        catch (Exception)
        {
        }
    }

    private static void SetHeader(HttpRequestMessage message, string name, string value)
    {
        try
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                message.Content!.Headers.Remove("Content-Type");
                message.Content.Headers.TryAddWithoutValidation("Content-Type", value);
                return;
            }

            if (Replace(message.Headers, name, value))
            {
                return;
            }

            Replace(message.Content!.Headers, name, value);
        }
        catch (Exception)
        {
        }
    }

    // False when the collection does not take the name: a content header on the request, or an invalid name.
    private static bool Replace(System.Net.Http.Headers.HttpHeaders headers, string name, string value)
    {
        try
        {
            headers.Remove(name);
        }
        catch (Exception)
        {
            return false;
        }

        return headers.TryAddWithoutValidation(name, value);
    }

    private static async Task<string> ReadTextAsync(HttpContent content, CancellationToken cancellationToken)
    {
#if NET
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        var buffer = new byte[MaxFailureBodyBytes];
        var length = 0;
        while (length < buffer.Length)
        {
#if NET
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
#else
            var read = await stream.ReadAsync(buffer, length, buffer.Length - length, cancellationToken).ConfigureAwait(false);
#endif
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        // fetch's text(): UTF-8 without its byte order mark.
        var start = length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(buffer, start, length - start);
    }

    // The request starts without the caller's execution context: no flow into the threads it uses, and no current
    // Activity to parent anything. SuppressFlow cannot clear the thread's own values, so Activity.Current is cleared
    // by hand and put back.
    private void Start(ExportRequest first)
    {
        var suppress = !ExecutionContext.IsFlowSuppressed();
        var flow = suppress ? ExecutionContext.SuppressFlow() : default;
        var customerActivity = Activity.Current;
        Activity.Current = null;
        try
        {
            _ = RunAsync(first);
        }
        finally
        {
            Activity.Current = customerActivity;
            if (suppress)
            {
                flow.Undo();
            }
        }
    }

    // One slot: sends its request, then the waiting ones, until none waits.
    private async Task RunAsync(ExportRequest first)
    {
        var next = first;
        while (next is not null)
        {
            var response = await ExchangeAsync(next).ConfigureAwait(false);
            Complete(next, response);
            next = TakeNext();
        }
    }

    // The next waiting request for a slot that has finished one, or null after the slot is freed.
    private ExportRequest? TakeNext()
    {
        TaskCompletionSource<bool>? idle = null;
        var report = 0;
        lock (_gate)
        {
            if (_waiting.Count > 0)
            {
                return _waiting.Dequeue();
            }

            _inFlight--;
            if (_inFlight == 0)
            {
                idle = _idle;
                _idle = null;
                report = TakeDropsToReport(whenIdle: true);
            }
        }

        if (report > 0)
        {
            WriteDropLine(report);
        }

        idle?.TrySetResult(true);
        return null;
    }

    // Called under the lock: how many drops to report now, or 0. A line goes out when none has yet, when the interval
    // has passed since the last one, when the clock stepped back (so the line never stalls), and when the channel
    // becomes idle.
    private int TakeDropsToReport(bool whenIdle)
    {
        if (_unreportedDrops == 0)
        {
            return 0;
        }

        var now = _clock.UtcNow;
        if (!whenIdle && _lastDropLine is { } last && now >= last && now - last < DropLineInterval)
        {
            return 0;
        }

        var count = _unreportedDrops;
        _unreportedDrops = 0;
        _lastDropLine = now;
        return count;
    }

    private void WriteDropLine(int count)
    {
        try
        {
            _log.Write(DroppedLine(count));
        }
        catch (Exception)
        {
        }
    }

    // The request, or a timeout after RequestTimeout on the clock. A timed-out request is aborted, and its slot is
    // free at once, whether or not the transfer stops.
    private async Task<ExportResponse> ExchangeAsync(ExportRequest request)
    {
        try
        {
            var abort = new CancellationTokenSource();
            var transfer = TransferAsync(request, abort.Token);
            if (!transfer.IsCompleted)
            {
                using var timer = new CancellationTokenSource();
                var expired = _clock.Delay(RequestTimeout, timer.Token);
                var first = await Task.WhenAny(transfer, expired).ConfigureAwait(false);
                timer.Cancel();
                if (first != transfer)
                {
                    Abort(abort);
                    return ExportResponse.Failed(new TimeoutException(TimedOutText), TimedOutText);
                }
            }

            abort.Dispose();
            return await transfer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ExportResponse.Failed(ex, ErrorText.Of(ex));
        }
    }

    private static void Abort(CancellationTokenSource abort)
    {
        try
        {
            abort.Cancel();
        }
        catch (Exception)
        {
        }
    }

    // Never throws: every failure becomes the response.
    private async Task<ExportResponse> TransferAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new ByteArrayContent(request.Body);
            content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
            using var message = new HttpRequestMessage(request.Method, new Uri(request.Url, UriKind.Absolute)) { Content = content };
            message.Headers.ExpectContinue = false;
            message.Headers.TryAddWithoutValidation("User-Agent", SdkInfo.UserAgent);
            foreach (var header in request.Headers)
            {
                SetHeader(message, header.Key, header.Value);
            }

            using var response = await Client!
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            string? body = null;
            if (request.ReadsFailureBody && (status < 200 || status > 299) && response.Content is not null)
            {
                body = await ReadTextAsync(response.Content, cancellationToken).ConfigureAwait(false);
            }

            return ExportResponse.Answered(status, response.ReasonPhrase ?? "", body);
        }
        catch (Exception ex)
        {
            return ExportResponse.Failed(ex, ErrorText.Of(ex));
        }
    }
}
