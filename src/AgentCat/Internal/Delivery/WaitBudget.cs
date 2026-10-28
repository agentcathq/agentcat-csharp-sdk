using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCat.Internal.Delivery;

/// <summary>
/// The end of one bounded wait, shared by every step of it: the budget runs out on the clock, or the token is
/// cancelled. A budget of <see cref="Timeout.InfiniteTimeSpan"/>, or at or past the timer range, never runs out, and a
/// negative one counts as zero. Disposing stops the clock's timer.
/// </summary>
internal sealed class WaitBudget : IDisposable
{
    private const long MaxTimerTicks = int.MaxValue * TimeSpan.TicksPerMillisecond;

    private readonly CancellationTokenSource _expiry = new();
    private readonly CancellationTokenRegistration _registration;
    private readonly Task _ended;

    internal WaitBudget(TimeSpan budget, IPipelineClock clock, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false), cancelled);
        var unbounded = budget == Timeout.InfiniteTimeSpan || budget.Ticks >= MaxTimerTicks;
        _ended = unbounded
            ? cancelled.Task
            : Task.WhenAny(cancelled.Task, clock.Delay(budget < TimeSpan.Zero ? TimeSpan.Zero : budget, _expiry.Token));
    }

    /// <summary>True when <paramref name="task"/> completes before the wait ends.</summary>
    internal async Task<bool> CompletesFirstAsync(Task task)
        => task.IsCompleted || await Task.WhenAny(task, _ended).ConfigureAwait(false) == task;

    public void Dispose()
    {
        _expiry.Cancel();
        _registration.Dispose();
        _expiry.Dispose();
    }
}
