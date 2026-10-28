using System;

namespace AgentCat.Internal.Telemetry;

/// <summary>Random bytes for IDs that have no input to hash: sessionless trace and span IDs, Sentry event-ID halves.</summary>
internal interface IRandomSource
{
    void Fill(Span<byte> buffer);
}

/// <summary>Cryptographically strong random bytes, as TS <c>crypto.randomBytes</c> gives.</summary>
internal sealed class RandomSource : IRandomSource
{
    internal static readonly RandomSource Default = new();

    private RandomSource()
    {
    }

    public void Fill(Span<byte> buffer) => Crypto.RandomBytes(buffer.Length).CopyTo(buffer);
}
