using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgentCat.Internal.Delivery;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.PublicApi;

public class CustomEventTests
{
    private const string ProjectRequired = "projectId is required for publishCustomEvent";

    private const string FirstParameter = "First parameter must be either an MCP server object or a session ID string";

    private const string NoSession =
        "publishCustomEvent: no sessionId provided; event will be published without a session. Pass eventData.sessionId to attribute it to a task.";

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static Implementation Info(string json)
        => (Implementation)JsonSerializer.Deserialize(json, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Implementation)))!;

    private static string Wire(AgentCatEvent e) => Encoding.UTF8.GetString(WireBody.Build(e));

    private static async Task<EngineHarness> StartedAsync(
        string? projectId = "proj_tracked",
        AgentCatOptions? options = null,
        string? protocolVersion = null,
        PipelineSettings? pipeline = null)
    {
        var h = new EngineHarness(projectId, options, pipeline);
        h.AddTool((string msg) => "echo:" + msg, "echo");
        h.Configure(o => o.ServerInfo = Info("""{"name":"tracked-server","version":"2.4.6"}"""));
        await h.StartAsync(protocolVersion);
        return h;
    }

    private static async Task<AgentCatEvent> OneSentAsync(EngineHarness h)
    {
        await h.FlushAsync();
        return Assert.Single(h.Sender.Sent);
    }

    // Tags and properties on the server form.

    [Fact]
    public async Task Should_include_tags_and_properties_passed_directly()
    {
        await using var h = await StartedAsync();

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "test-project", new CustomEventData
        {
            ResourceName = "custom-action",
            Tags = new Dictionary<string, string> { ["env"] = "production", ["trace_id"] = "xyz" },
            Properties = Obj("""{"device":"mobile","feature_flags":["beta"]}"""),
        });

        var e = await OneSentAsync(h);
        Assert.Equal("custom-action", e.ResourceName);
        Assert.Equal(new Dictionary<string, string> { ["env"] = "production", ["trace_id"] = "xyz" }, e.Tags);
        Assert.Equal("""{"device":"mobile","feature_flags":["beta"]}""", e.Properties!.ToJsonString());
    }

    [Fact]
    public async Task Should_validate_tags_passed_directly()
    {
        await using var h = await StartedAsync();

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "test-project", new CustomEventData
        {
            ResourceName = "custom-action",
            Tags = new Dictionary<string, string> { ["valid"] = "value", ["bad!key"] = "value" },
        });

        var e = await OneSentAsync(h);
        Assert.Equal(new Dictionary<string, string> { ["valid"] = "value" }, e.Tags);
        Assert.Contains("Dropping invalid tag: \"bad!key\" — key contains invalid characters or is empty", h.Log.Lines);
    }

    [Fact]
    public async Task Should_normalize_empty_properties_to_omission()
    {
        await using var h = await StartedAsync();

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "test-project", new CustomEventData
        {
            ResourceName = "custom-action",
            Properties = new JsonObject(),
        });

        var e = await OneSentAsync(h);
        Assert.Null(e.Properties);
        Assert.DoesNotContain("\"properties\"", Wire(e), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validates_tags_and_normalizes_empty_properties_on_direct_custom_events()
    {
        await using var h = await StartedAsync("proj_test");

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "proj_test", new CustomEventData
        {
            SessionId = "ses_custom000000000000000000000",
            ResourceName = "custom-action",
            Tags = new Dictionary<string, string> { ["valid"] = "value", ["bad!key"] = "value" },
            Properties = new JsonObject(),
        });

        var e = await OneSentAsync(h);
        Assert.Equal(new Dictionary<string, string> { ["valid"] = "value" }, e.Tags);
        Assert.Null(e.Properties);
    }

    // Server form.

    [Fact]
    public async Task Should_publish_custom_event_with_tracked_server()
    {
        await using var h = await StartedAsync();

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "proj_test123", new CustomEventData
        {
            SessionId = "ses_srv000000000000000000000000",
            ResourceName = "custom-action",
            Parameters = Obj("""{"action":"test"}"""),
            Message = "Testing custom event",
        });

        var e = await OneSentAsync(h);
        Assert.Equal("ses_srv000000000000000000000000", e.SessionId);
        Assert.Equal("proj_tracked", e.ProjectId);
        Assert.Equal("agentcat:custom", e.EventType);
        Assert.Equal("custom-action", e.ResourceName);
        Assert.Equal("""{"action":"test"}""", e.Parameters!.ToJsonString());
        Assert.Equal("Testing custom event", e.UserIntent);
        Assert.Contains("Published custom event for session ses_srv000000000000000000000000 with type 'agentcat:custom'", h.Log.Lines);
    }

    [Fact]
    public async Task Should_use_eventData_sessionId_as_the_session_id_not_any_ambient_server_session()
    {
        await using var h = await StartedAsync();
        await h.CallToolAsync("echo", """{"msg":"hi"}""");

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "proj_test123", new CustomEventData { SessionId = "ses_srv000000000000000000000000" });
        await h.FlushAsync();

        var custom = Assert.Single(h.Sender.Sent, s => s.EventType == "agentcat:custom");
        Assert.Equal("ses_srv000000000000000000000000", custom.SessionId);
    }

    [Fact]
    public async Task Should_publish_with_an_empty_session_id_and_warn_when_no_sessionId_is_provided()
    {
        await using var h = await StartedAsync();

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "proj_test123", new CustomEventData { ResourceName = "custom-action" });

        var e = await OneSentAsync(h);
        Assert.Equal("", e.SessionId);
        Assert.Equal("proj_tracked", e.ProjectId);
        Assert.Equal("custom-action", e.ResourceName);
        Assert.Contains("\"session_id\":null", Wire(e), StringComparison.Ordinal);
        Assert.Contains(NoSession, h.Log.Lines);
        Assert.Contains(h.Log.Written, w => w.Line == NoSession && w.Severity == AgentCat.Internal.Logging.LogSeverity.Warning);
        Assert.Contains("Published custom event without a session with type 'agentcat:custom'", h.Log.Lines);
    }

    [Fact]
    public async Task Should_handle_error_data_correctly()
    {
        await using var h = await StartedAsync();

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "proj_test123", new CustomEventData
        {
            IsError = true,
            Error = Obj("""{"message":"Test error","code":"ERR_001"}"""),
        });

        var e = await OneSentAsync(h);
        Assert.True(e.IsError);
        Assert.Equal("""{"message":"Test error","code":"ERR_001"}""", e.Error!.ToJsonString());
    }

    [Fact]
    public async Task Queues_a_custom_event_on_a_v2_tracked_server_with_sessionId_attribution()
    {
        await using var h = await StartedAsync("proj_test");

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "proj_test", new CustomEventData
        {
            SessionId = "ses_custom000000000000000000000",
            ResourceName = "custom-action",
            Parameters = Obj("""{"action":"test"}"""),
            Message = "Testing custom event",
        });

        var e = await OneSentAsync(h);
        Assert.Equal("agentcat:custom", e.EventType);
        Assert.Equal("ses_custom000000000000000000000", e.SessionId);
        Assert.Equal("proj_test", e.ProjectId);
        Assert.Equal("custom-action", e.ResourceName);
        Assert.Equal("""{"action":"test"}""", e.Parameters!.ToJsonString());
        Assert.Equal("Testing custom event", e.UserIntent);
    }

    [Fact]
    public async Task Publishes_without_a_session_when_a_tracked_server_gets_no_sessionId()
    {
        await using var h = await StartedAsync("proj_test");

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "proj_test", new CustomEventData { ResourceName = "sessionless-action" });

        var e = await OneSentAsync(h);
        Assert.Equal("sessionless-action", e.ResourceName);
        Assert.Equal("", e.SessionId);
    }

    [Fact]
    public async Task Server_form_carries_server_and_client_identity()
    {
        await using var h = await StartedAsync(protocolVersion: McpHarness.LegacyProtocol);

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "proj_x", new CustomEventData { SessionId = "s1" });

        var e = await OneSentAsync(h);
        Assert.Equal("tracked-server", e.ServerName);
        Assert.Equal("2.4.6", e.ServerVersion);
        Assert.Equal("harness-client", e.ClientName);
        Assert.Equal("3.1.4", e.ClientVersion);
        Assert.Equal("proj_tracked", e.ProjectId);
        Assert.Equal("C#", e.SdkLanguage);
    }

    [Fact]
    public async Task Server_form_runs_RedactEvent_but_not_the_string_redactor()
    {
        var stringRedactorCalls = 0;
        await using var h = await StartedAsync(options: new AgentCatOptions
        {
            RedactSensitiveInformation = (s, _) =>
            {
                Interlocked.Increment(ref stringRedactorCalls);
                return new ValueTask<string>("[x]");
            },
            RedactEvent = (e, _) =>
            {
                e.UserIntent = "redacted-by-event-hook";
                return new ValueTask<AgentCatEvent?>(e);
            },
        });

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "proj_x", new CustomEventData
        {
            SessionId = "s1",
            ResourceName = "secret-resource",
            Message = "secret",
        });

        var e = await OneSentAsync(h);
        Assert.Equal("redacted-by-event-hook", e.UserIntent);
        Assert.Equal("secret-resource", e.ResourceName);
        Assert.Equal(0, stringRedactorCalls);
    }

    [Fact]
    public async Task Server_form_with_tracing_off_publishes_nothing_but_logs_published()
    {
        await using var h = await StartedAsync(options: new AgentCatOptions { EnableTracing = false });

        await new AgentCatClient(h.Runtime).PublishCustomEventAsync(h.Server, "proj_x", new CustomEventData { SessionId = "s1" });

        Assert.True(await h.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Empty(h.Sender.Sent);
        Assert.Contains("Published custom event for session s1 with type 'agentcat:custom'", h.Log.Lines);
    }

    // The event carries the tracked project ID, which is empty on a telemetry-only server, so it is never sent. The
    // string-form event at the end proves that the runtime would reach the fake API.
    [Fact]
    public async Task Server_form_on_a_telemetry_only_server_never_sends_the_event()
    {
        using var api = new FakeAgentCatApi();
        using var sender = new HttpEventSender();
        var log = new RecordingLog();
        var runtime = new AgentCatRuntime(log, sender);
        try
        {
            await using var h = new McpHarness();
            h.AddTool((string msg) => "echo:" + msg, "echo");
            h.Configure(o => AgentCatSdk.Track(o, "", new AgentCatOptions { ApiBaseUrl = api.BaseUrl }, () => runtime));
            await h.StartAsync();
            var client = new AgentCatClient(runtime);

            await client.PublishCustomEventAsync(h.Server, "proj_x", new CustomEventData { SessionId = "ses_telemetry", ResourceName = "telemetry-only" });
            await client.PublishCustomEventAsync(h.Server, "proj_x", new CustomEventData { ResourceName = "telemetry-only" });

            Assert.True(await runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
            Assert.Empty(api.Requests);
            Assert.Equal(
                new[]
                {
                    "AgentCat setup started | project (telemetry-only) | sdk v" + McpHarness.McpMajor + "/options | signals filters",
                    "Warning: No projectId provided and no exporters configured. Events will not be sent anywhere.",
                    "AgentCat setup complete | project (telemetry-only) | tracing=true context=true reportMissing=true exporters=0",
                    "Published custom event for session ses_telemetry with type 'agentcat:custom'",
                    NoSession,
                    "Published custom event without a session with type 'agentcat:custom'",
                },
                log.Lines);

            await client.PublishCustomEventAsync("ses_control", "proj_control");
            var request = Assert.Single(await api.WaitForRequestsAsync(1));
            Assert.Equal("proj_control", (string?)JsonNode.Parse(Encoding.UTF8.GetString(request.Body))!["project_id"]);
        }
        finally
        {
            runtime.Pipeline.Dispose();
        }
    }

    [Fact]
    public async Task Server_form_failure_after_validation_logs_to_the_tracker_runtime_and_does_not_throw()
    {
        await using var h = await StartedAsync();
        var other = new RecordingLog();

        var task = new AgentCatClient(new AgentCatRuntime(other, new RecordingSender()))
            .PublishCustomEventAsync(h.Server, "proj_x", new CustomEventData { Tags = new ThrowingTags() });
        await task;

        Assert.Contains("Warning: Failed to publish custom event - InvalidOperationException: tags boom", h.Log.Lines);
        Assert.Empty(other.Lines);
        Assert.DoesNotContain(h.Log.Lines, l => l.StartsWith("Published custom event", StringComparison.Ordinal));
    }

    // String form.

    [Fact]
    public async Task Should_use_the_session_ID_string_verbatim_as_the_session_ID()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("user-session-12345", "proj_test123", new CustomEventData
        {
            ResourceName = "custom-action",
            Parameters = Obj("""{"action":"test"}"""),
        });

        var e = await d.OneSentAsync();
        Assert.Equal("user-session-12345", e.SessionId);
        Assert.Equal("proj_test123", e.ProjectId);
        Assert.Equal("agentcat:custom", e.EventType);
        Assert.Equal("custom-action", e.ResourceName);
        Assert.Equal("""{"action":"test"}""", e.Parameters!.ToJsonString());
        Assert.Contains("Published custom event for session user-session-12345 with type 'agentcat:custom'", d.Log.Lines);
    }

    [Fact]
    public async Task Should_let_eventData_sessionId_take_precedence_over_the_string()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("ignored-string", "proj", new CustomEventData { SessionId = "ses_wins00000000000000000000000" });

        var e = await d.OneSentAsync();
        Assert.Equal("ses_wins00000000000000000000000", e.SessionId);
        Assert.Equal("proj", e.ProjectId);
    }

    [Fact]
    public async Task Should_handle_all_event_data_fields()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("user-session-12345", "proj_test123", new CustomEventData
        {
            ResourceName = "full-test",
            Parameters = Obj("""{"key":"value"}"""),
            Response = Obj("""{"result":"success"}"""),
            Message = "Complete test",
            Duration = 1500,
            IsError = false,
            Error = null,
        });

        var e = await d.OneSentAsync();
        Assert.Equal("full-test", e.ResourceName);
        Assert.Equal("""{"key":"value"}""", e.Parameters!.ToJsonString());
        Assert.Equal("""{"result":"success"}""", e.Response!.ToJsonString());
        Assert.Equal("Complete test", e.UserIntent);
        Assert.Equal(1500, e.Duration);
        Assert.False(e.IsError);
        Assert.Null(e.Error);
        Assert.Contains("\"is_error\":false", Wire(e), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_throw_error_if_projectId_is_not_provided()
    {
        using var d = new Direct();

        foreach (var projectId in new string?[] { "", null })
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => d.Client.PublishCustomEventAsync("session-id", projectId!));
            Assert.Equal(ProjectRequired, ex.Message);
        }

        Assert.True(await d.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Empty(d.Sender.Sent);
    }

    [Fact]
    public async Task Should_throw_error_if_first_parameter_is_invalid()
    {
        using var d = new Direct();
        await using var h = await StartedAsync();

        var fromString = await Assert.ThrowsAsync<ArgumentException>(() => d.Client.PublishCustomEventAsync((string)null!, "proj_123"));
        var fromServer = await Assert.ThrowsAsync<ArgumentException>(() => d.Client.PublishCustomEventAsync((McpServer)null!, "proj_123"));

        Assert.Equal(FirstParameter, fromString.Message);
        Assert.Equal(FirstParameter, fromServer.Message);
    }

    [Fact]
    public async Task Should_always_use_agentcat_custom_as_event_type()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("test-session", "proj_test");

        Assert.Equal("agentcat:custom", (await d.OneSentAsync()).EventType);
    }

    [Fact]
    public async Task Should_include_timestamp()
    {
        using var d = new Direct();
        var before = DateTimeOffset.UtcNow;

        await d.Client.PublishCustomEventAsync("test-session", "proj_test");

        var after = DateTimeOffset.UtcNow;
        var e = await d.OneSentAsync();
        Assert.InRange(e.Timestamp, before, after);
    }

    [Fact]
    public async Task Should_handle_undefined_event_data_gracefully()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("test-session", "proj_test", null);

        AssertEmptyData(await d.OneSentAsync());
    }

    [Fact]
    public async Task Should_handle_empty_event_data_object()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("test-session", "proj_test", new CustomEventData());

        AssertEmptyData(await d.OneSentAsync());
    }

    [Fact]
    public async Task Uses_a_session_ID_string_verbatim_as_the_session_carrying_full_event_data()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("user-session-12345", "proj_test", new CustomEventData
        {
            ResourceName = "full-test",
            Parameters = Obj("""{"key":"value"}"""),
            Response = Obj("""{"result":"success"}"""),
            Message = "Complete test",
            Duration = 1500,
            Properties = Obj("""{"device":"mobile","feature_flags":["beta"]}"""),
            Tags = new Dictionary<string, string> { ["env"] = "production" },
        });

        var e = await d.OneSentAsync();
        Assert.Equal("agentcat:custom", e.EventType);
        Assert.Equal("user-session-12345", e.SessionId);
        Assert.Equal("full-test", e.ResourceName);
        Assert.Equal("""{"key":"value"}""", e.Parameters!.ToJsonString());
        Assert.Equal("""{"result":"success"}""", e.Response!.ToJsonString());
        Assert.Equal("Complete test", e.UserIntent);
        Assert.Equal(1500, e.Duration);
        Assert.Equal(new Dictionary<string, string> { ["env"] = "production" }, e.Tags);
        Assert.Equal("""{"device":"mobile","feature_flags":["beta"]}""", e.Properties!.ToJsonString());
    }

    [Fact]
    public async Task Lets_eventData_sessionId_take_precedence_over_the_session_ID_string()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("ignored-string", "proj_test", new CustomEventData { SessionId = "ses_wins00000000000000000000000" });

        Assert.Equal("ses_wins00000000000000000000000", (await d.OneSentAsync()).SessionId);
    }

    [Fact]
    public async Task Validates_its_required_parameters()
    {
        using var d = new Direct();

        var project = await Assert.ThrowsAsync<ArgumentException>(() => d.Client.PublishCustomEventAsync("session-id", ""));
        var first = await Assert.ThrowsAsync<ArgumentException>(() => d.Client.PublishCustomEventAsync((string)null!, "proj_test"));

        Assert.Equal(ProjectRequired, project.Message);
        Assert.Equal(FirstParameter, first.Message);
        Assert.True(await d.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Empty(d.Sender.Sent);
    }

    [Fact]
    public async Task Empty_string_session_is_accepted_without_a_session()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("", "proj_test", new CustomEventData { ResourceName = "r" });

        var e = await d.OneSentAsync();
        Assert.Equal("", e.SessionId);
        Assert.Contains("\"session_id\":null", Wire(e), StringComparison.Ordinal);
        Assert.Contains("Published custom event without a session with type 'agentcat:custom'", d.Log.Lines);
        Assert.DoesNotContain(NoSession, d.Log.Lines);
    }

    [Fact]
    public async Task Null_server_and_null_session_throw_the_first_parameter_message()
    {
        using var d = new Direct();

        var server = d.Client.PublishCustomEventAsync((McpServer)null!, "proj_test");
        var session = d.Client.PublishCustomEventAsync((string)null!, "proj_test");

        Assert.Equal(FirstParameter, (await Assert.ThrowsAsync<ArgumentException>(() => server)).Message);
        Assert.Equal(FirstParameter, (await Assert.ThrowsAsync<ArgumentException>(() => session)).Message);
    }

    [Fact]
    public async Task Project_id_is_checked_before_the_first_argument()
    {
        using var d = new Direct();

        var session = await Assert.ThrowsAsync<ArgumentException>(() => d.Client.PublishCustomEventAsync((string)null!, ""));
        var server = await Assert.ThrowsAsync<ArgumentException>(() => d.Client.PublishCustomEventAsync((McpServer)null!, null!));

        Assert.Equal(ProjectRequired, session.Message);
        Assert.Equal(ProjectRequired, server.Message);
    }

    [Fact]
    public async Task String_form_has_no_sdk_identity_and_no_identify_data()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("s1", "proj_test", new CustomEventData { ResourceName = "r" });

        var body = Wire(await d.OneSentAsync());
        Assert.Contains("\"event_type\":\"agentcat:custom\"", body, StringComparison.Ordinal);
        foreach (var key in new[] { "sdk_language", "agentcat_version", "server_name", "identify_data" })
        {
            Assert.DoesNotContain("\"" + key + "\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task String_form_runs_neither_redaction_hook()
    {
        using var d = new Direct();

        await d.Client.PublishCustomEventAsync("s1", "proj_test", new CustomEventData { ResourceName = "secret", Message = "secret" });

        var e = await d.OneSentAsync();
        Assert.Equal("secret", e.ResourceName);
        Assert.Equal("secret", e.UserIntent);
    }

    [Fact]
    public async Task Failure_after_validation_logs_and_does_not_throw()
    {
        using var d = new Direct();

        var thrown = await Record.ExceptionAsync(() => d.Client.PublishCustomEventAsync("s1", "proj_test", new CustomEventData { Tags = new ThrowingTags() }));

        Assert.Null(thrown);
        Assert.Contains("Warning: Failed to publish custom event - InvalidOperationException: tags boom", d.Log.Lines);
        Assert.True(await d.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Empty(d.Sender.Sent);
    }

    [Fact]
    public async Task A_runtime_that_cannot_be_resolved_does_not_throw()
    {
        var client = new AgentCatClient((Func<AgentCatRuntime>)(() => throw new InvalidOperationException("no runtime")));

        Assert.Null(await Record.ExceptionAsync(() => client.PublishCustomEventAsync("s1", "proj_test")));
        Assert.False(await client.FlushAsync(cancellationToken: TestContext.Current.CancellationToken));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => client.PublishCustomEventAsync("s1", ""));
        Assert.Equal(ProjectRequired, ex.Message);
    }

    // The pipeline reads the event data later, on its own thread, so a caller may reuse or change its objects as soon
    // as the call returns. One pipeline reader, held by a hanging send, keeps the event queued while the caller writes.
    [Theory]
    [InlineData("string")]
    [InlineData("server")]
    public async Task Event_data_is_copied_when_the_event_is_published(string form)
    {
        await using var h = await StartedAsync(pipeline: new PipelineSettings { Concurrency = 1 });
        var client = new AgentCatClient(h.Runtime);
        h.Sender.Hang();
        await client.PublishCustomEventAsync("ses_hold", "proj_hold");
        await WaitUntilAsync(() => h.Sender.HangingCount == 1);
        var parameters = Obj("""{"a":1,"nested":{"b":"original"},"list":["x"]}""");
        var response = Obj("""{"content":[{"type":"text","text":"original"}]}""");
        var error = Obj("""{"message":"original"}""");
        var properties = Obj("""{"p":"original"}""");
        var data = new CustomEventData { SessionId = "ses_copy", Parameters = parameters, Response = response, Error = error, Properties = properties };

        await (form == "server" ? client.PublishCustomEventAsync(h.Server, "proj_x", data) : client.PublishCustomEventAsync("ses_copy", "proj_x", data));
        Assert.Equal(1, h.Runtime.Pipeline.QueueLength);
        parameters["a"] = 2;
        parameters["nested"]!["b"] = "changed";
        parameters["list"]!.AsArray().Add("y");
        response["content"]![0]!["text"] = "changed";
        error["message"] = "changed";
        properties["p"] = "changed";
        properties["added"] = true;
        h.Sender.Release();

        await h.FlushAsync();
        var body = JsonNode.Parse(Wire(Assert.Single(h.Sender.Sent, e => e.SessionId == "ses_copy")))!;
        Assert.Equal("""{"a":1,"nested":{"b":"original"},"list":["x"]}""", body["parameters"]!.ToJsonString());
        Assert.Equal("""{"content":[{"type":"text","text":"original"}]}""", body["response"]!.ToJsonString());
        Assert.Equal("original", (string?)body["error"]!["message"]);
        Assert.Equal("""{"p":"original"}""", body["properties"]!.ToJsonString());
        Assert.Contains(CustomEvent.Published("ses_copy"), h.Log.Lines);
    }

    [Fact]
    public async Task Event_data_that_cannot_be_copied_is_a_failure_after_validation()
    {
        using var d = new Direct();
        var info = JsonMetadataServices.CreateValueInfo<Unwritable>(
            new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() },
            new UnwritableConverter());
        var parameters = new JsonObject { ["value"] = JsonValue.Create(new Unwritable(), info) };

        var thrown = await Record.ExceptionAsync(() => d.Client.PublishCustomEventAsync("s1", "proj_test", new CustomEventData { Parameters = parameters }));

        Assert.Null(thrown);
        Assert.Contains("Warning: Failed to publish custom event - InvalidOperationException: unwritable", d.Log.Lines);
        Assert.DoesNotContain(d.Log.Lines, l => l.StartsWith("Published custom event", StringComparison.Ordinal));
        Assert.True(await d.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Empty(d.Sender.Sent);
    }

    [Fact]
    public void AgentCatSdk_PublishCustomEventAsync_is_public_static()
    {
        var overloads = typeof(AgentCatSdk).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "PublishCustomEventAsync").ToArray();

        Assert.Equal(2, overloads.Length);
        Assert.Contains(overloads, m => m.GetParameters()[0].ParameterType == typeof(string));
        Assert.Contains(overloads, m => m.GetParameters()[0].ParameterType == typeof(McpServer));
        Assert.All(overloads, m =>
        {
            Assert.Equal(typeof(Task), m.ReturnType);
            Assert.Equal(typeof(string), m.GetParameters()[1].ParameterType);
            Assert.Equal(typeof(CustomEventData), m.GetParameters()[2].ParameterType);
            Assert.True(m.GetParameters()[2].IsOptional);
        });
        var flush = typeof(AgentCatSdk).GetMethod("FlushAsync", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(TimeSpan?), typeof(CancellationToken) }, null);
        Assert.NotNull(flush);
        Assert.Equal(typeof(Task<bool>), flush.ReturnType);
        Assert.All(flush.GetParameters(), p => Assert.True(p.IsOptional));
        Assert.Single(typeof(AgentCatSdk).GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name == "FlushAsync");
        Assert.NotNull(typeof(IAgentCat).GetMethod("FlushAsync", new[] { typeof(TimeSpan?), typeof(CancellationToken) }));
        var shutdown = typeof(AgentCatSdk).GetMethod("ShutdownAsync", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(CancellationToken) }, null);
        Assert.NotNull(shutdown);
        Assert.Equal(typeof(Task<bool>), shutdown.ReturnType);
        Assert.True(shutdown.GetParameters()[0].IsOptional);
        Assert.NotNull(typeof(IAgentCat).GetMethod("ShutdownAsync", new[] { typeof(CancellationToken) }));
        Assert.Equal(4, typeof(IAgentCat).GetMethods().Length);
        Assert.True(typeof(IAgentCat).IsAssignableFrom(typeof(AgentCatClient)));
    }

    private static void AssertEmptyData(AgentCatEvent e)
    {
        Assert.Null(e.ResourceName);
        Assert.Null(e.Parameters);
        Assert.Null(e.Response);
        Assert.Null(e.UserIntent);
        Assert.Null(e.Duration);
        Assert.Null(e.IsError);
        Assert.Null(e.Error);
        Assert.Null(e.Tags);
        Assert.Null(e.Properties);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class Unwritable
    {
    }

    private sealed class UnwritableConverter : JsonConverter<Unwritable>
    {
        public override Unwritable Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Unwritable value, JsonSerializerOptions options) => throw new InvalidOperationException("unwritable");
    }

    private sealed class ThrowingTags : IReadOnlyDictionary<string, string>
    {
        public IEnumerable<string> Keys => throw new InvalidOperationException("tags boom");

        public IEnumerable<string> Values => throw new InvalidOperationException("tags boom");

        public int Count => 1;

        public string this[string key] => throw new InvalidOperationException("tags boom");

        public bool ContainsKey(string key) => throw new InvalidOperationException("tags boom");

        public bool TryGetValue(string key, out string value) => throw new InvalidOperationException("tags boom");

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => throw new InvalidOperationException("tags boom");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class Direct : IDisposable
    {
        internal Direct()
        {
            Runtime = new AgentCatRuntime(Log, Sender);
            Client = new AgentCatClient(Runtime);
        }

        internal RecordingLog Log { get; } = new();

        internal RecordingSender Sender { get; } = new();

        internal AgentCatRuntime Runtime { get; }

        internal AgentCatClient Client { get; }

        internal async Task<AgentCatEvent> OneSentAsync()
        {
            Assert.True(await Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound));
            return Assert.Single(Sender.Sent);
        }

        public void Dispose() => Runtime.Pipeline.Dispose();
    }
}

