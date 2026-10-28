using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace AgentCat.Internal.Injection;

internal sealed record InjectedList(
    IReadOnlyList<JsonObject?> Tools,
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> Registry,
    IReadOnlyCollection<string> OutputRegistry);
