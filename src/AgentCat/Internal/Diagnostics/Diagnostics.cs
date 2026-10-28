using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Logging;

namespace AgentCat.Internal.Diagnostics;

/// <summary>
/// Sends every AgentCat log line to AgentCat as an OTLP log record. The first <see cref="Init"/> decides for the
/// process. Nothing here writes a log line, throws, or runs in the caller's execution context.
/// </summary>
internal sealed class Diagnostics
{
    internal const string DefaultEndpoint = "https://otel.agentcat.com";
    internal const string DefaultToken = "dgk_sdk_diag_3f9a2c7e1b8d4065af2e9c1d7b6a4f80";
    internal const string EndpointVariable = "AGENTCAT_DIAGNOSTICS_ENDPOINT";
    internal const string TokenVariable = "AGENTCAT_DIAGNOSTICS_TOKEN";
    internal const int MaxBuffer = 1000;
    internal static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan TimerFlushTimeout = TimeSpan.FromSeconds(10);

    private const string Path = "/v1/logs";

    private readonly bool _inert;
    private readonly Func<string, string?>? _environment;
    private readonly Func<bool>? _inTestRun;
    private readonly IPipelineClock? _clock;
    private readonly Action<Action<string>?>? _setSink;
    private readonly HttpClient? _client;
    private readonly Func<TimeSpan, Action, IDisposable> _startTimer;
    private readonly TimeSpan _batchDelay;
    private readonly object _gate = new();
    private readonly Queue<DiagnosticsRecord> _buffer = new();
    private int _initialized;
    private volatile bool _enabled;
    private IReadOnlyList<KeyValuePair<string, string>> _resource = Array.Empty<KeyValuePair<string, string>>();
    private IReadOnlyList<KeyValuePair<string, string>> _recordAttributes = Array.Empty<KeyValuePair<string, string>>();
    private IDisposable? _timer;

    private Diagnostics()
    {
        _inert = true;
        _startTimer = StartTimer;
    }

    internal Diagnostics(
        Func<string, string?> environment,
        Func<bool> inTestRun,
        IPipelineClock clock,
        Action<Action<string>?> setSink,
        HttpClient client,
        Func<TimeSpan, Action, IDisposable>? startTimer = null,
        TimeSpan? batchDelay = null)
    {
        _environment = environment;
        _inTestRun = inTestRun;
        _clock = clock;
        _setSink = setSink;
        _client = client;
        _startTimer = startTimer ?? StartTimer;
        _batchDelay = batchDelay ?? BatchDelay;
    }

    /// <summary>An instance whose <see cref="Init"/> does nothing.</summary>
    internal static Diagnostics Inert { get; } = new();

    internal bool IsEnabled => _enabled;

    internal int BufferedCount
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Count;
            }
        }
    }

    internal IReadOnlyList<DiagnosticsRecord> Buffered
    {
        get
        {
            lock (_gate)
            {
                return _buffer.ToArray();
            }
        }
    }

    internal IReadOnlyList<KeyValuePair<string, string>> Resource => _resource;

    /// <summary>The first call decides. Later calls change nothing.</summary>
    internal void Init(string? projectId, bool disabled)
    {
        if (_inert || Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        try
        {
            if (!DiagnosticsSwitch.IsEnabled(disabled, _environment!, InTestRun()))
            {
                return;
            }

            _resource = DiagnosticsAttributes.Resource(projectId, _environment!);
            _recordAttributes = DiagnosticsAttributes.Record();
            _enabled = true;
            _setSink!(Capture);
        }
        catch (Exception)
        {
            _enabled = false;
        }
    }

    /// <summary>Sends the buffered records in one POST that gives up after <paramref name="timeout"/>. Never throws.</summary>
    internal async Task FlushAsync(TimeSpan timeout)
    {
        try
        {
            if (!_enabled)
            {
                return;
            }

            DiagnosticsRecord[] batch;
            IDisposable? timer;
            lock (_gate)
            {
                if (_buffer.Count == 0)
                {
                    return;
                }

                batch = _buffer.ToArray();
                _buffer.Clear();
                timer = _timer;
                _timer = null;
            }

            timer?.Dispose();
            var body = DiagnosticsBody.Build(_resource, SdkInfo.Version, batch);
            using var cancel = new CancellationTokenSource(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint(_environment!)) { Content = content };
            request.Headers.ExpectContinue = false;
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ResolveToken(_environment!));
            request.Headers.TryAddWithoutValidation("User-Agent", SdkInfo.UserAgent);
            using var response = await Post(request, cancel.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The records are lost and nothing is logged, so a failed flush never feeds the buffer.
        }
    }

    internal static string ResolveEndpoint(Func<string, string?> environment)
    {
        var configured = Read(environment, EndpointVariable);
        var trimmed = (configured ?? DefaultEndpoint).TrimEnd('/');
        return trimmed.EndsWith(Path, StringComparison.Ordinal) ? trimmed : trimmed + Path;
    }

    internal static string ResolveToken(Func<string, string?> environment) => Read(environment, TokenVariable) ?? DefaultToken;

    /// <summary>The OTLP severity of a log line: fail or error (any case) is ERROR, then "Warning:" is WARN, else INFO.</summary>
    internal static (int Number, string Text) Severity(string entry)
    {
        return LogSeverityRule.Of(entry) switch
        {
            LogSeverity.Error => (17, "ERROR"),
            LogSeverity.Warning => (13, "WARN"),
            _ => (9, "INFO"),
        };
    }

    private static string? Read(Func<string, string?> environment, string name)
    {
        try
        {
            var value = environment(name);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool InTestRun()
    {
        try
        {
            return _inTestRun!();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Capture(string entry)
    {
        try
        {
            if (!_enabled)
            {
                return;
            }

            var (number, text) = Severity(entry);
            var nanos = (_clock!.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L).ToString(CultureInfo.InvariantCulture);
            var record = new DiagnosticsRecord(nanos, number, text, entry, _recordAttributes);
            lock (_gate)
            {
                if (_buffer.Count >= MaxBuffer)
                {
                    _buffer.Dequeue();
                }

                _buffer.Enqueue(record);
                _timer ??= _startTimer(_batchDelay, OnTimer);
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnTimer()
    {
        try
        {
            lock (_gate)
            {
                _timer = null;
            }

            _ = FlushAsync(TimerFlushTimeout);
        }
        catch (Exception)
        {
        }
    }

    // The POST starts without the caller's execution context: no flow into the threads it uses, and no current
    // Activity to parent anything. SuppressFlow cannot clear the thread's own values, so Activity.Current is cleared
    // by hand and put back.
    private Task<HttpResponseMessage> Post(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var suppress = !ExecutionContext.IsFlowSuppressed();
        var flow = suppress ? ExecutionContext.SuppressFlow() : default;
        var customerActivity = Activity.Current;
        Activity.Current = null;
        try
        {
            return _client!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

    private static Timer StartTimer(TimeSpan delay, Action callback)
    {
        var suppress = !ExecutionContext.IsFlowSuppressed();
        var flow = suppress ? ExecutionContext.SuppressFlow() : default;
        try
        {
            return new Timer(static state => ((Action)state!)(), callback, delay, Timeout.InfiniteTimeSpan);
        }
        finally
        {
            if (suppress)
            {
                flow.Undo();
            }
        }
    }
}