// An install that the gate stopped turns "not tracked" into a warning for the whole process
// (EnabledGate.SkippedAnInstall), so the tests that expect the error run alone.
[Collection(SharedRuntime.Name)]
public sealed class CustomEventUntrackedServerTests : IDisposable
{
    private const string ProjectRequired = "projectId is required for publishCustomEvent";

    private const string FirstParameter = "First parameter must be either an MCP server object or a session ID string";

    private const string NotTracked = "Server is not tracked. Please call agentcat.track() first or provide a session ID string.";

    public CustomEventUntrackedServerTests() => EnabledGate.ResetSkippedAnInstall();

    public void Dispose() => EnabledGate.ResetSkippedAnInstall();

    [Fact]
    public async Task Should_throw_error_if_server_is_not_tracked()
    {
        var sender = new RecordingSender();
        var runtime = new AgentCatRuntime(new RecordingLog(), sender);
        await using var h = new McpHarness();
        h.AddTool((string msg) => "echo:" + msg, "echo");
        await h.StartAsync();

        var task = new AgentCatClient(runtime).PublishCustomEventAsync(h.Server, "proj_test123");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => task);
        Assert.Equal(NotTracked, ex.Message);
        Assert.True(await runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Empty(sender.Sent);
        runtime.Pipeline.Dispose();
    }

