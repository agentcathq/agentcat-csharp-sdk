using System.Text;
using System.Text.Json;
using AgentCat.Internal.Handles;
using AgentCat.Internal.Ids;

namespace AgentCat.Tests.Handles;

public class HandleResolverTests
{
    private const string SessionShape = @"^ses_[0-9A-Za-z]{27}\z";
    private const string Agent = "opus-4.80-1m|claude-code|k3n9x";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);

    private static string Sid(string label)
    {
        var body = new StringBuilder();
        foreach (var c in label)
        {
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                body.Append(c);
            }
        }

        body.Append('0', 27);
        return "ses_" + body.ToString(0, 27);
    }

    private static Dictionary<string, JsonElement> Args(string json) => Args(Encoding.UTF8.GetBytes(json));

    private static Dictionary<string, JsonElement> Args(byte[] utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            args[property.Name] = property.Value.Clone();
        }

        return args;
    }

    private static Dictionary<string, JsonElement> SessionArg(string value)
        => Args("{\"session_id\":" + JsonSerializer.Serialize(value) + "}");

    private static HandleResolution Prompted(IReadOnlyDictionary<string, JsonElement>? args, bool agentTracking = false)
        => HandleResolver.Resolve(args, hookMode: false, agentTracking, sessionParamIsOurs: true, Now);

    [Fact]
    public void ExtractHandle_returns_trimmed_non_empty_strings_only()
    {
        Assert.Equal("ses_x", HandleResolver.ExtractHandle(Args("{\"session_id\":\" ses_x \"}"), "session_id"));
        Assert.Null(HandleResolver.ExtractHandle(Args("{\"session_id\":\"\"}"), "session_id"));
        Assert.Null(HandleResolver.ExtractHandle(Args("{\"session_id\":\"   \"}"), "session_id"));
        Assert.Null(HandleResolver.ExtractHandle(Args("{\"session_id\":42}"), "session_id"));
        Assert.Null(HandleResolver.ExtractHandle(null, "session_id"));
        Assert.Null(HandleResolver.ExtractHandle(null, "agent_id"));
        Assert.Null(HandleResolver.ExtractHandle(Args("{}"), "session_id"));
    }

    [Fact]
    public void ExtractHandle_returns_any_non_empty_string_validation_happens_in_resolveHandles()
    {
        Assert.Equal(
            "my-own-correlation-id",
            HandleResolver.ExtractHandle(Args("{\"session_id\":\"my-own-correlation-id\"}"), "session_id"));
    }

    [Theory]
    [InlineData("{\"session_id\":null}")]
    [InlineData("{\"session_id\":true}")]
    [InlineData("{\"session_id\":{}}")]
    [InlineData("{\"session_id\":[\"x\"]}")]
    [InlineData("{\"session_id\":\"\\u00A0\\uFEFF\\u3000\"}")]
    public void ExtractHandle_ignores_non_strings_and_JS_whitespace(string json)
    {
        Assert.Null(HandleResolver.ExtractHandle(Args(json), "session_id"));
    }

    [Fact]
    public void ExtractHandle_ignores_a_default_element()
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["session_id"] = default };

        Assert.Null(HandleResolver.ExtractHandle(args, "session_id"));
    }

    [Fact]
    public void ExtractHandle_is_case_sensitive_on_the_name()
    {
        Assert.Null(HandleResolver.ExtractHandle(Args("{\"Session_Id\":\"x\"}"), "session_id"));
    }

    [Fact]
    public void ExtractHandle_keeps_a_lone_surrogate_like_JSON_parse()
    {
        Assert.Equal("a\uD800b", HandleResolver.ExtractHandle(Args("{\"agent_id\":\" a\\ud800b \"}"), "agent_id"));
        Assert.Equal("\uDC00", HandleResolver.ExtractHandle(Args("{\"agent_id\":\"\\udc00\"}"), "agent_id"));
        Assert.Equal(
            "x\n\"\\/\b\f\r\t\uD83D\uDE00\u00E9y\uD800",
            HandleResolver.ExtractHandle(Args("{\"agent_id\":\"x\\n\\\"\\\\\\/\\b\\f\\r\\t\\ud83d\\ude00\u00E9y\\ud800\"}"), "agent_id"));
        Assert.Equal("ab\n\uD800", HandleResolver.ExtractHandle(Args("{\"agent_id\":\"\\u0020a\\u0062\\n\\ud800\"}"), "agent_id"));
        Assert.Equal("a b\uDBFF", HandleResolver.ExtractHandle(Args("{\"agent_id\":\"\\t a\\u0020b\\udbff\\u3000\"}"), "agent_id"));
    }

    [Fact]
    public void ExtractHandle_replaces_invalid_UTF8_like_a_JS_decoder()
    {
        var json = Encoding.UTF8.GetBytes("{\"agent_id\":\"a?b\\u0041\"}");
        json[Array.IndexOf(json, (byte)'?')] = 0xFF;

        Assert.Equal("a\uFFFDbA", HandleResolver.ExtractHandle(Args(json), "agent_id"));
    }

    [Fact]
    public void Mints_the_task_but_never_the_agent_when_nothing_supplied_agent_tracking_on()
    {
        var res = Prompted(Args("{}"), agentTracking: true);

        Assert.False(res.HookMode);
        Assert.Equal(SessionSource.Minted, res.SessionSource);
        Assert.Matches(SessionShape, res.SessionId);
        Assert.Null(res.AgentId);
    }

    [Fact]
    public void Subagent_flow_supplied_task_and_omitted_agent_leaves_the_agent_unresolved()
    {
        var res = Prompted(SessionArg(Sid("parent")), agentTracking: true);

        Assert.Equal(SessionSource.Supplied, res.SessionSource);
        Assert.Equal(Sid("parent"), res.SessionId);
        Assert.Null(res.AgentId);
    }

    [Fact]
    public void Uses_supplied_handles_verbatim()
    {
        var res = Prompted(
            Args("{\"session_id\":\"" + Sid("anythinggoes") + "\",\"agent_id\":\" opus-4.80-1m|claude-code|k3n9x \"}"),
            agentTracking: true);

        Assert.Equal(SessionSource.Supplied, res.SessionSource);
        Assert.Equal(Sid("anythinggoes"), res.SessionId);
        Assert.Equal(Agent, res.AgentId);
    }

    [Fact]
    public void EnableAgentTracking_false_resolves_no_agent_id_at_all()
    {
        var res = Prompted(Args("{\"agent_id\":\"agt_ignored\"}"), agentTracking: false);

        Assert.Null(res.AgentId);
    }

    [Fact]
    public void Agent_tracking_is_off_by_default_omitted_option_resolves_no_agent_id()
    {
        var res = Prompted(Args("{\"agent_id\":\"agt_ignored\"}"));

        Assert.Equal(SessionSource.Minted, res.SessionSource);
        Assert.Null(res.AgentId);
    }

    [Fact]
    public void Rejects_a_malformed_supplied_session_id_sessionless_source_invalid()
    {
        var res = Prompted(SessionArg("nope"));

        Assert.Equal(SessionSource.Invalid, res.SessionSource);
        Assert.Equal(string.Empty, res.SessionId);
    }

    [Fact]
    public void Never_stores_the_raw_rejected_value()
    {
        var res = Prompted(SessionArg("sk_live_secret_token"));

        Assert.Equal(string.Empty, res.SessionId);
        Assert.DoesNotContain("sk_live_secret_token", res.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_a_well_formed_supplied_session_id_verbatim()
    {
        var res = Prompted(SessionArg(Sid("parent")));

        Assert.Equal(SessionSource.Supplied, res.SessionSource);
        Assert.Equal(Sid("parent"), res.SessionId);
    }

    [Fact]
    public void Foreign_param_sessionless_regardless_of_what_the_agent_sent()
    {
        var withValue = HandleResolver.Resolve(SessionArg("customer-value"), false, false, sessionParamIsOurs: false, Now);
        var withoutValue = HandleResolver.Resolve(Args("{}"), false, false, sessionParamIsOurs: false, Now);
        var withOurShape = HandleResolver.Resolve(SessionArg(Sid("parent")), false, false, sessionParamIsOurs: false, Now);

        Assert.Equal(SessionSource.Foreign, withValue.SessionSource);
        Assert.Equal(string.Empty, withValue.SessionId);
        Assert.Equal(SessionSource.Foreign, withoutValue.SessionSource);
        Assert.Equal(string.Empty, withoutValue.SessionId);
        Assert.Equal(SessionSource.Foreign, withOurShape.SessionSource);
        Assert.Equal(string.Empty, withOurShape.SessionId);
    }

    [Fact]
    public void Hook_mode_wins_over_foreign_arguments_are_never_read()
    {
        var res = HandleResolver.Resolve(SessionArg("customer-value"), hookMode: true, false, sessionParamIsOurs: false, Now);

        Assert.True(res.HookMode);
        Assert.Equal(SessionSource.Hook, res.SessionSource);
        Assert.Equal(string.Empty, res.SessionId);
    }

    [Fact]
    public void Still_mints_when_nothing_is_supplied_on_our_own_param()
    {
        var res = Prompted(Args("{}"));

        Assert.Equal(SessionSource.Minted, res.SessionSource);
        Assert.Matches(SessionShape, res.SessionId);
    }

    [Fact]
    public void Start_sentinel_resolves_exactly_like_omission_fresh_mint_source_minted()
    {
        var res = Prompted(SessionArg("start"));
        var again = Prompted(SessionArg("start"));

        Assert.Equal(SessionSource.Minted, res.SessionSource);
        Assert.Matches(SessionShape, res.SessionId);
        Assert.NotEqual(res.SessionId, again.SessionId);
    }

    [Theory]
    [InlineData("START")]
    [InlineData("Start")]
    [InlineData("sTaRt")]
    [InlineData("  start  ")]
    [InlineData(" START\t")]
    [InlineData("  Start\n")]
    [InlineData("\u00A0start\uFEFF")]
    [InlineData("\u2028sTART\u3000\u2029")]
    [InlineData("\u000Bstart\u000C")]
    public void Start_sentinel_is_case_insensitive_and_whitespace_tolerant(string value)
    {
        var res = Prompted(SessionArg(value));

        Assert.Equal(SessionSource.Minted, res.SessionSource);
        Assert.Matches(SessionShape, res.SessionId);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("start now")]
    [InlineData("starts")]
    [InlineData("star t")]
    public void Near_sentinel_values_stay_on_the_invalid_path(string value)
    {
        var res = Prompted(SessionArg(value));

        Assert.Equal(SessionSource.Invalid, res.SessionSource);
        Assert.Equal(string.Empty, res.SessionId);
    }

    [Theory]
    [InlineData("\u017Ftart")]
    [InlineData("\u017FTART")]
    [InlineData("\u0085start")]
    [InlineData("start\u0085")]
    [InlineData("\u200Bstart")]
    [InlineData("\uFF53tart")]
    public void Lookalike_sentinels_are_not_start(string value)
    {
        var res = Prompted(SessionArg(value));

        Assert.Equal(SessionSource.Invalid, res.SessionSource);
        Assert.Equal(string.Empty, res.SessionId);
    }

    [Fact]
    public void Foreign_param_start_is_the_customers_value_never_a_sentinel()
    {
        var res = HandleResolver.Resolve(SessionArg("start"), false, false, sessionParamIsOurs: false, Now);

        Assert.Equal(SessionSource.Foreign, res.SessionSource);
        Assert.Equal(string.Empty, res.SessionId);
    }

    [Theory]
    [InlineData("{\"session_id\":123}")]
    [InlineData("{\"session_id\":0}")]
    [InlineData("{\"session_id\":{}}")]
    [InlineData("{\"session_id\":{\"id\":\"ses_parent000000000000000000000\"}}")]
    [InlineData("{\"session_id\":null}")]
    [InlineData("{\"session_id\":true}")]
    [InlineData("{\"session_id\":false}")]
    [InlineData("{\"session_id\":[\"ses_parent000000000000000000000\"]}")]
    [InlineData("{\"session_id\":\"\"}")]
    [InlineData("{\"session_id\":\" \\t\\n \"}")]
    [InlineData("{\"Session_ID\":\"ses_parent000000000000000000000\"}")]
    public void Non_string_session_id_mints(string json)
    {
        var res = Prompted(Args(json));

        Assert.Equal(SessionSource.Minted, res.SessionSource);
        Assert.Matches(SessionShape, res.SessionId);
    }

    [Fact]
    public void Null_arguments_mint()
    {
        var res = HandleResolver.Resolve(null, false, agentTracking: true, true, Now);

        Assert.Equal(SessionSource.Minted, res.SessionSource);
        Assert.Matches(SessionShape, res.SessionId);
        Assert.Null(res.AgentId);
    }

    [Fact]
    public void A_minted_session_id_carries_the_given_clock()
    {
        var res = Prompted(null);

        Assert.Equal(Now, Ksuid.Parse(res.SessionId.Substring(4)).Date);
    }

    [Fact]
    public void A_supplied_session_id_is_JS_trimmed_before_validation()
    {
        var res = Prompted(SessionArg("\u3000" + Sid("parent") + "\n\uFEFF"));

        Assert.Equal(SessionSource.Supplied, res.SessionSource);
        Assert.Equal(Sid("parent"), res.SessionId);
    }

    [Theory]
    [InlineData("\u0085")]
    [InlineData("\u180E")]
    [InlineData("\u200B")]
    public void A_session_id_with_a_non_JS_whitespace_unit_is_invalid(string unit)
    {
        var res = Prompted(SessionArg(Sid("parent") + unit));

        Assert.Equal(SessionSource.Invalid, res.SessionSource);
        Assert.Equal(string.Empty, res.SessionId);
    }

    [Fact]
    public void A_session_id_with_an_undecodable_string_is_invalid_and_does_not_throw()
    {
        var res = Prompted(Args("{\"session_id\":\"ses_parent00000000000000000000\\ud800\"}"));

        Assert.Equal(SessionSource.Invalid, res.SessionSource);
        Assert.Equal(string.Empty, res.SessionId);
    }

    [Theory]
    [InlineData("{\"agent_id\":42}")]
    [InlineData("{\"agent_id\":null}")]
    [InlineData("{\"agent_id\":{\"id\":\"a\"}}")]
    [InlineData("{\"agent_id\":\"\"}")]
    [InlineData("{\"agent_id\":\" \\u00A0 \"}")]
    public void Agent_tracking_ignores_unusable_agent_ids(string json)
    {
        var res = Prompted(Args(json), agentTracking: true);

        Assert.Null(res.AgentId);
    }

    [Fact]
    public void Agent_id_resolves_on_foreign_and_invalid_calls_too()
    {
        var foreign = HandleResolver.Resolve(
            Args("{\"session_id\":\"customer-value\",\"agent_id\":\"" + Agent + "\"}"), false, true, false, Now);
        var invalid = Prompted(Args("{\"session_id\":\"nope\",\"agent_id\":\"" + Agent + "\"}"), agentTracking: true);

        Assert.Equal(SessionSource.Foreign, foreign.SessionSource);
        Assert.Equal(Agent, foreign.AgentId);
        Assert.Equal(SessionSource.Invalid, invalid.SessionSource);
        Assert.Equal(Agent, invalid.AgentId);
    }

    [Fact]
    public void Returns_a_provisional_resolution_empty_id_source_hook_hookMode_on()
    {
        var res = HandleResolver.Resolve(Args("{}"), hookMode: true, false, true, Now);

        Assert.True(res.HookMode);
        Assert.Equal(SessionSource.Hook, res.SessionSource);
        Assert.Equal(string.Empty, res.SessionId);
    }

    [Fact]
    public void Ignores_agent_supplied_session_id_the_hook_wins()
    {
        var odd = HandleResolver.Resolve(SessionArg("ses_agent_sent"), hookMode: true, false, true, Now);
        var wellShaped = HandleResolver.Resolve(SessionArg(Sid("parent")), hookMode: true, false, true, Now);
        var start = HandleResolver.Resolve(SessionArg("start"), hookMode: true, false, true, Now);

        Assert.Equal(SessionSource.Hook, odd.SessionSource);
        Assert.Equal(string.Empty, odd.SessionId);
        Assert.Equal(SessionSource.Hook, wellShaped.SessionSource);
        Assert.Equal(string.Empty, wellShaped.SessionId);
        Assert.Equal(SessionSource.Hook, start.SessionSource);
        Assert.Equal(string.Empty, start.SessionId);
    }

    [Fact]
    public void Hook_mode_supplied_agent_id_resolves_omitted_stays_unresolved()
    {
        var supplied = HandleResolver.Resolve(Args("{\"agent_id\":\"" + Agent + "\"}"), true, agentTracking: true, true, Now);
        var omitted = HandleResolver.Resolve(Args("{}"), true, agentTracking: true, true, Now);

        Assert.Equal(Agent, supplied.AgentId);
        Assert.Null(omitted.AgentId);
    }

    [Fact]
    public void Derives_deterministically_from_the_hook_value_and_project()
    {
        var a = HandleResolver.FromHookValue("customer-42", "proj_1", Now);
        var b = HandleResolver.FromHookValue("customer-42", "proj_1", Now);

        Assert.Equal(SessionSource.Hook, a.Source);
        Assert.Equal(a.SessionId, b.SessionId);
        Assert.Matches(SessionShape, a.SessionId);
        Assert.Equal(SessionIds.Derive("customer-42", "proj_1"), a.SessionId);
    }

    [Fact]
    public void Different_project_yields_a_different_derived_id()
    {
        Assert.NotEqual(
            HandleResolver.FromHookValue("customer-abc", "proj_1", Now).SessionId,
            HandleResolver.FromHookValue("customer-abc", "proj_2", Now).SessionId);
        Assert.NotEqual(
            HandleResolver.FromHookValue("customer-abc", null, Now).SessionId,
            HandleResolver.FromHookValue("customer-abc", "proj_1", Now).SessionId);
    }

    [Fact]
    public void Trims_the_value_before_deriving()
    {
        Assert.Equal(SessionIds.Derive("corr-1", "proj_1"), HandleResolver.FromHookValue("  corr-1  ", "proj_1", Now).SessionId);
        Assert.Equal(SessionIds.Derive("corr-1", "proj_1"), HandleResolver.FromHookValue("\u3000corr-1\uFEFF", "proj_1", Now).SessionId);
        Assert.Equal(SessionIds.Derive("\u0085corr-1", "proj_1"), HandleResolver.FromHookValue("\u0085corr-1", "proj_1", Now).SessionId);
    }

    [Fact]
    public void Mints_a_fresh_id_per_null_silent_mint_never_supplied()
    {
        var a = HandleResolver.FromHookValue(null, "proj_1", Now);
        var b = HandleResolver.FromHookValue(null, "proj_1", Now);

        Assert.Equal(SessionSource.Minted, a.Source);
        Assert.Equal(SessionSource.Minted, b.Source);
        Assert.Matches(SessionShape, a.SessionId);
        Assert.NotEqual(a.SessionId, b.SessionId);
        Assert.Equal(Now, Ksuid.Parse(a.SessionId.Substring(4)).Date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n\u00A0\u2028")]
    public void Treats_a_whitespace_only_value_as_null(string value)
    {
        Assert.Equal(SessionSource.Minted, HandleResolver.FromHookValue(value, "proj_1", Now).Source);
    }

    [Fact]
    public void Derives_without_a_project_when_the_project_is_missing_or_empty()
    {
        Assert.Equal("ses_2cZY3tvyI25O2AmL2CGVo2B1IIj", HandleResolver.FromHookValue("customer-abc", null, Now).SessionId);
        Assert.Equal("ses_2cZY3tvyI25O2AmL2CGVo2B1IIj", HandleResolver.FromHookValue("customer-abc", string.Empty, Now).SessionId);
        Assert.Equal("ses_2cOHEO0LYGADMzRvWTXXVbbgxgm", HandleResolver.FromHookValue("customer-abc", "proj_1", Now).SessionId);
    }

    [Fact]
    public void Session_sources_have_their_TS_wire_names()
    {
        Assert.Equal("hook", SessionSource.Hook.ToWire());
        Assert.Equal("supplied", SessionSource.Supplied.ToWire());
        Assert.Equal("minted", SessionSource.Minted.ToWire());
        Assert.Equal("invalid", SessionSource.Invalid.ToWire());
        Assert.Equal("foreign", SessionSource.Foreign.ToWire());
    }
}
