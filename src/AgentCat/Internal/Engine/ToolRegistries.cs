using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AgentCat.Internal.Injection;

namespace AgentCat.Internal.Engine;

// Writers replace the snapshot under the gate; every tools/call reads it without taking the lock.
internal sealed class ToolRegistries
{
    private readonly object _gate = new();
    private Snapshot _current = new(new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), false);

    internal IReadOnlyDictionary<string, IReadOnlyCollection<string>> InjectedParams => Volatile.Read(ref _current).Injected;

    internal bool HasListing => Volatile.Read(ref _current).HasListing;

    internal void Record(InjectedList list)
    {
        lock (_gate)
        {
            RecordLocked(list);
        }
    }

    // A rebuild records the whole list while there is no listing yet; after that it adds only the tools the
    // registry does not hold, so it never replaces what a listing recorded.
    internal bool RecordMissing(InjectedList list)
    {
        lock (_gate)
        {
            var current = _current;
            if (!current.HasListing)
            {
                RecordLocked(list);
                return true;
            }

            List<string>? added = null;
            foreach (var name in list.Registry.Keys)
            {
                if (!current.Injected.ContainsKey(name))
                {
                    (added ??= new List<string>()).Add(name);
                }
            }

            if (added is null)
            {
                return false;
            }

            var injected = new Dictionary<string, IReadOnlyCollection<string>>(current.Injected, StringComparer.Ordinal);
            var outputs = new HashSet<string>(current.Outputs, StringComparer.Ordinal);
            foreach (var name in added)
            {
                injected[name] = list.Registry[name];
                if (list.OutputRegistry.Contains(name))
                {
                    outputs.Add(name);
                }
                else
                {
                    outputs.Remove(name);
                }
            }

            Volatile.Write(ref _current, new Snapshot(injected, outputs, true));
            return true;
        }
    }

    internal bool MirrorsStructured(string toolName)
    {
        var current = Volatile.Read(ref _current);
        return !current.HasListing || current.Outputs.Contains(toolName);
    }

    private void RecordLocked(InjectedList list)
    {
        var current = _current;
        var injected = new Dictionary<string, IReadOnlyCollection<string>>(current.Injected, StringComparer.Ordinal);
        var outputs = new HashSet<string>(current.Outputs, StringComparer.Ordinal);
        foreach (var entry in list.Registry)
        {
            injected[entry.Key] = entry.Value;
            outputs.Remove(entry.Key);
        }

        foreach (var name in list.OutputRegistry)
        {
            outputs.Add(name);
        }

        Volatile.Write(ref _current, new Snapshot(injected, outputs, true));
    }

    private sealed class Snapshot(Dictionary<string, IReadOnlyCollection<string>> injected, HashSet<string> outputs, bool hasListing)
    {
        internal Dictionary<string, IReadOnlyCollection<string>> Injected { get; } = injected;

        internal HashSet<string> Outputs { get; } = outputs;

        internal bool HasListing { get; } = hasListing;
    }
}
