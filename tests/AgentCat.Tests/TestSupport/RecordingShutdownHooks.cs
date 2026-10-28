#if NET
using System.Runtime.InteropServices;
#endif
using AgentCat.Internal.Lifecycle;

namespace AgentCat.Tests.TestSupport;

// Records the handlers AgentCat would attach to the process, and attaches nothing.
internal sealed class RecordingShutdownHooks : IShutdownHooks
{
    private readonly object _gate = new();
    private readonly List<Action> _processExit = new();
#if NET
    private readonly List<(PosixSignal Signal, Action<PosixSignalContext> Handler)> _signals = new();
#endif

    internal int ProcessExitCount
    {
        get
        {
            lock (_gate)
            {
                return _processExit.Count;
            }
        }
    }

#if NET
    internal int SignalCount
    {
        get
        {
            lock (_gate)
            {
                return _signals.Count;
            }
        }
    }
#else
    // AgentCat registers no signal handler on .NET Framework.
#pragma warning disable CA1822
    internal int SignalCount => 0;
#pragma warning restore CA1822
#endif

#if NET
    internal IReadOnlyList<PosixSignal> Signals
    {
        get
        {
            lock (_gate)
            {
                return _signals.Select(s => s.Signal).ToArray();
            }
        }
    }

    internal IReadOnlyList<Action<PosixSignalContext>> SignalHandlers
    {
        get
        {
            lock (_gate)
            {
                return _signals.Select(s => s.Handler).ToArray();
            }
        }
    }
#endif

    public void AddProcessExit(Action handler)
    {
        lock (_gate)
        {
            _processExit.Add(handler);
        }
    }

#if NET
    public IDisposable AddSignal(PosixSignal signal, Action<PosixSignalContext> handler)
    {
        lock (_gate)
        {
            _signals.Add((signal, handler));
        }

        return new Registration();
    }

    private sealed class Registration : IDisposable
    {
        public void Dispose()
        {
        }
    }
#endif
}
