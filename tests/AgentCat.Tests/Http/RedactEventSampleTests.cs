using System.Text.Json.Nodes;
using AgentCat.Internal.Delivery;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Http;

// The RedactEvent sample of the AgentCatOptions docs runs on every event, custom events included, whose parameters
// can have any shape. An exception in the hook drops the event, so the sample tests each level by type.
public class RedactEventSampleTests
{
    public static TheoryData<string> CustomParameters => new()
    {
        """[1,"two"]""",
        "\"text\"",
        """{"extra":"x"}""",
        """{"extra":{"requestInfo":["x"]}}""",
        """{"extra":{"requestInfo":{"headers":"x","url":5}}}""",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(CustomParameters))]
    public async Task The_sample_keeps_custom_events_of_any_shape(string parameters)
    {
        var (sent, log) = await RedactAsync("agentcat:custom", parameters);

        Assert.Equal(JsonNode.Parse(parameters)!.ToJsonString(), sent["parameters"]?.ToJsonString());
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("Failed to redact event", StringComparison.Ordinal));
    }

    // The query string can carry an API key or a configuration, so the sample removes it with the credentials.
    [Fact]
    public async Task The_sample_removes_the_credentials_and_the_query()
    {
        var (sent, _) = await RedactAsync(
            "mcp:tools/call",
            """{"extra":{"requestId":1,"requestInfo":{"headers":{"authorization":"Bearer t","cookie":"a=1","x-a":"1"},"url":"https://h.example/mcp?api_key=k"}}}""");

        Assert.Equal(
            """{"extra":{"requestId":1,"requestInfo":{"headers":{"x-a":"1"},"url":"https://h.example/mcp"}}}""",
            sent["parameters"]?.ToJsonString());
    }

    private static async Task<(JsonNode Sent, RecordingLog Log)> RedactAsync(string eventType, string parameters)
    {
        var log = new RecordingLog();
        var sender = new RecordingSender();
        var e = new AgentCatEvent
        {
            SessionId = "test-session",
            ProjectId = "test-project",
            EventType = eventType,
            Timestamp = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
            Parameters = JsonNode.Parse(parameters),
        };

        await new EventProcessor(sender, log, new ManualClock()).ProcessAsync(new QueuedEvent(e) { RedactEvent = RedactEventSample.Hook }, Ct);

        return (JsonNode.Parse(Assert.Single(sender.SentJson))!, log);
    }
}
