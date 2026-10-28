namespace AgentCat.Internal.Injection;

internal sealed record InjectionSettings(
    bool EnableReportMissing,
    bool InjectSessionId,
    bool InjectAgentId,
    bool EnableToolCallContext,
    string? CustomContextDescription);
