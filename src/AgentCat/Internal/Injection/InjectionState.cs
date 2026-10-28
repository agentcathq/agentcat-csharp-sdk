using System;
using System.Collections.Concurrent;
using System.Threading;

namespace AgentCat.Internal.Injection;

internal sealed class InjectionState
{
    private readonly ConcurrentDictionary<string, byte> _reportedConflicts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _declaredSessionParams = new(StringComparer.Ordinal);
    private int _customerOwnedGetMoreToolsWarned;

    internal bool TryReportConflict(string toolName) => _reportedConflicts.TryAdd(toolName, 0);

    internal void DeclareSessionParam(string toolName) => _declaredSessionParams.TryAdd(toolName, 0);

    internal bool IsSessionParamDeclared(string toolName) => _declaredSessionParams.ContainsKey(toolName);

    internal bool TryWarnCustomerOwnedGetMoreTools() => Interlocked.Exchange(ref _customerOwnedGetMoreToolsWarned, 1) == 0;
}
