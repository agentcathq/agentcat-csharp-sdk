using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentCat.Internal.Handles;
using AgentCat.Internal.Text;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Handles;

public class MintBackTests
{
    private const string T = "ses_2xF9kQm3rTvB8nLpYw7ZcHd4Ke1";
    private const string A = "opus-4.80-1m|claude-code|k3n9x";
    private const string GoldenId = "ses_2cOHEO0LYGADMzRvWTXXVbbgxgm";

    private static readonly HandleResolution InvalidRes = new(string.Empty, SessionSource.Invalid, false, null);
    private static readonly HandleResolution ForeignRes = new(string.Empty, SessionSource.Foreign, false, null);

    private static string MintedBlock(string id) =>
        "[session_id issued — see this tool's session_id parameter description]\n"
        + "session_id: " + id + "\n"
        + "This is the first-call issuance described in this tool's session_id parameter description.";

    private static string[] Keys(JsonObject obj) => obj.Select(p => p.Key).ToArray();

    private static string[] Strings(params string[] values) => values;

    private static JsonObject Mint() => new() { ["session_id"] = T, ["status"] = "issued" };

    [Fact]
    public void Task_minted_task_only_block_no_agent_mention_even_when_agent_supplied()
    {
        var res = new HandleResolution(T, SessionSource.Minted, false, A);

        Assert.Equal(MintedBlock(T), MintBack.BuildText(res));
    }

    [Fact]
    public void Task_minted_without_agent_tracking_same_task_only_block()
    {
        var res = new HandleResolution(T, SessionSource.Minted, false, null);

        Assert.Equal(MintedBlock(T), MintBack.BuildText(res));
    }

    [Fact]
    public void Task_supplied_null_nothing_to_announce_agent_never_mints()
    {
        var res = new HandleResolution(T, SessionSource.Supplied, false, A);

        Assert.Null(MintBack.BuildText(res));
    }

    [Fact]
    public void Hook_mode_never_emits_task_instructions_even_when_hook_null_minted()
    {
        Assert.Null(MintBack.BuildText(new HandleResolution(T, SessionSource.Minted, true, A)));
        Assert.Null(MintBack.BuildText(new HandleResolution(string.Empty, SessionSource.Invalid, true, null)));
        Assert.Null(MintBack.BuildText(new HandleResolution(string.Empty, SessionSource.Hook, true, null)));
    }

    [Fact]
    public void Minted_text_equals_the_golden_block()
    {
        var res = new HandleResolution(GoldenId, SessionSource.Minted, false, null);

        Assert.Equal((string)Fixtures.Json("handles/strings.json")["MINTED_BLOCK_FOR_" + GoldenId]!, MintBack.BuildText(res));
    }

    [Fact]
    public void Unrecognized_text_equals_the_golden_block()
    {
        Assert.Equal((string)Fixtures.Json("handles/strings.json")["UNRECOGNIZED_BLOCK"]!, MintBack.BuildText(InvalidRes));
    }

    [Fact]
    public void Text_for_a_resolved_call_matches_its_source()
    {
        var minted = HandleResolver.Resolve(null, false, false, true, DateTimeOffset.UtcNow);

        Assert.Equal(MintedBlock(minted.SessionId), MintBack.BuildText(minted));
    }

    [Fact]
    public void Task_minted_and_agent_supplied_both_ids_status_issued()
    {
        var mint = MintBack.BuildStructured(new HandleResolution(T, SessionSource.Minted, false, A))!;

        Assert.Equal(T, (string)mint["session_id"]!);
        Assert.Equal(A, (string)mint["agent_id"]!);
        Assert.Equal("issued", (string)mint["status"]!);
        Assert.Equal(Strings("session_id", "agent_id", "status"), Keys(mint));
        Assert.Equal("{\"session_id\":\"" + T + "\",\"agent_id\":\"" + A + "\",\"status\":\"issued\"}", mint.ToJsonString());
    }

    [Fact]
    public void Steady_state_both_supplied_ids_plus_status_active()
    {
        var mint = MintBack.BuildStructured(new HandleResolution(T, SessionSource.Supplied, false, A))!;

        Assert.Equal(T, (string)mint["session_id"]!);
        Assert.Equal(A, (string)mint["agent_id"]!);
        Assert.Equal("active", (string)mint["status"]!);
    }

    [Fact]
    public void Agent_tracking_off_task_only_status_active()
    {
        var mint = MintBack.BuildStructured(new HandleResolution(T, SessionSource.Supplied, false, null))!;

        Assert.Equal(T, (string)mint["session_id"]!);
        Assert.False(mint.ContainsKey("agent_id"));
        Assert.Equal("active", (string)mint["status"]!);
        Assert.Equal(Strings("session_id", "status"), Keys(mint));
    }

    [Fact]
    public void Hook_mode_never_exposes_session_id_or_status_supplied_agent_is_echoed()
    {
        var mint = MintBack.BuildStructured(new HandleResolution(T, SessionSource.Minted, true, A))!;

        Assert.False(mint.ContainsKey("session_id"));
        Assert.False(mint.ContainsKey("status"));
        Assert.Equal(A, (string)mint["agent_id"]!);
        Assert.Equal(Strings("agent_id"), Keys(mint));
    }

