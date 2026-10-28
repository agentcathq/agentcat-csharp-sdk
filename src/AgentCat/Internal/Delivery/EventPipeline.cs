using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Errors;
using AgentCat.Internal.Logging;
using AgentCat.Internal.Telemetry;

namespace AgentCat.Internal.Delivery;

internal sealed class EventPipeline : IDisposable
{
    private static readonly Action DefaultWarmUp = () => _ = AppLibraries.Current;
    private static readonly Task<bool> CompletedTrue = Task.FromResult(true);
    private static readonly Task<bool> CompletedFalse = Task.FromResult(false);

    private readonly IAgentCatLog _log;
    private readonly IPipelineClock _clock;
    private readonly EventProcessor _processor;
    private readonly ExportChannel? _exports;
    private readonly Action _warmUp;
    private readonly int _maxQueueSize;
    private readonly int _concurrency;
    private readonly TimeSpan _shutdownTimeout;
    private readonly object _gate = new();
    private readonly Queue<QueuedEvent> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stop = new();
    private int _active;
    private bool _closed;
    private bool _disposed;
    private int _started;
    private Timer? _starter;
    private TaskCompletionSource<bool>? _drained;
    private TaskCompletionSource<bool>? _shutdown;

    internal EventPipeline(
        IEventSender sender,
        IAgentCatLog log,
        PipelineSettings? settings = null,
        IPipelineClock? clock = null,
        Action? warmUp = null,
        Action<AgentCatEvent>? record = null,
        ExportChannel? exports = null)
    {
        settings ??= new PipelineSettings();
        _log = log;
        _clock = clock ?? SystemPipelineClock.Instance;
        _processor = new EventProcessor(sender, log, _clock, settings.MaxRetries, record: record);
        _exports = exports;
        _warmUp = warmUp ?? DefaultWarmUp;
        _maxQueueSize = Math.Max(1, settings.MaxQueueSize);
        _concurrency = Math.Max(0, settings.Concurrency);
        _shutdownTimeout = settings.ShutdownTimeout;
    }

