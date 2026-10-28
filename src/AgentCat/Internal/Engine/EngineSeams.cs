using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Internal.Engine;

internal sealed class EngineSeams
{
    internal EngineSeams(AgentCatTracker tracker)
    {
        List = new ListToolsSeam(tracker).Wrap;
        Outer = new CallToolSeam(tracker).Wrap;
        Inner = new InnerTap(tracker).Wrap;
        Outgoing = new OutgoingSeam(tracker).Wrap;
    }

    internal McpRequestFilter<ListToolsRequestParams, ListToolsResult> List { get; }

    internal McpRequestFilter<CallToolRequestParams, CallToolResult> Outer { get; }

    internal McpRequestFilter<CallToolRequestParams, CallToolResult> Inner { get; }

    internal McpMessageFilter Outgoing { get; }
}