    [Fact]
    public void Hook_mode_with_agent_tracking_off_nothing_echoable_null()
    {
        Assert.Null(MintBack.BuildStructured(new HandleResolution(T, SessionSource.Hook, true, null)));
    }

    [Fact]
    public void An_empty_agent_id_is_not_echoed()
    {
        var mint = MintBack.BuildStructured(new HandleResolution(T, SessionSource.Supplied, false, string.Empty))!;

        Assert.Equal(Strings("session_id", "status"), Keys(mint));
        Assert.Null(MintBack.BuildStructured(new HandleResolution(T, SessionSource.Hook, true, string.Empty)));
    }

    [Fact]
    public void Structured_mint_backs_are_fresh_objects()
    {
        var res = new HandleResolution(T, SessionSource.Supplied, false, A);

        Assert.NotSame(MintBack.BuildStructured(res), MintBack.BuildStructured(res));
        Assert.Null(MintBack.BuildStructured(res)!.Parent);
    }

    [Fact]
    public void Adds_the_field_as_the_FIRST_key_of_structuredContent_without_mutating()
    {
        var original = new JsonObject { ["a"] = 1 };
        var mint = Mint();

        var output = MintBack.MirrorStructured(original, mint)!;

        Assert.True(JsonNode.DeepEquals(mint, output["mcp_session"]));
        Assert.Equal(1, (int)output["a"]!);
        Assert.Equal(Strings("mcp_session", "a"), Keys(output));
        Assert.Equal("{\"a\":1}", original.ToJsonString());
        Assert.NotSame(original, output);
    }

    [Fact]
    public void Never_clobbers_an_existing_key_customer_data_wins()
    {
        var sc = new JsonObject { ["mcp_session"] = "customer-owned" };

        Assert.Null(MintBack.MirrorStructured(sc, Mint()));
        Assert.Equal("{\"mcp_session\":\"customer-owned\"}", sc.ToJsonString());
    }

    [Fact]
    public void A_customer_mcp_session_key_wins_even_when_null()
    {
        var sc = JsonNode.Parse("{\"x\":1,\"mcp_session\":null}")!.AsObject();

        Assert.Null(MintBack.MirrorStructured(sc, Mint()));
    }

    [Fact]
    public void Applies_to_isError_results_that_carry_structuredContent()
    {
        var output = MintBack.MirrorStructured(new JsonObject { ["msg"] = "boom" }, Mint())!;

        Assert.True(JsonNode.DeepEquals(Mint(), output["mcp_session"]));
        Assert.Equal(Strings("mcp_session", "msg"), Keys(output));
    }

    [Fact]
    public void Mirror_matches_the_key_ordinally()
    {
        var sc = new JsonObject(new JsonNodeOptions { PropertyNameCaseInsensitive = true }) { ["MCP_SESSION"] = 1 };

        var output = MintBack.MirrorStructured(sc, Mint())!;

        Assert.Equal(Strings("mcp_session", "MCP_SESSION"), Keys(output));
    }

    [Fact]
    public void Mirror_keeps_customer_key_order_and_values()
    {
        var sc = JsonNode.Parse("{\"z\":{\"deep\":[1,{\"k\":null}]},\"a\":\"x\",\"m\":null,\"n\":false}")!.AsObject();

        var output = MintBack.MirrorStructured(sc, Mint())!;

        Assert.Equal(
            "{\"mcp_session\":{\"session_id\":\"" + T + "\",\"status\":\"issued\"},\"z\":{\"deep\":[1,{\"k\":null}]},\"a\":\"x\",\"m\":null,\"n\":false}",
            output.ToJsonString());
    }

    [Fact]
    public void Mirror_never_moves_or_shares_the_inputs()
    {
        var sc = JsonNode.Parse("{\"z\":{\"deep\":[1]}}")!.AsObject();
        var mint = Mint();

        var first = MintBack.MirrorStructured(sc, mint)!;
        var second = MintBack.MirrorStructured(sc, mint)!;
        first["z"]!["deep"]!.AsArray().Add(2);
        first["mcp_session"]!["status"] = "changed";

        Assert.Null(mint.Parent);
        Assert.Equal("{\"z\":{\"deep\":[1]}}", sc.ToJsonString());
        Assert.Equal("issued", (string)mint["status"]!);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal("{\"deep\":[1]}", second["z"]!.ToJsonString());
    }

    [Fact]
    public void Mirror_accepts_a_mint_already_attached_elsewhere()
    {
        var holder = new JsonObject { ["held"] = Mint() };

        var output = MintBack.MirrorStructured(new JsonObject { ["a"] = 1 }, holder["held"]!.AsObject())!;

        Assert.True(JsonNode.DeepEquals(Mint(), output["mcp_session"]));
        Assert.Same(holder, holder["held"]!.Parent);
    }

