namespace AgentCat.Internal.Text;

internal static class WireNames
{
    internal const string ToolCallEventType = "mcp:tools/call";
    internal const string CustomEventType = "agentcat:custom";
    internal const string Source = "agentcat";
    internal const string MetaClientInfoKey = "io.modelcontextprotocol/clientInfo";
    internal const string MetaProtocolVersionKey = "io.modelcontextprotocol/protocolVersion";
    internal const string TagAgentId = "agentcat_agent_id";
    internal const string TagSessionSource = "agentcat_session_id_source";
    internal const string TagAgentSource = "agentcat_agent_id_source";
    internal const string TagProtocolVersion = "agentcat_protocol_version";
    internal const string TagMrtr = "agentcat_mrtr";
    internal const string MrtrContinuation = "continuation";
    internal const string MrtrInputRequired = "input_required";
    internal const string SessionIdParam = "session_id";
    internal const string AgentIdParam = "agent_id";
    internal const string ContextParam = "context";
    internal const string McpSessionKey = "mcp_session";
    internal const string McpSessionStatusKey = "status";
    internal const string McpSessionStatusIssued = "issued";
    internal const string McpSessionStatusActive = "active";
    internal const string McpSessionStatusUnrecognized = "unrecognized";
    internal const string AgentSourceSupplied = "supplied";
    internal const string GetMoreToolsName = "get_more_tools";
    internal const string SessionPrefix = "ses";
    internal const string EventPrefix = "evt";
}
