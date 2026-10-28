using System;
using System.Diagnostics;
using System.Threading;

namespace AgentCat.Internal.Lifecycle;

/// <summary>
/// The stop window of a runtime, shared by every automatic stop path that drains it: the flush after a host stops, the
/// signal handler and the process-exit handler. The shared runtime's window is the process's. The first path that
/// starts sets the deadline for events to now plus <see cref="EventsBudget"/>. Every path waits for events only until
/// that deadline, then for diagnostics at most <see cref="DiagnosticsBudget"/> and never past the deadline plus
/// <see cref="DiagnosticsBudget"/>, so a stop takes about 6 s however many paths run. A path that starts after the
/// window has ended still joins it, so it waits for no events, until one more full window has passed: only a path that
/// starts at least that long after the window ended is a later stop and gets a new deadline. A path whose event wait
/// ends with nothing left to send clears the deadline, so the next stop gets a new window at once. The wait for events
/// covers the exporter requests too: the pipeline's flush and drain wait for its runtime's export channel.
/// </summary>
internal sealed class ShutdownDeadline
{
    internal static readonly TimeSpan EventsBudget = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan DiagnosticsBudget = TimeSpan.FromSeconds(1);

    // Monotonic: a change of the system clock does not move the deadline.
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    private readonly TimeSpan _events;
    private readonly TimeSpan _diagnostics;
    private readonly Func<TimeSpan> _now;

    // The ticks of _now at which the wait for events ends; 0 while no stop runs.
    private long _deadline;

    internal ShutdownDeadline()
        : this(EventsBudget, DiagnosticsBudget, static () => Uptime.Elapsed)
    {
    }

    internal ShutdownDeadline(TimeSpan events, TimeSpan diagnostics, Func<TimeSpan> now)
    {
        _events = events;
        _diagnostics = diagnostics;
        _now = now;
    }

    /// <summary>
    /// Starts a stop path: joins the running stop's window, or sets a new deadline when there is none or when the
    /// window ended at least one full window (events plus diagnostics budget) ago.
    /// </summary>
    internal Window Enter()
    {
        while (true)
        {
            var now = _now();
            var current = Interlocked.Read(ref _deadline);
            if (current != 0 && now.Ticks < current + _diagnostics.Ticks + (_events + _diagnostics).Ticks)
            {
                return new Window(this, current);
            }

            var fresh = Math.Max(1, (now + _events).Ticks);
            if (Interlocked.CompareExchange(ref _deadline, fresh, current) == current)
            {
                return new Window(this, fresh);
            }
        }
    }

    /// <summary>Ends the stop that <paramref name="window"/> belongs to, unless a later stop has set its own deadline.</summary>
    internal void Clear(Window window) => Interlocked.CompareExchange(ref _deadline, 0, window.Deadline);

    private TimeSpan Left(long end)
    {
        var left = end - _now().Ticks;
        return left > 0 ? TimeSpan.FromTicks(left) : TimeSpan.Zero;
    }

    /// <summary>One path's view of the stop window.</summary>
    internal readonly struct Window
    {
        private readonly ShutdownDeadline _owner;

        internal Window(ShutdownDeadline owner, long deadline)
        {
            _owner = owner;
            Deadline = deadline;
        }

        internal long Deadline { get; }

        /// <summary>How long the path may still wait for events; zero once the deadline has passed.</summary>
        internal TimeSpan EventsLeft => _owner.Left(Deadline);

        /// <summary>How long until the end of the window: the deadline plus the diagnostics budget.</summary>
        internal TimeSpan UntilEnd => _owner.Left(Deadline + _owner._diagnostics.Ticks);

        /// <summary>How long the path may wait for diagnostics: the diagnostics budget, at most until the end of the window.</summary>
        internal TimeSpan DiagnosticsLeft
        {
            get
            {
                var left = UntilEnd;
                return left < _owner._diagnostics ? left : _owner._diagnostics;
            }
        }
    }
}
