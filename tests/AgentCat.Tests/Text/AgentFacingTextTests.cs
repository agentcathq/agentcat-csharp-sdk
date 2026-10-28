using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentCat.Internal.Text;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Text;

public class AgentFacingTextTests
{
    private const string SampleSessionId = "ses_2cOHEO0LYGADMzRvWTXXVbbgxgm";

    private static readonly Dictionary<string, string> Constants = new(StringComparer.Ordinal)
    {
        ["DEFAULT_CONTEXT_PARAMETER_DESCRIPTION"] = AgentFacingText.DefaultContextParameterDescription,
        ["SESSION_ID_PARAM_DESCRIPTION"] = AgentFacingText.SessionIdParamDescription,
        ["SESSION_ID_PARAM_PATTERN"] = AgentFacingText.SessionIdParamPattern,
        ["AGENT_ID_PARAM_DESCRIPTION"] = AgentFacingText.AgentIdParamDescription,
        ["MINT_BACK_HEADER_ISSUED"] = AgentFacingText.MintBackHeaderIssued,
        ["MINT_BACK_ISSUED_BODY"] = AgentFacingText.MintBackIssuedBody,
        ["MINT_BACK_HEADER_UNRECOGNIZED"] = AgentFacingText.MintBackHeaderUnrecognized,
        ["MINT_BACK_UNRECOGNIZED_BODY"] = AgentFacingText.MintBackUnrecognizedBody,
        ["MCP_SESSION_FIELD_DESCRIPTION"] = AgentFacingText.McpSessionFieldDescription,
        ["MCP_SESSION_FIELD_DESCRIPTION_HOOK_MODE"] = AgentFacingText.McpSessionFieldDescriptionHookMode,
        ["MCP_SESSION_SESSION_ID_DESCRIPTION"] = AgentFacingText.McpSessionSessionIdDescription,
        ["MCP_SESSION_AGENT_ID_DESCRIPTION"] = AgentFacingText.McpSessionAgentIdDescription,
        ["MCP_SESSION_STATUS_DESCRIPTION"] = AgentFacingText.McpSessionStatusDescription,
        ["GET_MORE_TOOLS_DESCRIPTION"] = AgentFacingText.GetMoreToolsDescription,
        ["GET_MORE_TOOLS_CONTEXT_DESCRIPTION"] = AgentFacingText.GetMoreToolsContextDescription,
        ["GET_MORE_TOOLS_RESPONSE_TEXT"] = AgentFacingText.GetMoreToolsReplyText,
        ["MINTED_BLOCK_EXAMPLE"] = ComposedIssued(),
        ["UNRECOGNIZED_BLOCK"] = ComposedUnrecognized(),
    };

    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "GET_MORE_TOOLS_DESCRIPTOR",
        "GET_MORE_TOOLS_RESULT",
        "UNRECOGNIZED_BLOCK",
    };

    private static string ComposedIssued()
        => AgentFacingText.MintBackHeaderIssued + "\n" + AgentFacingText.MintBackSessionLine(SampleSessionId)
            + "\n" + AgentFacingText.MintBackIssuedBody;

    private static string ComposedUnrecognized()
        => AgentFacingText.MintBackHeaderUnrecognized + "\n" + AgentFacingText.MintBackUnrecognizedBody;

    public static TheoryData<string> StringKeys()
    {
        var data = new TheoryData<string>();
        foreach (var kv in Fixtures.Json("handles/strings.json").AsObject())
        {
            if (Excluded.Contains(kv.Key) || kv.Key.StartsWith("MINTED_BLOCK_FOR_", StringComparison.Ordinal))
            {
                continue;
            }

            data.Add(kv.Key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StringKeys))]
    public void Constant_matches_fixture(string key)
    {
        var expected = (string)Fixtures.Json("handles/strings.json").AsObject()[key]!;
        Assert.Equal(expected, Constants[key]);
    }

    [Fact]
    public void Composed_blocks_match_fixture()
    {
        var json = Fixtures.Json("handles/strings.json").AsObject();
        Assert.Equal((string)json["MINTED_BLOCK_FOR_" + SampleSessionId]!, ComposedIssued());
        Assert.Equal((string)json["UNRECOGNIZED_BLOCK"]!, ComposedUnrecognized());
    }

#pragma warning disable CA1850 // net472 has no static SHA256.HashData
    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var b in sha.ComputeHash(bytes))
        {
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
#pragma warning restore CA1850

    private static int Number(string field)
        => int.Parse(field.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Get_more_tools_descriptor_strings_match()
    {
        var descriptor = Fixtures.Json("handles/strings.json")["GET_MORE_TOOLS_DESCRIPTOR"]!;
        Assert.Equal(AgentFacingText.GetMoreToolsDescription, (string)descriptor["description"]!);
        Assert.Equal(AgentFacingText.GetMoreToolsContextDescription,
            (string)descriptor["inputSchema"]!["properties"]!["context"]!["description"]!);
    }

    [Fact]
    public void Sha256_and_lengths_match_golden()
    {
        var checkedCount = 0;
        foreach (var line in Fixtures.Text("handles/golden.txt").Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 4 || !parts[1].StartsWith("len_utf16=", StringComparison.Ordinal)
                || !Constants.TryGetValue(parts[0], out var value))
            {
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            Assert.Equal(Number(parts[1]), value.Length);
            Assert.Equal(Number(parts[2]), bytes.Length);
            Assert.Equal(parts[3].Substring("sha256=".Length),
                Sha256Hex(bytes));
            checkedCount++;
        }

        Assert.Equal(18, checkedCount);
    }

    [Fact]
    public void Get_more_tools_reply_matches()
    {
        var text = (string)Fixtures.Json("handles/strings.json")["GET_MORE_TOOLS_RESULT"]!["content"]![0]!["text"]!;
        Assert.Equal(AgentFacingText.GetMoreToolsReplyText, text);
    }

    [Fact]
    public void Wire_names_are_pinned()
    {
        Assert.Equal("mcp:tools/call", WireNames.ToolCallEventType);
        Assert.Equal("agentcat:custom", WireNames.CustomEventType);
        Assert.Equal("agentcat", WireNames.Source);
        Assert.Equal("io.modelcontextprotocol/clientInfo", WireNames.MetaClientInfoKey);
        Assert.Equal("io.modelcontextprotocol/protocolVersion", WireNames.MetaProtocolVersionKey);
        Assert.Equal("agentcat_agent_id", WireNames.TagAgentId);
        Assert.Equal("agentcat_session_id_source", WireNames.TagSessionSource);
        Assert.Equal("agentcat_agent_id_source", WireNames.TagAgentSource);
        Assert.Equal("agentcat_protocol_version", WireNames.TagProtocolVersion);
        Assert.Equal("agentcat_mrtr", WireNames.TagMrtr);
        Assert.Equal("session_id", WireNames.SessionIdParam);
        Assert.Equal("agent_id", WireNames.AgentIdParam);
        Assert.Equal("context", WireNames.ContextParam);
        Assert.Equal("mcp_session", WireNames.McpSessionKey);
        Assert.Equal("get_more_tools", WireNames.GetMoreToolsName);
        Assert.Equal("ses", WireNames.SessionPrefix);
        Assert.Equal("evt", WireNames.EventPrefix);
        Assert.Equal("start", AgentFacingText.SessionStartSentinel);
        Assert.Equal("session_id: abc", AgentFacingText.MintBackSessionLine("abc"));
    }
}
