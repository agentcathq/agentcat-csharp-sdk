using System;
using System.Collections.Generic;
#if NET
using System.Runtime.InteropServices;
#endif
using System.Threading;
using System.Threading.Tasks;
using AgentCat.Internal.Runtime;

namespace AgentCat.Internal.Lifecycle;

/// <summary>
/// Sends the queued events of the shared runtime when the process ends. The first install of the process that passes
/// the gate decides: whether there are handlers at all, and whether there are signal handlers. These handlers and the
/// flush after a host stops share the runtime's <see cref="ShutdownDeadline"/>, so the whole stop waits about 6 s at
/// most (5 s for events, then at most 1 s for diagnostics), however many of them run.
/// </summary>
internal static class ShutdownHandlers
{
    private static readonly object Gate = new();

    // A signal registration that is collected stops handling the signal.
    private static readonly List<IDisposable> Kept = new();

    private static bool _decided;

    /// <summary>Test seam: attaches the handlers to the process.</summary>
    internal static IShutdownHooks Hooks { get; set; } = ProcessShutdownHooks.Instance;

    /// <summary>
    /// Attaches the process-exit handler and, with <paramref name="withSignals"/> on .NET, the SIGTERM and SIGINT
    /// handlers, unless <paramref name="disabled"/>. Only the first call for the shared runtime counts. Never throws.
    /// </summary>
    internal static void EnsureRegistered(AgentCatRuntime runtime, bool disabled, bool withSignals)
    {
        try
        {
            if (Volatile.Read(ref _decided) || runtime is null || !runtime.IsShared)
            {
                return;
            }

            lock (Gate)
            {
                if (_decided)
                {
                    return;
                }

                Volatile.Write(ref _decided, true);
                if (disabled)
                {
                    return;
                }

                var hooks = Hooks;
                try
                {
                    hooks.AddProcessExit(() => OnProcessExit(runtime));
                }
                catch (Exception)
                {
                }

#if NET
                if (withSignals)
                {
                    Keep(hooks, PosixSignal.SIGTERM, runtime);
                    Keep(hooks, PosixSignal.SIGINT, runtime);
                }
#endif
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Test seam: forgets the decision and the kept signal registrations. Attached handlers stay attached.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            foreach (var registration in Kept)
            {
                registration.Dispose();
            }

            Kept.Clear();
            Volatile.Write(ref _decided, false);
        }
    }

    /// <summary>
    /// Closes the pipeline and waits for its drain, then sends the last diagnostics batch, within the runtime's stop
    /// window. Never throws.
    /// </summary>
    internal static void OnProcessExit(AgentCatRuntime runtime)
    {
        try
        {
            // The drain's own budget ends at the deadline, so its "Shutting down with N events still in queue" line is
            // written before the diagnostics flush starts.
            Drain(
                runtime.ShutdownDeadline,
                budget => runtime.Pipeline.ShutdownAsync(budget),
                budget => runtime.Diagnostics.FlushAsync(budget));
        }
        catch (Exception)
        {
        }
    }

#if NET
    /// <summary>
    /// Flushes the pipeline, then sends the last diagnostics batch, within the runtime's stop window, unless an earlier
    /// handler took the signal. Never sets <see cref="PosixSignalContext.Cancel"/>, so the default termination and its
    /// exit code follow. Never throws.
    /// </summary>
    internal static void OnSignal(PosixSignalContext context, AgentCatRuntime runtime)
    {
        try
        {
            OnSignal(
                context,
                runtime.ShutdownDeadline,
                budget => runtime.Pipeline.FlushAsync(budget),
                budget => runtime.Diagnostics.FlushAsync(budget));
        }
        catch (Exception)
        {
        }
    }

    internal static void OnSignal(PosixSignalContext context, ShutdownDeadline deadline, Func<TimeSpan, Task<bool>> drain, Func<TimeSpan, Task> flushDiagnostics)
    {
        try
        {
            // A handler that ran before this one (a host's ConsoleLifetime, or the customer's) owns the signal. A host
            // stops gracefully and AgentCat flushes after it has stopped. .NET 8 calls handlers in registration order
            // and .NET 10 newest first, so this one can also run before a host's: it then flushes, and the host's stop
            // starts after it. It never closes the pipeline, because that stop can still queue events.
            if (context.Cancel)
            {
                return;
            }

            Drain(deadline, drain, flushDiagnostics);
        }
        catch (Exception)
        {
        }
    }

    private static void Keep(IShutdownHooks hooks, PosixSignal signal, AgentCatRuntime runtime)
    {
        try
        {
            Kept.Add(hooks.AddSignal(signal, context => OnSignal(context, runtime)));
        }
        catch (Exception)
        {
            // PlatformNotSupportedException where the OS has no such signal.
        }
    }
#endif

    /// <summary>
    /// Waits for the drain, then for the diagnostics flush, within the stop window of <paramref name="deadline"/>. The
    /// drain gets the time left until the deadline (none once it has passed); a drain that leaves nothing to send clears
    /// the deadline. The diagnostics flush comes second, so the drain's own lines are in the last batch, and gets at
    /// most the diagnostics budget, never past the end of the window. Never throws.
    /// </summary>
    internal static void Drain(ShutdownDeadline deadline, Func<TimeSpan, Task<bool>> drain, Func<TimeSpan, Task> flushDiagnostics)
    {
        try
        {
            var window = deadline.Enter();
            var events = window.EventsLeft;

            // The drain ends by itself at the deadline. The wait for it may also use the diagnostics time, so that a drain
            // that ends a moment late still writes its lines before the diagnostics flush.
            if (Wait(() => drain(events), window.UntilEnd))
            {
                deadline.Clear(window);
            }

            var diagnostics = window.DiagnosticsLeft;
            if (diagnostics > TimeSpan.Zero)
            {
                Wait(() => flushDiagnostics(diagnostics), diagnostics);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>The same as <see cref="Drain"/>, awaited instead of waited for: the host awaits its stop. Never throws.</summary>
    internal static async Task DrainAsync(ShutdownDeadline deadline, Func<TimeSpan, Task<bool>> drain, Func<TimeSpan, Task> flushDiagnostics)
    {
        try
        {
            var window = deadline.Enter();
            var drained = false;
            try
            {
                drained = await drain(window.EventsLeft).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            if (drained)
            {
                deadline.Clear(window);
            }

            var diagnostics = window.DiagnosticsLeft;
            if (diagnostics > TimeSpan.Zero)
            {
                await flushDiagnostics(diagnostics).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }

    // The process waits for these handlers: on .NET, ProcessExit runs on the thread that ends the process (on .NET
    // Framework, the finalizer thread), and a signal handler on the runtime's signal thread. A bounded Wait gives up at
    // the bound whatever the task does. True when the task ended in time with true.
    private static bool Wait(Func<Task<bool>> start, TimeSpan bound)
    {
        try
        {
            var task = start();
            return task.Wait(bound) && task.Result;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Wait(Func<Task> start, TimeSpan bound)
    {
        try
        {
            start().Wait(bound);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>Attaches shutdown handlers to the process.</summary>
internal interface IShutdownHooks
{
    void AddProcessExit(Action handler);

#if NET
    IDisposable AddSignal(PosixSignal signal, Action<PosixSignalContext> handler);
#endif
}

internal sealed class ProcessShutdownHooks : IShutdownHooks
{
    internal static readonly ProcessShutdownHooks Instance = new();

    private ProcessShutdownHooks()
    {
    }

    public void AddProcessExit(Action handler) => AppDomain.CurrentDomain.ProcessExit += (_, _) => handler();

#if NET
    public IDisposable AddSignal(PosixSignal signal, Action<PosixSignalContext> handler) => PosixSignalRegistration.Create(signal, handler);
#endif
}
