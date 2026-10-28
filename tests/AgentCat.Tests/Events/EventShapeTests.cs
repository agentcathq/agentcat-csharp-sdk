using System.Globalization;
using System.Text.Json.Nodes;
using AgentCat.Internal.Events;

namespace AgentCat.Tests.Events;

public class EventShapeTests
{
    [Fact]
    public void ToTsJson_uses_TS_keys_in_TS_order_and_omits_nulls()
    {
        Assert.Equal(
            "{\"id\":\"evt_1\",\"sessionId\":\"ses_1\",\"projectId\":\"proj_1\",\"eventType\":\"mcp:tools/call\","
            + "\"timestamp\":\"2026-10-19T12:00:00.123Z\",\"duration\":42,\"inputTokens\":10,\"outputTokens\":20,"
            + "\"ipAddress\":\"10.0.0.1\",\"sdkLanguage\":\"csharp\",\"agentcatVersion\":\"0.1.0\","
            + "\"serverName\":\"srv\",\"serverVersion\":\"1.0\",\"clientName\":\"cli\",\"clientVersion\":\"2.0\","
            + "\"identifyActorGivenId\":\"u1\",\"identifyActorName\":\"User One\","
            + "\"identifyActorData\":{\"plan\":\"pro\",\"nested\":{\"a\":1}},\"resourceName\":\"search\","
            + "\"parameters\":{\"q\":\"x\",\"list\":[1,2]},\"response\":{\"ok\":true},\"userIntent\":\"find\","
            + "\"isError\":true,\"error\":{\"message\":\"boom\"},\"tags\":{\"k\":\"v\"},\"properties\":{\"p\":[\"a\"]}}",
            EventShape.ToTsJson(AgentCatEventTests.Full()).ToJsonString());
    }

    [Fact]
    public void ToTsJson_minimal_event_has_only_required_keys()
    {
        var e = new AgentCatEvent { Id = "a", SessionId = "b", EventType = "c", Timestamp = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        Assert.Equal(
            "{\"id\":\"a\",\"sessionId\":\"b\",\"eventType\":\"c\",\"timestamp\":\"1970-01-01T00:00:00.000Z\"}",
            EventShape.ToTsJson(e).ToJsonString());
    }

    [Fact]
    public void Timestamp_is_ISO_UTC_with_milliseconds()
    {
        var e = new AgentCatEvent { Timestamp = new DateTimeOffset(2026, 1, 2, 5, 4, 3, 123, TimeSpan.FromHours(2)) };
        Assert.Equal("2026-01-02T03:04:03.123Z", (string)EventShape.ToTsJson(e)["timestamp"]!);
    }

    [Fact]
    public void Timestamp_ignores_current_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var e = new AgentCatEvent { Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero) };
            Assert.Equal("2026-01-02T03:04:05.006Z", (string)EventShape.ToTsJson(e)["timestamp"]!);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void CopyInto_replaces_every_property()
    {
        var source = AgentCatEventTests.Full();
        var target = new AgentCatEvent
        {
            Id = "old", ProjectId = "old", Duration = 1, IsError = false,
            Parameters = new JsonObject(), Tags = new Dictionary<string, string> { ["z"] = "z" },
        };
        EventShape.CopyInto(source, target);
        Assert.Equal(EventShape.ToTsJson(source).ToJsonString(), EventShape.ToTsJson(target).ToJsonString());
        Assert.Same(source.Parameters, target.Parameters);

        var sparse = new AgentCatEvent { Id = "n" };
        EventShape.CopyInto(sparse, target);
        Assert.Null(target.ProjectId);
        Assert.Null(target.Duration);
        Assert.Null(target.Parameters);
        Assert.Null(target.Tags);
        Assert.Null(target.IsError);
        Assert.Equal("n", target.Id);
    }
}