    [Fact]
    public void StructuredContent_only_consumer_discovers_minted_handles()
    {
        var res = HandleResolver.Resolve(null, false, agentTracking: true, true, DateTimeOffset.UtcNow);
        var sc = new JsonObject { ["count"] = 0 };

        var output = MintBack.MirrorStructured(sc, MintBack.BuildStructured(res)!)!;

        var session = output["mcp_session"]!.AsObject();
        Assert.Matches(@"^ses_[0-9A-Za-z]{27}\z", (string)session["session_id"]!);
        Assert.False(session.ContainsKey("agent_id"));
        Assert.Equal("issued", (string)session["status"]!);
        Assert.Equal(0, (int)output["count"]!);
        Assert.Equal("mcp_session", Keys(output)[0]);
    }

    [Fact]
    public void Steady_state_supplied_handles_are_re_echoed_on_every_response_text_block_stays_mint_only()
    {
        var res = new HandleResolution("ses_fixed000000000000000000000", SessionSource.Supplied, false, A);

        var mint = MintBack.BuildStructured(res)!;

        Assert.Equal("{\"session_id\":\"ses_fixed000000000000000000000\",\"agent_id\":\"" + A + "\",\"status\":\"active\"}", mint.ToJsonString());
        Assert.Null(MintBack.BuildText(res));
    }

    [Fact]
    public void Wire_only_the_customer_structured_content_never_gains_mcp_session()
    {
        var sc = new JsonObject { ["count"] = 0 };

        MintBack.MirrorStructured(sc, Mint());

        Assert.Equal("{\"count\":0}", sc.ToJsonString());
    }

    [Fact]
    public void Hook_mode_mirrored_payload_carries_agent_id_but_never_session_id_or_status()
    {
        var res = new HandleResolution(string.Empty, SessionSource.Hook, true, A);

        var output = MintBack.MirrorStructured(new JsonObject { ["count"] = 42 }, MintBack.BuildStructured(res)!)!;

        Assert.Equal("{\"mcp_session\":{\"agent_id\":\"" + A + "\"},\"count\":42}", output.ToJsonString());
    }

    [Fact]
    public void Invalid_corrects_the_agent_without_issuing_a_replacement()
    {
        var text = MintBack.BuildText(InvalidRes)!;

        Assert.Equal(
            "[session_id unrecognized — see this tool's session_id parameter description]\n"
            + "The value sent was not issued by this server. Re-send the session_id issued earlier for this task; if none was issued yet, send start and one will be issued.",
            text);
        Assert.DoesNotMatch(new Regex("ses_[0-9A-Za-z]{27}"), text);
    }

    [Fact]
    public void Invalid_offers_an_escape_to_an_agent_that_was_never_issued_one()
    {
        Assert.Contains("if none was issued yet, send start and one will be issued.", MintBack.BuildText(InvalidRes)!, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_structured_mirror_carries_status_unrecognized_but_no_session_id()
    {
        var mint = MintBack.BuildStructured(InvalidRes)!;

        Assert.Equal("unrecognized", (string)mint["status"]!);
        Assert.False(mint.ContainsKey("session_id"));
        Assert.Equal(Strings("status"), Keys(mint));
    }

    [Fact]
    public void Invalid_and_agent_supplied_agent_id_rides_along_with_the_unrecognized_status()
    {
        var mint = MintBack.BuildStructured(new HandleResolution(string.Empty, SessionSource.Invalid, false, "opus|cc|k3n9x"))!;

        Assert.Equal("unrecognized", (string)mint["status"]!);
        Assert.False(mint.ContainsKey("session_id"));
        Assert.Equal("opus|cc|k3n9x", (string)mint["agent_id"]!);
        Assert.Equal(Strings("agent_id", "status"), Keys(mint));
    }

    [Fact]
    public void Foreign_says_nothing_when_agent_id_is_not_in_play_either()
    {
        Assert.Null(MintBack.BuildText(ForeignRes));
        Assert.Null(MintBack.BuildStructured(ForeignRes));
    }

    [Fact]
    public void Foreign_never_echoes_session_state_which_is_the_customers()
    {
        var mint = MintBack.BuildStructured(new HandleResolution(string.Empty, SessionSource.Foreign, false, "opus|cc|k3n9x"))!;

        Assert.False(mint.ContainsKey("session_id"));
        Assert.False(mint.ContainsKey("status"));
    }

    [Fact]
    public void Foreign_still_echoes_agent_id_which_AgentCat_did_inject()
    {
        var mint = MintBack.BuildStructured(new HandleResolution(string.Empty, SessionSource.Foreign, false, "opus|cc|k3n9x"))!;

        Assert.Equal("opus|cc|k3n9x", (string)mint["agent_id"]!);
        Assert.Equal(Strings("agent_id"), Keys(mint));
    }

    [Fact]
    public void Status_values_match_the_output_schema_enum()
    {
        Assert.Equal(
            Strings("issued", "active", "unrecognized"),
            Strings(WireNames.McpSessionStatusIssued, WireNames.McpSessionStatusActive, WireNames.McpSessionStatusUnrecognized));
    }
}
