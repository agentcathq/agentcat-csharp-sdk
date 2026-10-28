using AgentCat.Internal.Telemetry;

namespace AgentCat.Tests.TestSupport;

// Hands out the given bytes, one hex string per draw, in order. A draw of another length, or one draw too many, throws.
internal sealed class FixedRandomSource : IRandomSource
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _draws = new();

    internal FixedRandomSource(params string[] hexDraws)
    {
        foreach (var hex in hexDraws)
        {
            _draws.Enqueue(Bytes(hex));
        }
    }

    internal int Draws { get; private set; }

    public void Fill(Span<byte> buffer)
    {
        byte[] next;
        lock (_gate)
        {
            if (_draws.Count == 0)
            {
                throw new InvalidOperationException("No random bytes left after " + Draws + " draw(s).");
            }

            next = _draws.Dequeue();
            Draws++;
        }

        if (next.Length != buffer.Length)
        {
            throw new InvalidOperationException("The draw has " + next.Length + " bytes; " + buffer.Length + " were asked for.");
        }

        next.CopyTo(buffer);
    }

    private static byte[] Bytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }
}
