using System.Text.Json;
using AgentCat.Internal.Handles;

namespace AgentCat.Tests.Handles;

public class HandleTagsTests
{
    private const string T = "ses_2xF9kQm3rTvB8nLpYw7ZcHd4Ke1";
    private const string A = "opus-4.80-1m|claude-code|k3n9x";

    private static string[] Strings(params string[] values) => values;

    private static Dictionary<string, string> Expected(params string[] pairs)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            expected.Add(pairs[i], pairs[i + 1]);
        }

        return expected;
    }

    [Fact]
    public void Stamps_sources_agent_id_and_protocol_version()
    {
        var res = new HandleResolution(T, SessionSource.Supplied, false, A);

        Assert.Equal(
            Expected(
                "agentcat_session_id_source", "supplied",
                "agentcat_agent_id", A,
                "agentcat_agent_id_source", "supplied",
                "agentcat_protocol_version", "2026-07-28"),
            HandleTags.Build(res, "2026-07-28"));
    }

    [Fact]
    public void Omits_agent_and_protocol_keys_when_absent()
    {
        var res = new HandleResolution(T, SessionSource.Hook, true, null);

        Assert.Equal(Expected("agentcat_session_id_source", "hook"), HandleTags.Build(res, null));
    }

    [Fact]
    public void Clamps_a_longer_than_200_char_agent_id_to_exactly_200_chars_in_the_tag_only()
    {
        var longId = new string('a', 500);
        var res = new HandleResolution(T, SessionSource.Supplied, false, longId);

        var tags = HandleTags.Build(res, null);

        Assert.Equal(200, tags["agentcat_agent_id"].Length);
        Assert.Equal(new string('a', 200), tags["agentcat_agent_id"]);
        Assert.Equal(longId, res.AgentId);
    }

    [Fact]
    public void Strips_newlines_from_the_agent_id_tag()
    {
        var res = new HandleResolution(T, SessionSource.Supplied, false, "line1\nline2\r\nline3");

        var tags = HandleTags.Build(res, null);

        Assert.Equal("line1 line2  line3", tags["agentcat_agent_id"]);
        Assert.Equal("line1\nline2\r\nline3", res.AgentId);
    }

    [Fact]
    public void Passes_a_normal_agent_id_through_unchanged()
    {
        var res = new HandleResolution(T, SessionSource.Supplied, false, A);

        Assert.Equal(A, HandleTags.Build(res, null)["agentcat_agent_id"]);
    }

    [Fact]
    public void Keeps_exactly_200_UTF16_units()
    {
        var exact = new string('b', 200);

        Assert.Equal(exact, HandleTags.Build(new HandleResolution(T, SessionSource.Supplied, false, exact), null)["agentcat_agent_id"]);
    }

    [Fact]
    public void Cuts_at_200_UTF16_units_even_inside_a_surrogate_pair()
    {
        var res = new HandleResolution(T, SessionSource.Supplied, false, new string('a', 199) + "\U0001F600z");

        Assert.Equal(new string('a', 199) + "\uD83D", HandleTags.Build(res, null)["agentcat_agent_id"]);
    }

    [Fact]
    public void Replaces_newlines_before_cutting()
    {
        var res = new HandleResolution(T, SessionSource.Supplied, false, new string('\n', 150) + new string('\r', 100));

        Assert.Equal(new string(' ', 200), HandleTags.Build(res, null)["agentcat_agent_id"]);
    }

    [Fact]
    public void Keeps_other_line_breaks_and_tabs()
    {
        var res = new HandleResolution(T, SessionSource.Supplied, false, "a\tb\u2028c\u0085d");

        Assert.Equal("a\tb\u2028c\u0085d", HandleTags.Build(res, null)["agentcat_agent_id"]);
    }

    [Fact]
    public void Session_source_tag_names_every_source()
    {
        var sources = new[] { SessionSource.Hook, SessionSource.Supplied, SessionSource.Minted, SessionSource.Invalid, SessionSource.Foreign };
        var wires = new List<string>();
        foreach (var source in sources)
        {
            var tags = HandleTags.Build(new HandleResolution(string.Empty, source, source == SessionSource.Hook, null), null);
            Assert.Single(tags);
            wires.Add(tags["agentcat_session_id_source"]);
        }

        Assert.Equal(Strings("hook", "supplied", "minted", "invalid", "foreign"), wires.ToArray());
    }

    [Fact]
    public void An_empty_protocol_version_is_omitted()
    {
        var res = new HandleResolution(T, SessionSource.Minted, false, null);

        Assert.Equal(Expected("agentcat_session_id_source", "minted"), HandleTags.Build(res, string.Empty));
    }

    [Fact]
    public void An_empty_agent_id_is_not_tagged()
    {
        var res = new HandleResolution(T, SessionSource.Supplied, false, string.Empty);

        Assert.Equal(Expected("agentcat_session_id_source", "supplied"), HandleTags.Build(res, null));
    }

    [Fact]
    public void Tags_follow_the_TS_insertion_order()
    {
        var res = new HandleResolution(T, SessionSource.Supplied, false, A);

        Assert.Equal(
            Strings("agentcat_session_id_source", "agentcat_agent_id", "agentcat_agent_id_source", "agentcat_protocol_version"),
            HandleTags.Build(res, "2026-07-28").Keys.ToArray());
    }

    [Fact]
    public void Resolved_calls_tag_their_source()
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse("{\"session_id\":\"nope\",\"agent_id\":\"line\\nbreak\"}");
        foreach (var property in document.RootElement.EnumerateObject())
        {
            args[property.Name] = property.Value.Clone();
        }

        var res = HandleResolver.Resolve(args, false, true, true, DateTimeOffset.UtcNow);

        Assert.Equal(
            Expected(
                "agentcat_session_id_source", "invalid",
                "agentcat_agent_id", "line break",
                "agentcat_agent_id_source", "supplied"),
            HandleTags.Build(res, null));
    }
}