    [Fact]
    public async Task Throws_for_an_untracked_v2_server()
    {
        var sender = new RecordingSender();
        var runtime = new AgentCatRuntime(new RecordingLog(), sender);
        await using var h = new McpHarness();
        await h.StartAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => new AgentCatClient(runtime).PublishCustomEventAsync(h.Server, "proj_test", new CustomEventData { ResourceName = "custom-action" }));

        Assert.Equal(NotTracked, ex.Message);
        Assert.True(await runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        Assert.Empty(sender.Sent);
        runtime.Pipeline.Dispose();
    }

    [Fact]
    public async Task Validation_failures_are_faulted_tasks()
    {
        using var d = new CustomEventTests.Direct();
        await using var h = new McpHarness();
        await h.StartAsync();
        var tasks = new List<Task>();

        var thrown = Record.Exception(() =>
        {
            tasks.Add(d.Client.PublishCustomEventAsync("s", ""));
            tasks.Add(d.Client.PublishCustomEventAsync((string)null!, "p"));
            tasks.Add(d.Client.PublishCustomEventAsync((McpServer)null!, "p"));
            tasks.Add(d.Client.PublishCustomEventAsync(h.Server, "p"));
        });

        Assert.Null(thrown);
        Assert.All(tasks, t => Assert.True(t.IsFaulted));
        Assert.Equal(
            new[] { ProjectRequired, FirstParameter, FirstParameter, NotTracked },
            tasks.Select(t => Assert.IsType<ArgumentException>(t.Exception!.InnerException).Message));
    }
}
