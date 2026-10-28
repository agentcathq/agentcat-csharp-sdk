using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Events;
using AgentCat.Internal.Ids;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Delivery;

internal sealed class EventProcessor
{
    private readonly IEventSender _sender;
    private readonly IAgentCatLog _log;
    private readonly IPipelineClock _clock;
    private readonly int _maxRetries;
    private readonly Action<AgentCatEvent> _sanitize;
    private readonly Action<AgentCatEvent> _truncate;
    private readonly Action<Task> _logExportFault;
    private readonly Action<AgentCatEvent>? _record;
    private IEventExporter? _exporter;

    internal EventProcessor(
        IEventSender sender,
        IAgentCatLog log,
        IPipelineClock clock,
        int maxRetries = 3,
        Action<AgentCatEvent>? sanitize = null,
        Action<AgentCatEvent>? truncate = null,
        Action<AgentCatEvent>? record = null)
    {
        _sender = sender;
        _log = log;
        _clock = clock;
        _maxRetries = maxRetries;
        _sanitize = sanitize ?? EventSanitizer.Sanitize;
        _truncate = truncate ?? EventTruncator.Truncate;
        _logExportFault = LogExportFault;
        _record = record;
    }

    internal bool TrySetExporter(IEventExporter exporter)
        => Interlocked.CompareExchange(ref _exporter, exporter, null) is null;

    internal async Task ProcessAsync(QueuedEvent item, CancellationToken cancellationToken)
    {
        var e = item.Event;
        try
        {
            if (item.Prepare is { } prepare)
            {
                try
                {
                    await prepare(e, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
                {
                    _log.Write("Failed to resolve pending hook results: " + ErrorText.Of(ex));
                }
            }

            if (item.RedactEvent is { } redactEvent)
            {
                bool kept;
                try
                {
                    kept = await EventRedactor.ApplyEventHookAsync(e, redactEvent, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
                {
                    _log.Write("Failed to redact event (event-level hook): " + ErrorText.Of(ex));
                    return;
                }

                if (!kept)
                {
                    _log.Write("Event dropped by redactEvent hook");
                    return;
                }
            }

            if (item.RedactStrings is { } redactStrings)
            {
                try
                {
                    await EventRedactor.RedactStringsAsync(e, redactStrings, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
                {
                    _log.Write("Failed to redact event: " + ErrorText.Of(ex));
                    return;
                }
            }

            try
            {
                _sanitize(e);
            }
            catch (Exception ex)
            {
                _log.Write("Failed to sanitize event: " + ErrorText.Of(ex));
                return;
            }

            try
            {
                _truncate(e);
            }
            catch (Exception ex)
            {
                _log.Write("Failed to truncate event: " + ErrorText.Of(ex));
                return;
            }

            if (string.IsNullOrEmpty(e.Id))
            {
                e.Id = SessionIds.NewEventId(_clock.UtcNow);
            }

            // A recording runtime keeps the event after its last stage: no export, no send, no success line.
            if (_record is { } record)
            {
                record(e);
                return;
            }

            await DeliverAsync(e, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            _log.Write("Failed to deliver AgentCat event after retries: " + SendFailureText(ex));
        }
    }

    private static bool IsCancellation(Exception ex, CancellationToken cancellationToken)
        => ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static string SendFailureText(Exception ex) => ex is EventSendException ? ex.Message : ErrorText.Of(ex);

    private static string SuccessLine(AgentCatEvent e)
        => "Successfully sent event " + e.Id
            + " | " + e.EventType
            + " | session " + e.SessionId
            + " | " + e.ProjectId
            + " | " + (e.Duration?.ToString(CultureInfo.InvariantCulture) ?? "undefined") + " ms"
            + " | " + (string.IsNullOrEmpty(e.IdentifyActorGivenId) ? "anonymous" : e.IdentifyActorGivenId);

    private async Task DeliverAsync(AgentCatEvent e, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            StartExport(e, cancellationToken);
            if (string.IsNullOrEmpty(e.ProjectId))
            {
                return;
            }

            Exception? error = null;
            try
            {
                await _sender.SendAsync(e, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
            {
                error = ex;
            }

            if (error is null)
            {
                _log.WriteRoutine(SuccessLine(e));
                return;
            }

            var failure = SendFailureText(error);
            _log.Write("Failed to send event " + e.Id + ", retrying... [Error: " + failure + "]");
            if (attempt >= _maxRetries || error is EventSendException { Retryable: false })
            {
                _log.Write("Failed to deliver AgentCat event after retries: " + failure);
                return;
            }

            await _clock.Delay(TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 1000), cancellationToken).ConfigureAwait(false);
        }
    }

    private void StartExport(AgentCatEvent e, CancellationToken cancellationToken)
    {
        var exporter = Volatile.Read(ref _exporter);
        if (exporter is null)
        {
            return;
        }

        Task export;
        try
        {
            export = exporter.ExportAsync(e, cancellationToken);
        }
        catch (Exception ex)
        {
            LogExportError(ex);
            return;
        }

        if (export is null)
        {
            return;
        }

        if (export.IsCompleted)
        {
            LogExportFault(export);
            return;
        }

        _ = export.ContinueWith(
            _logExportFault,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void LogExportFault(Task export)
    {
        if (export.IsFaulted)
        {
            LogExportError(export.Exception!.InnerException ?? export.Exception);
        }
    }

    private void LogExportError(Exception ex) => _log.Write("Telemetry export error: " + ErrorText.Of(ex));
}
