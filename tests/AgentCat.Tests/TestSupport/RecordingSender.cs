using AgentCat.Internal.Delivery;
using AgentCat.Internal.Events;

namespace AgentCat.Tests.TestSupport;

internal sealed class RecordingSender : IEventSender
{
    private readonly object _gate = new();
    private readonly Queue<Outcome> _script = new();
    private readonly List<AgentCatEvent> _sent = new();
    private readonly List<string> _sentJson = new();
    private readonly List<HangingSend> _hanging = new();

    public Action<AgentCatEvent>? OnSend { get; set; }

    public IReadOnlyList<AgentCatEvent> Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToArray();
            }
        }
    }

    public IReadOnlyList<string> SentJson
    {
        get
        {
            lock (_gate)
            {
                return _sentJson.ToArray();
            }
        }
    }

    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _sent.Count;
            }
        }
    }

    public int HangingCount
    {
        get
        {
            lock (_gate)
            {
                return _hanging.Count(h => !h.Task.IsCompleted);
            }
        }
    }

    public RecordingSender Succeed(int times = 1) => Script(times, Outcome.Success);

    public RecordingSender Fail(string message, int times = 1)
        => Script(times, new Outcome(OutcomeKind.Throw, () => new EventSendException(message)));

    public RecordingSender Throw(Func<Exception> exception, int times = 1)
        => Script(times, new Outcome(OutcomeKind.Throw, exception));

    public RecordingSender Hang(int times = 1) => Script(times, Outcome.Hanging);

    public void Release()
    {
        HangingSend[] hanging;
        lock (_gate)
        {
            hanging = _hanging.ToArray();
            _hanging.Clear();
        }

        foreach (var send in hanging)
        {
            send.Release();
        }
    }

    public Task SendAsync(AgentCatEvent e, CancellationToken cancellationToken)
    {
        Outcome outcome;
        HangingSend? hanging = null;
        lock (_gate)
        {
            _sent.Add(e);
            _sentJson.Add(EventShape.ToTsJson(e).ToJsonString());
            outcome = _script.Count > 0 ? _script.Dequeue() : Outcome.Success;
            if (outcome.Kind == OutcomeKind.Hang)
            {
                hanging = new HangingSend(cancellationToken);
                _hanging.Add(hanging);
            }
        }

        OnSend?.Invoke(e);
        return outcome.Kind switch
        {
            OutcomeKind.Throw => Task.FromException(outcome.Exception!()),
            OutcomeKind.Hang => hanging!.Task,
            _ => Task.CompletedTask,
        };
    }

    private RecordingSender Script(int times, Outcome outcome)
    {
        lock (_gate)
        {
            for (var i = 0; i < times; i++)
            {
                _script.Enqueue(outcome);
            }
        }

        return this;
    }

    private enum OutcomeKind
    {
        Succeed,
        Throw,
        Hang,
    }

    private sealed class Outcome(OutcomeKind kind, Func<Exception>? exception = null)
    {
        internal static readonly Outcome Success = new(OutcomeKind.Succeed);

        internal static readonly Outcome Hanging = new(OutcomeKind.Hang);

        internal OutcomeKind Kind { get; } = kind;

        internal Func<Exception>? Exception { get; } = exception;
    }

    private sealed class HangingSend
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        internal HangingSend(CancellationToken cancellationToken)
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
