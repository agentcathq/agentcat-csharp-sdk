using System.Text.Json.Nodes;
using AgentCat.Internal.ServerInfo;

namespace AgentCat.Tests.TestSupport;

// Records every server info heartbeat and answers 202, or what the script says. It never reaches the network.
internal sealed class RecordingServerInfoSender : IServerInfoSender
{
    internal const int Hang = -1;

    private readonly object _gate = new();
    private readonly Queue<int> _script = new();
    private readonly List<byte[]> _bodies = new();

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _bodies.Count;
            }
        }
    }

    internal IReadOnlyList<JsonObject> Bodies
    {
        get
        {
            lock (_gate)
            {
                return _bodies.Select(b => JsonNode.Parse(b)!.AsObject()).ToArray();
            }
        }
    }

    internal IReadOnlyList<byte[]> RawBodies
    {
        get
        {
            lock (_gate)
            {
                return _bodies.ToArray();
            }
        }
    }

    // The statuses of the next answers, in order. Hang never answers and ignores cancellation.
    internal RecordingServerInfoSender Respond(params int[] statuses)
    {
        lock (_gate)
        {
            foreach (var status in statuses)
            {
                _script.Enqueue(status);
            }
        }

        return this;
    }

    public Task<int> PostServerInfoAsync(byte[] body, CancellationToken cancellationToken)
    {
        int status;
        lock (_gate)
        {
            _bodies.Add(body);
            status = _script.Count > 0 ? _script.Dequeue() : 202;
        }

        return status == Hang ? new TaskCompletionSource<int>().Task : Task.FromResult(status);
    }

    internal async Task<IReadOnlyList<JsonObject>> WaitForAsync(int count)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (Count < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Expected " + count + " server info heartbeat(s), got " + Count + ".");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return Bodies;
    }
}
