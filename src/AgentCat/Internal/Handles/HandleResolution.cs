using System;

namespace AgentCat.Internal.Handles;

internal enum SessionSource
{
    Hook,
    Supplied,
    Minted,
    Invalid,
    Foreign,
}

internal sealed record HandleResolution(string SessionId, SessionSource SessionSource, bool HookMode, string? AgentId);

internal static class SessionSourceExtensions
{
    internal static string ToWire(this SessionSource source) => source switch
    {
        SessionSource.Hook => "hook",
        SessionSource.Supplied => "supplied",
        SessionSource.Minted => "minted",
        SessionSource.Invalid => "invalid",
        SessionSource.Foreign => "foreign",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}