    internal int QueueLength
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    internal int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    internal void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var suppress = !ExecutionContext.IsFlowSuppressed();
            var flow = suppress ? ExecutionContext.SuppressFlow() : default;
            try
            {
                var starter = new Timer(OnStart, null, Timeout.Infinite, Timeout.Infinite);
                Volatile.Write(ref _starter, starter);
                starter.Change(0, Timeout.Infinite);
            }
            finally
            {
                if (suppress)
                {
                    flow.Undo();
                }
            }
        }
        catch (Exception)
        {
        }
    }

    internal void Add(QueuedEvent item)
    {
        try
        {
            if (Volatile.Read(ref _closed))
            {
                _log.Write("Queue is shutting down, event dropped", LogSeverity.Warning);
                Discard(item);
                return;
            }

            Start();
            bool closed;
            QueuedEvent? dropped = null;
            lock (_gate)
            {
                closed = _closed;
                if (!closed)
                {
                    if (_queue.Count >= _maxQueueSize)
                    {
                        dropped = _queue.Dequeue();
                    }

                    _queue.Enqueue(item);
                }
            }

            if (closed)
            {
                _log.Write("Queue is shutting down, event dropped", LogSeverity.Warning);
                Discard(item);
            }
            else if (dropped is not null)
            {
                _log.Write("Event queue full, dropping oldest event", LogSeverity.Warning);

                // Cancelling the evicted call's hook token runs the hook's continuations on this thread, the thread of
                // the call whose Add overflowed. That happens only at the limit of waiting events.
                Discard(dropped);
            }
            else
            {
                _available.Release();
            }
        }
        catch (Exception)
        {
        }
    }

    internal bool TrySetExporter(IEventExporter exporter) => _processor.TrySetExporter(exporter);

    // True when no event and no exporter request is left within the budget.
    internal async Task<bool> FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            return await WaitDrainedAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Closes the pipeline for good and drains it within its own budget. True when no event and no exporter request was
    // left. A second call returns the first call's task.
    internal Task<bool> ShutdownAsync() => ShutdownAsync(_shutdownTimeout);

    // The same within the given budget. When the budget ends with events waiting, the "Shutting down with N events
    // still in queue" line is written before the task completes.
    internal Task<bool> ShutdownAsync(TimeSpan budget)
    {
        TaskCompletionSource<bool> shutdown;
        lock (_gate)
        {
            if (_shutdown is not null)
            {
                return _shutdown.Task;
            }

            _closed = true;
            shutdown = _shutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = CompleteShutdownAsync(shutdown, budget);
        return shutdown.Task;
    }

    public void Dispose()
    {
        TaskCompletionSource<bool>? drained;
        QueuedEvent[] abandoned;
        lock (_gate)
        {
            abandoned = _disposed ? Array.Empty<QueuedEvent>() : _queue.ToArray();
            _closed = true;
            _disposed = true;
            drained = _drained;
            _drained = null;
        }

        foreach (var item in abandoned)
        {
            Discard(item);
        }

        try
        {
            _stop.Cancel();
        }
        catch (Exception)
        {
        }

        Interlocked.Exchange(ref _starter, null)?.Dispose();
        drained?.TrySetResult(false);
    }

    private static void Discard(QueuedEvent item)
    {
        try
        {
            item.Discard?.Invoke();
        }
        catch (Exception)
        {
        }
    }

    private void OnStart(object? state)
    {
        try
        {
            Interlocked.Exchange(ref _starter, null)?.Dispose();
            var stop = _stop.Token;
            if (stop.IsCancellationRequested)
            {
                return;
            }

            for (var i = 0; i < _concurrency; i++)
            {
                _ = ReadAsync(stop);
            }

            _warmUp();
        }
        catch (Exception)
        {
        }
    }

    private async Task ReadAsync(CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(stop).ConfigureAwait(false);
                QueuedEvent item;
                lock (_gate)
                {
                    if (stop.IsCancellationRequested || _disposed)
                    {
                        return;
                    }

                    if (_queue.Count == 0)
                    {
                        continue;
                    }

                    item = _queue.Dequeue();
                    _active++;
                }

                try
                {
                    await _processor.ProcessAsync(item, stop).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
                finally
                {
                    Finished();
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private void Finished()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_gate)
        {
            _active--;
            if (_active == 0 && _queue.Count == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult(true);
    }

    // True when no event is queued or in process and no exporter request is left, all within one budget. The events
    // come first, because processing them sends exporter requests. A cancelled token ends the wait like an expired
    // budget: the events and requests keep being sent.
    private async Task<bool> WaitDrainedAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var events = EventsDrained();
        if (IsSettled(events))
        {
            return events.Result;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        using var wait = new WaitBudget(budget, _clock, cancellationToken);
        while (true)
        {
            if (!await wait.CompletesFirstAsync(events).ConfigureAwait(false) || !await events.ConfigureAwait(false))
            {
                return false;
            }

            if (!await wait.CompletesFirstAsync(ExportsIdle()).ConfigureAwait(false))
            {
                return false;
            }

            // Events that arrived during the wait for the requests are waited for in turn.
            events = EventsDrained();
            if (IsSettled(events))
            {
                return events.Result;
            }
        }
    }

    // Completed with true when no event is queued or in process, with false once the pipeline is disposed.
    private Task<bool> EventsDrained()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return CompletedFalse;
            }

            if (_active == 0 && _queue.Count == 0)
            {
                return CompletedTrue;
            }

            _drained ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    private Task ExportsIdle() => _exports?.WhenIdle() ?? Task.CompletedTask;

    // Nothing left to wait for: disposed, or no event and no exporter request.
    private bool IsSettled(Task<bool> events) => events.IsCompleted && (!events.Result || ExportsIdle().IsCompleted);

    private async Task CompleteShutdownAsync(TaskCompletionSource<bool> shutdown, TimeSpan budget)
    {
        var drained = false;
        try
        {
            drained = await WaitDrainedAsync(budget).ConfigureAwait(false);
            var waiting = QueueLength;
            if (waiting > 0)
            {
                _log.Write("Shutting down with " + waiting.ToString(CultureInfo.InvariantCulture) + " events still in queue", LogSeverity.Warning);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            shutdown.TrySetResult(drained);
        }
    }
}
