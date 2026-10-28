using AgentCat.Internal.Delivery;

namespace AgentCat.Tests.TestSupport;

internal sealed class ManualClock : IPipelineClock
{
    internal static readonly DateTimeOffset DefaultNow = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new();
    private readonly List<TimeSpan> _delays = new();
    private readonly List<HeldDelay> _held = new();
    private DateTimeOffset _utcNow = DefaultNow;
    private bool _holding;

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }

        set
        {
            lock (_gate)
            {
                _utcNow = value;
            }
        }
    }

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return _delays.ToArray();
            }
        }
    }

    public int HeldCount
    {
        get
        {
            lock (_gate)
            {
                return _held.Count(h => !h.Task.IsCompleted);
            }
        }
    }

    public void HoldDelays()
    {
        lock (_gate)
        {
            _holding = true;
        }
    }

    public void ReleaseDelays()
    {
        HeldDelay[] held;
        lock (_gate)
        {
            held = _held.ToArray();
            _held.Clear();
        }

        foreach (var delay in held)
        {
            delay.Release();
        }
    }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _delays.Add(delay);
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            if (!_holding)
            {
                return Task.CompletedTask;
            }

            var held = new HeldDelay(cancellationToken);
            _held.Add(held);
            return held.Task;
        }
    }

    private sealed class HeldDelay
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        internal HeldDelay(CancellationToken cancellationToken)
        {
            var completion = _completion;
            _registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        }

        internal Task Task => _completion.Task;

        internal void Release()
        {
            _registration.Dispose();
            _completion.TrySetResult(true);
        }
    }
}
