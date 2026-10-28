using AgentCat.Internal.Ids;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Ids;

public class SessionIdsTests
{
    private const string SessionShape = @"^ses_[0-9A-Za-z]{27}\z";
    private const string EventShape = @"^evt_[0-9A-Za-z]{27}\z";

    public static TheoryData<string, string?, string> DeriveVectors()
    {
        var data = new TheoryData<string, string?, string>();
        foreach (var row in Fixtures.Json("handles/derive-vectors.json").AsArray())
        {
            data.Add((string)row!["id"]!, (string?)row["projectId"], (string)row["sessionId"]!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DeriveVectors))]
    public void Derive_matches_golden_vectors(string id, string? projectId, string sessionId)
    {
        Assert.Equal(sessionId, SessionIds.Derive(id, projectId));
    }

    [Theory]
    [InlineData(" x ", "p", "ses_2c3yR5mYKQdLaXsJNgZH6erbfQK")]
    [InlineData("x", "p", "ses_2bw285VY9apdgUgTPXKFnT6P4G0")]
    [InlineData("", "", "ses_2cLO5ynrWej2LbO4pGhuDkShNRM")]
    [InlineData("x", null, "ses_2ajQcGwVDEwKX9Q5aud5zcaBhXV")]
    [InlineData("x", "", "ses_2ajQcGwVDEwKX9Q5aud5zcaBhXV")]
    [InlineData("\U0001F600", null, "ses_2cSHkaxkj6guDytT1IOcNky9EQs")]
    public void Derive_matches_TS_for_edge_inputs(string id, string? projectId, string sessionId)
    {
        Assert.Equal(sessionId, SessionIds.Derive(id, projectId));
    }

    [Fact]
    public void Derive_encodes_a_lone_surrogate_like_node()
    {
        Assert.Equal("ses_2c1ITKtRnDBMGhQcQua7L27W3tv", SessionIds.Derive("a\uD800b", "p"));
    }

    public static TheoryData<string, string?> Rejected() => new()
    {
        { "wrong prefix", "task_2xF9kQm3rTvB8nLpYw7ZcHd4Ke" },
        { "no prefix", "2xF9kQm3rTvB8nLpYw7ZcHd4Ke1" },
        { "too short", "ses_abc" },
        { "too long", "ses_" + new string('a', 28) },
        { "empty", "" },
        { "prefix only", "ses_" },
        { "customer value", "my-app-session-42" },
        { "non-base62 body", "ses_" + new string('-', 27) },
        { "inner whitespace", "ses_ " + new string('a', 26) },
        { "trailing newline", "ses_" + new string('a', 27) + "\n" },
        { "null", null },
        { "upper-case prefix", "SES_" + new string('a', 27) },
        { "non-ASCII letter", "ses_" + new string('a', 26) + "é" },
        { "full-width digit", "ses_" + new string('a', 26) + "０" },
        { "char before 0", "ses_" + new string('a', 26) + "/" },
        { "char after 9", "ses_" + new string('a', 26) + ":" },
        { "char before A", "ses_" + new string('a', 26) + "@" },
        { "char after Z", "ses_" + new string('a', 26) + "[" },
        { "char before a", "ses_" + new string('a', 26) + "`" },
        { "char after z", "ses_" + new string('a', 26) + "{" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void IsValid_rejects(string label, string? value)
    {
        Assert.False(SessionIds.IsValid(value), label);
    }

    [Theory]
    [InlineData("ses_parent000000000000000000000")]
    [InlineData("ses_09AZaz000000000000000000000")]
    [InlineData("ses_2cOHEO0LYGADMzRvWTXXVbbgxgm")]
    public void IsValid_accepts_well_shaped_values(string value)
    {
        Assert.True(SessionIds.IsValid(value));
    }

    [Fact]
    public void IsValid_accepts_ids_this_SDK_issues()
    {
        Assert.True(SessionIds.IsValid(SessionIds.NewSessionId(DateTimeOffset.UtcNow)));
        Assert.True(SessionIds.IsValid(SessionIds.Derive("anything", "proj_1")));
    }

    [Fact]
    public void New_ids_have_prefix_and_shape()
    {
        var now = DateTimeOffset.UtcNow;

        var sessionId = SessionIds.NewSessionId(now);
        var eventId = SessionIds.NewEventId(now);

        Assert.Matches(SessionShape, sessionId);
        Assert.Matches(EventShape, eventId);
        Assert.NotEqual(sessionId, SessionIds.NewSessionId(now));
        var floored = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds() / 1000 * 1000);
        Assert.Equal(floored, Ksuid.Parse(sessionId.Substring(4)).Date);
        Assert.Equal(floored, Ksuid.Parse(eventId.Substring(4)).Date);
    }

    [Fact]
    public void Should_create_prefixed_KSUIDs_with_randomSync()
    {
        var id = SessionIds.NewSessionId(DateTimeOffset.UtcNow);

        Assert.Matches(SessionShape, id);
    }

    [Fact]
    public void Should_create_prefixed_KSUIDs_with_random_async()
    {
        var id = SessionIds.NewEventId(DateTimeOffset.UtcNow);

        Assert.Matches(EventShape, id);
    }

    [Fact]
    public void Should_create_prefixed_KSUIDs_with_fromParts()
    {
        var id = SessionIds.Derive("customer-abc", "proj_1");

        Assert.Matches(SessionShape, id);
    }

    [Fact]
    public void Should_create_unique_prefixed_KSUIDs()
    {
        var ids = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add(SessionIds.NewEventId(DateTimeOffset.UtcNow));
        }

        Assert.Equal(ids.Count, new HashSet<string>(ids, StringComparer.Ordinal).Count);
        Assert.All(ids, id => Assert.Matches(EventShape, id));
    }
}
