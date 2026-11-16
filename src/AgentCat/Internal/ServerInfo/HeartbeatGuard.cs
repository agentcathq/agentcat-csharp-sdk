using System;
using System.Collections.Generic;

namespace AgentCat.Internal.ServerInfo;

// The memory of the heartbeat: the time of the first check and the time of the last attempt for each project and
// server name. It holds times only, and it lives as long as its runtime.
internal sealed class HeartbeatGuard
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(3600);

    internal static readonly TimeSpan SettledAge = TimeSpan.FromSeconds(60);

    internal const int MaxKeys = 1000;

    private readonly object _gate = new();
    private readonly Dictionary<(string ProjectId, string ServerName), DateTimeOffset> _attempts = new();
    private DateTimeOffset? _firstCheck;
    private long _begun;

    // How many attempts this memory let begin since it was created.
    internal long Begun
    {
        get
        {
            lock (_gate)
            {
                return _begun;
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _attempts.Count;
            }
        }
    }

    // True when a heartbeat is due for the key, and then the attempt is recorded, so of two checks at the same moment
    // one starts the work. A check that needs a settled memory is never due before the memory is SettledAge old.
    internal bool TryBegin(string projectId, string serverName, bool needsSettledMemory, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_firstCheck is not { } first || now < first)
            {
                _firstCheck = first = now;
            }

            if (needsSettledMemory && now - first < SettledAge)
            {
                return false;
            }

            var key = (projectId, serverName);
            if (_attempts.TryGetValue(key, out var last))
            {
                // A clock that went back counts as a due heartbeat.
                if (now >= last && now - last < Interval)
                {
                    return false;
                }
            }
            else if (_attempts.Count >= MaxKeys)
            {
                _attempts.Clear();
            }

            _attempts[key] = now;
            _begun++;
            return true;
        }
    }

    // Test seam: sets the time of the first check, and with it the age of the memory.
    internal void StartMemory(DateTimeOffset firstCheck)
    {
        lock (_gate)
        {
            _firstCheck = firstCheck;
        }
    }

    // Forgets every attempt and the first check, as a new process does.
    internal void Clear()
    {
        lock (_gate)
        {
            _attempts.Clear();
            _firstCheck = null;
        }
    }
}
