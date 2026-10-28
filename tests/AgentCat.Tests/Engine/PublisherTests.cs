using System.Text.Json.Nodes;
using AgentCat.Internal;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;

namespace AgentCat.Tests.Engine;

public class PublisherTests
{
    private readonly RecordingSender _sender = new();
    private readonly ManualClock _clock = new();
    private readonly RecordingLog _log = new();

    public PublisherTests() => _clock.HoldDelays();

    private AgentCatTracker NewTracker(AgentCatOptions? options = null, string projectId = "proj_a")
    {
        var runtime = new AgentCatRuntime(_log, _sender, clock: _clock);
        return AgentCatTracker.Create(projectId, options, runtime, "options", _ => null);
    }

    private async Task<AgentCatEvent> PublishOne(AgentCatTracker tracker, AgentCatEvent e, bool redactStrings = true)
    {
        EventPublisher.Publish(tracker, e, null, redactStrings);
        Assert.True(await tracker.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound));
        return Assert.Single(_sender.Sent);
    }

    private AgentCatEvent NewEvent() => new()
    {
        SessionId = "test-session",
        EventType = "mcp:tools/call",
        ResourceName = "test-tool",
        Timestamp = _clock.UtcNow,
        Parameters = new JsonObject { ["test"] = "value" },
    };

    [Fact]
    public async Task Should_publish_event_with_server_tracking_data_and_session_info()
    {
        var sent = await PublishOne(NewTracker(), NewEvent());

        Assert.Equal("proj_a", sent.ProjectId);
        Assert.Equal("test-session", sent.SessionId);
        Assert.Equal("test-tool", sent.ResourceName);
        Assert.Equal("value", (string?)sent.Parameters!["test"]);
        Assert.StartsWith("evt_", sent.Id);
    }

    [Fact]
    public async Task Should_not_publish_event_when_enableTracing_is_false()
    {
        var tracker = NewTracker(new AgentCatOptions { EnableTracing = false });

        EventPublisher.Publish(tracker, NewEvent(), null, true);

        Assert.True(await tracker.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Equal(0, _sender.Calls);
        Assert.Equal(0, tracker.Runtime.Pipeline.QueueLength);
    }

    [Fact]
    public async Task Should_calculate_duration_when_not_provided()
    {
        var e = NewEvent();
        e.Timestamp = _clock.UtcNow - TimeSpan.FromSeconds(1);

        var sent = await PublishOne(NewTracker(), e);

        Assert.Equal(1000, sent.Duration);
    }

    [Fact]
    public async Task Should_preserve_existing_duration()
    {
        var e = NewEvent();
        e.Timestamp = _clock.UtcNow - TimeSpan.FromSeconds(1);
        e.Duration = 500;

        var sent = await PublishOne(NewTracker(), e);

        Assert.Equal(500, sent.Duration);
    }

    [Fact]
    public async Task A_given_zero_duration_is_kept()
    {
        var e = NewEvent();
        e.Timestamp = _clock.UtcNow - TimeSpan.FromSeconds(1);
        e.Duration = 0;

        var sent = await PublishOne(NewTracker(), e);

        Assert.Equal(0, sent.Duration);
    }

    [Fact]
    public async Task A_default_timestamp_is_set_and_leaves_the_duration_empty()
    {
        var e = NewEvent();
        e.Timestamp = default;

        var sent = await PublishOne(NewTracker(), e);

        Assert.Equal(_clock.UtcNow, sent.Timestamp);
        Assert.Null(sent.Duration);
    }

    [Fact]
    public async Task Should_publish_an_absent_sessionId_as_empty_string_and_send_it_as_null()
    {
        var e = NewEvent();
        e.SessionId = "";

        var sent = await PublishOne(NewTracker(), e);

        Assert.Equal("", sent.SessionId);
        Assert.Contains("\"session_id\":null", System.Text.Encoding.UTF8.GetString(AgentCat.Internal.Delivery.WireBody.Build(sent)));
    }

    [Fact]
    public async Task Should_send_inputTokens_and_outputTokens_on_the_wire_request()
    {
        var e = NewEvent();
        e.InputTokens = 6;
        e.OutputTokens = 4;

        var sent = await PublishOne(NewTracker(), e);

        Assert.Equal(6, sent.InputTokens);
        Assert.Equal(4, sent.OutputTokens);
    }

    [Fact]
    public async Task Should_leave_the_token_fields_undefined_when_the_event_has_none()
    {
        var sent = await PublishOne(NewTracker(), NewEvent());

        Assert.Null(sent.InputTokens);
        Assert.Null(sent.OutputTokens);
    }

    [Fact]
    public async Task Publisher_fills_sdk_identity_and_empty_identify_data()
    {
        var e = NewEvent();
        e.ProjectId = "someone_else";
        e.SdkLanguage = "other";
        e.AgentCatVersion = "0";

        var sent = await PublishOne(NewTracker(), e);

        Assert.Equal("proj_a", sent.ProjectId);
        Assert.Equal(SdkInfo.Language, sent.SdkLanguage);
        Assert.Equal(SdkInfo.Version, sent.AgentCatVersion);
        Assert.NotNull(sent.IdentifyActorData);
        Assert.Empty(sent.IdentifyActorData!);
    }

    [Fact]
    public async Task Existing_identify_data_is_kept()
    {
        var e = NewEvent();
        e.IdentifyActorData = new JsonObject { ["plan"] = "pro" };

        var sent = await PublishOne(NewTracker(), e);

        Assert.Equal("pro", (string?)sent.IdentifyActorData!["plan"]);
    }

    [Fact]
    public async Task Redaction_hooks_come_from_the_settings_and_strings_only_when_asked()
    {
        var eventHookCalls = 0;
        var stringHookCalls = 0;
        var options = new AgentCatOptions
        {
            RedactEvent = (e, _) =>
            {
                Interlocked.Increment(ref eventHookCalls);
                return new ValueTask<AgentCatEvent?>(e);
            },
            RedactSensitiveInformation = (text, _) =>
            {
                Interlocked.Increment(ref stringHookCalls);
                return new ValueTask<string>(text);
            },
        };
        var tracker = NewTracker(options);

        EventPublisher.Publish(tracker, NewEvent(), null, redactStrings: false);
        Assert.True(await tracker.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Equal(1, eventHookCalls);
        Assert.Equal(0, stringHookCalls);

        EventPublisher.Publish(tracker, NewEvent(), null, redactStrings: true);
        Assert.True(await tracker.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Equal(2, eventHookCalls);
        Assert.True(stringHookCalls > 0);
    }

    [Fact]
    public async Task Prepare_runs_on_the_pipeline_before_sending()
    {
        var tracker = NewTracker();
        var e = NewEvent();

        EventPublisher.Publish(tracker, e, (ev, _) =>
        {
            ev.UserIntent = "prepared";
            return default;
        }, true);
        Assert.True(await tracker.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));

        Assert.Equal("prepared", Assert.Single(_sender.Sent).UserIntent);
    }
}
