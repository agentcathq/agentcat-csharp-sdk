using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AgentCat.Internal.Engine;
using AgentCat.Internal.Text;
using AgentCat.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.E2E;

// The TS e2e-http matrix over the C# lanes: each scenario runs on every lane of the loaded MCP version that it applies
// to.
public class ScenarioMatrixTests
{
    private static readonly string[] LooseArguments = { "msg", "customer_extra", "context", "session_id" };

    // The TS v1 and v2 baseline assertions together. The strip proof goes through loose and strict, because a C# echo
    // tool never sees arguments it does not declare. On the HTTP lanes it is also the TS SSE smoke: every request POST
    // answers with text/event-stream.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task baseline_defaults(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults);

        await AssertBaselineAsync(e2e);
    }

    // WithHttpTransport() as the SDK ships it (stateful on MCP 1.x, stateless on 2.x) and WithAgentCat with no AgentCat
    // setting must track out of the box.
    [Fact]
    public async Task Default_http_transport_tracks()
    {
        await using var e2e = await Lanes.Default.StartAsync(scenario: null);

        await AssertBaselineAsync(e2e);
    }

    // A stateless server builds options for every POST. Once a request is over, nothing AgentCat keeps (the tracker
    // and its seams, the lookup table, the event pipeline, the DI singletons WithAgentCat registers) may hold that
    // POST's options. The host is gone before the GC, so the test keeps those singletons alive itself.
    [Fact]
    public async Task Stateless_post_options_are_not_retained()
    {
        var (references, tracker, hostSingletons) = await ServeStatelessAndDropAsync();

        // The last await can complete inline, which leaves the helper's frames below this one on the stack.
        await Task.Yield();
        Assert.True(references.Count >= 1 + 2 + 20, "Only " + references.Count + " options instances were built.");
        Assert.Empty(await Retention.LiveAfterCollectingAsync(references));
        Assert.Equal(E2EToolkit.ProjectId, tracker.ProjectId);
        Assert.Contains(hostSingletons, s => s is IAgentCat);
        GC.KeepAlive(tracker);
        GC.KeepAlive(hostSingletons);
    }

    // The lane API that the scenario and case tests build on: the server and client hooks, and a second client on one
    // host.
    [Fact]
    public async Task Lane_hooks_and_a_second_client_reach_one_server()
    {
        var transportConfigured = false;
        var clientsConfigured = 0;
        await using var e2e = await Lanes.Get(Lanes.HttpStateless).StartAsync(Scenarios.BaselineDefaults, new LaneSetup
        {
            AgentCat = o => o.EnableReportMissing = false,
            Transport = _ => transportConfigured = true,
            Client = _ => clientsConfigured++,
        });
        var second = await e2e.ConnectClientAsync();

        Assert.True(transportConfigured);
        Assert.Equal(2, clientsConfigured);
        Assert.DoesNotContain((await e2e.ListToolsAsync(second)).Tools, t => t.Name == WireNames.GetMoreToolsName);
        var structured = await e2e.CallToolAsync("structured", """{"msg":"mirrored","context":"first client","session_id":"start"}""");
        Assert.Equal("mirrored", (string?)ToolResults.StructuredContent(structured)?["echoed"]);
        var raw = await e2e.CallToolRawAsync("""{"name":"echo","arguments":{"msg":"raw","context":"second client"}}""", second);
        Assert.Equal("raw", ToolResults.ToolText(raw));

        await e2e.AssertEventCountAsync(2);
        Assert.Single(e2e.Events, e => e.ResourceName == "structured" && e.UserIntent == "first client");
        Assert.Single(e2e.Events, e => e.ResourceName == "echo" && e.UserIntent == "second client");

        // Two connects, one listing, two calls.
        Assert.Equal(2 + 1 + 2, e2e.SsePosts);
    }

    // Tracing off: injection of session_id and agent_id stops, the context parameter stays, no event is published,
    // and the tool still never sees the injected context.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task tracing_off(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.TracingOff);

        var echoProperties = Properties(await ListAsync(e2e, "echo"));
        Assert.Null(echoProperties["session_id"]);
        Assert.Null(echoProperties["agent_id"]);
        Assert.NotNull(echoProperties["context"]);

        var echo = await e2e.CallToolAsync("echo", """{"msg":"hi"}""");
        E2EAssert.Succeeded(echo);
        Assert.Equal("hi", ToolResults.ToolText(echo));
        Assert.Null(ToolResults.MintBackOf(echo));

        var loose = await e2e.CallToolAsync("loose", """{"msg":"hi","customer_extra":"keep-me","context":"tracing off intent"}""");
        E2EAssert.Succeeded(loose);
        Assert.Null(ToolResults.MintBackOf(loose));
        Assert.Equal("""{"msg":"hi","customer_extra":"keep-me"}""", ToolResults.ToolText(loose));
        var strict = await e2e.CallToolAsync("strict", """{"msg":"strictly","context":"tracing off strict"}""");
        E2EAssert.Succeeded(strict);
        Assert.Equal("strictly", ToolResults.ToolText(strict));

        await e2e.AssertEventCountAsync(0);
    }

    // Tracing off still answers get_more_tools, with no mint-back and no event.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task tracing_off_x_report_missing_on(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.TracingOffXReportMissingOn);

        Assert.Contains((await e2e.ListToolsAsync()).Tools, t => t.Name == WireNames.GetMoreToolsName);
        var result = await e2e.CallToolAsync(WireNames.GetMoreToolsName, """{"context":"need a search tool"}""");

        E2EAssert.Succeeded(result);
        Assert.Contains("we have shown you the full tool list", ToolResults.Texts(result)[0], StringComparison.Ordinal);
        Assert.Null(ToolResults.MintBackOf(result));
        await e2e.AssertEventCountAsync(0);
    }

    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task report_missing_off(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.ReportMissingOff);

        var tools = (await e2e.ListToolsAsync()).Tools;
        Assert.DoesNotContain(tools, t => t.Name == WireNames.GetMoreToolsName);
        var properties = Properties(tools.Single(t => t.Name == "echo"));
        Assert.NotNull(properties["session_id"]);
        Assert.NotNull(properties["context"]);

        var echo = await e2e.CallToolAsync("echo", """{"msg":"hi","context":"no gmt"}""");
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(ToolResults.MintBackOf(echo)!, "session_id"));

        await e2e.AssertEventCountAsync(1);
        var e = Assert.Single(e2e.Events);
        Assert.Equal("echo", e.ResourceName);
        Assert.Equal(sessionId, e.SessionId);
        Assert.Equal("no gmt", e.UserIntent);
    }

    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task report_missing_call(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.ReportMissingCall);
        const string context = "Need a database query tool for SQL operations";

        await e2e.ListToolsAsync();
        var result = await e2e.CallToolAsync(WireNames.GetMoreToolsName, "{\"context\":\"" + context + "\"}");

        var texts = ToolResults.Texts(result);
        var mintBack = ToolResults.MintBackOf(result);
        Assert.NotNull(mintBack);
        Assert.Equal(mintBack, texts[0]);
        Assert.Contains("Unfortunately", texts[1], StringComparison.Ordinal);
        Assert.Contains("we have shown you the full tool list", texts[1], StringComparison.Ordinal);
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(mintBack, "session_id"));

        await e2e.AssertEventCountAsync(1);
        var e = Assert.Single(e2e.Events);
        Assert.Equal(WireNames.GetMoreToolsName, e.ResourceName);
        Assert.Equal(context, e.UserIntent);
        Assert.Equal(context, e.Argument("context"));
        Assert.Equal(sessionId, e.SessionId);
        Assert.NotNull(e.Response);
        Assert.DoesNotContain("[session_id", e.Response.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task context_off(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.ContextOff);
        var sessionId = ToolResults.Sid("ctxoff");

        var echoProperties = Properties(await ListAsync(e2e, "echo"));
        Assert.Null(echoProperties["context"]);
        Assert.NotNull(echoProperties["session_id"]);

        var echo = await e2e.CallToolAsync("echo", """{"msg":"hi","session_id":""" + "\"" + sessionId + "\"}");
        Assert.Equal("hi", ToolResults.ToolText(echo));
        Assert.Null(ToolResults.MintBackOf(echo));
        var loose = await e2e.CallToolAsync("loose", """{"msg":"hi","session_id":""" + "\"" + sessionId + "\"}");
        Assert.Equal("""{"msg":"hi"}""", ToolResults.ToolText(loose));

        await e2e.AssertEventCountAsync(2);
        Assert.All(e2e.Events, e =>
        {
            Assert.Equal(sessionId, e.SessionId);
            Assert.Null(e.UserIntent);
            Assert.Equal("supplied", e.Tag(WireNames.TagSessionSource));
        });
    }

    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task context_custom_description(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.ContextCustomDescription);

        var tools = (await e2e.ListToolsAsync()).Tools;
        var echoContext = (string?)Properties(tools.Single(t => t.Name == "echo"))["context"]?["description"];
        Assert.Equal(Scenarios.CustomContextDescriptionValue, echoContext);
        Assert.NotEqual(AgentFacingText.DefaultContextParameterDescription, echoContext);
        var gmtContext = (string?)Properties(tools.Single(t => t.Name == WireNames.GetMoreToolsName))["context"]?["description"];
        Assert.NotNull(gmtContext);
        Assert.NotEqual(Scenarios.CustomContextDescriptionValue, gmtContext);

        await e2e.CallToolAsync("echo", """{"msg":"hi","context":"custom description intent"}""");

        await e2e.AssertEventCountAsync(1);
        var e = Assert.Single(e2e.Events);
        Assert.Equal("echo", e.ResourceName);
        Assert.Equal("custom description intent", e.UserIntent);
    }

    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task agent_tracking_mint(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.AgentTrackingMint);

        var tools = (await e2e.ListToolsAsync()).Tools;
        foreach (var name in new[] { "echo", "structured", WireNames.GetMoreToolsName })
        {
            var schema = ToolResults.InputSchema(tools.Single(t => t.Name == name));
            Assert.NotNull(schema["properties"]!["agent_id"]);
            Assert.NotNull(schema["properties"]!["session_id"]);
            var required = ToolResults.Required(schema);
            Assert.Contains("agent_id", required);
            Assert.Contains("session_id", required);
        }

        var result = await e2e.CallToolAsync("structured", """{"msg":"mint","context":"agent minting","session_id":"start"}""");

        E2EAssert.Succeeded(result);
        var mintBack = ToolResults.MintBackOf(result);
        Assert.NotNull(mintBack);
        Assert.Contains(AgentFacingText.MintBackHeaderIssued, mintBack, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", mintBack, StringComparison.Ordinal);
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(mintBack, "session_id"));
        var structured = ToolResults.StructuredContent(result)!;
        Assert.Equal("mcp_session", structured.First().Key);
        var mcpSession = structured["mcp_session"]!.AsObject();
        Assert.Equal(sessionId, (string?)mcpSession["session_id"]);
        Assert.Equal("issued", (string?)mcpSession["status"]);
        Assert.Null(mcpSession["agent_id"]);
        Assert.Equal("mint", (string?)structured["echoed"]);

        await e2e.AssertEventCountAsync(1);
        var e = Assert.Single(e2e.Events);
        Assert.Equal(sessionId, e.SessionId);
        Assert.Equal("minted", e.Tag(WireNames.TagSessionSource));
        Assert.False(e.Tags!.ContainsKey(WireNames.TagAgentId));
        Assert.False(e.Tags.ContainsKey(WireNames.TagAgentSource));
    }

    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task agent_tracking_supplied(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.AgentTrackingSupplied);
        const string agentId = "opus-4.80-1m|claude-code|k3n9x";

        await e2e.ListToolsAsync();
        var first = await e2e.CallToolAsync("echo", """{"msg":"one","context":"agent supplied: mint first","session_id":"start"}""");
        var mintBack = ToolResults.MintBackOf(first);
        Assert.NotNull(mintBack);
        Assert.Contains(AgentFacingText.MintBackHeaderIssued, mintBack, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_id", mintBack, StringComparison.Ordinal);
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(mintBack, "session_id"));

        // loose proves that both injected arguments are stripped from what the tool receives.
        var second = await e2e.CallToolAsync(
            "loose",
            """{"msg":"two","context":"agent supplied: reuse","session_id":""" + "\"" + sessionId + "\",\"agent_id\":\"" + agentId + "\"}");
        Assert.Null(ToolResults.MintBackOf(second));
        Assert.Equal("""{"msg":"two"}""", ToolResults.ToolText(second));

        await e2e.AssertEventCountAsync(2);
        var firstEvent = Assert.Single(e2e.Events, x => x.Argument("msg") == "one");
        Assert.Equal(sessionId, firstEvent.SessionId);
        Assert.Equal("minted", firstEvent.Tag(WireNames.TagSessionSource));
        Assert.False(firstEvent.Tags!.ContainsKey(WireNames.TagAgentId));
        var secondEvent = Assert.Single(e2e.Events, x => x.Argument("msg") == "two");
        Assert.Equal(sessionId, secondEvent.SessionId);
        Assert.Equal("supplied", secondEvent.Tag(WireNames.TagSessionSource));
        Assert.Equal(agentId, secondEvent.Tag(WireNames.TagAgentId));
        Assert.Equal("supplied", secondEvent.Tag(WireNames.TagAgentSource));
    }

    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task agent_tracking_x_hook(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.AgentTrackingXHook);
        const string agentId = "opus-4.80-1m|claude-code|k3n9x";

        var tools = (await e2e.ListToolsAsync()).Tools;
        foreach (var name in new[] { "echo", WireNames.GetMoreToolsName })
        {
            var schema = ToolResults.InputSchema(tools.Single(t => t.Name == name));
            var required = ToolResults.Required(schema);
            Assert.Null(schema["properties"]!["session_id"]);
            Assert.NotNull(schema["properties"]!["agent_id"]);
            Assert.Contains("agent_id", required);
            Assert.DoesNotContain("session_id", required);
        }

        var one = await e2e.CallToolAsync("echo", """{"msg":"one","context":"agent hook","agent_id":""" + "\"" + agentId + "\"}");
        var two = await e2e.CallToolAsync("loose", """{"msg":"two","context":"agent hook","agent_id":""" + "\"" + agentId + "\"}");
        Assert.Null(ToolResults.MintBackOf(one));
        Assert.Null(ToolResults.MintBackOf(two));
        Assert.Equal("""{"msg":"two"}""", ToolResults.ToolText(two));

        await e2e.AssertEventCountAsync(2);
        Assert.All(e2e.Events, e =>
        {
            E2EAssert.SessionId(e.SessionId);
            Assert.Equal("hook", e.Tag(WireNames.TagSessionSource));
            Assert.Equal(agentId, e.Tag(WireNames.TagAgentId));
            Assert.Equal("supplied", e.Tag(WireNames.TagAgentSource));
        });
        Assert.Single(e2e.Events.Select(e => e.SessionId).Distinct());
    }

    // The hook value derives the same session ID on every call. On the per-request lanes each call is a new server
    // instance, and on HTTP a second client is a second connection, so equality shows the derivation holds no state.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task task_hook_deterministic(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.TaskHookDeterministic);

        await e2e.ListToolsAsync();
        var one = await e2e.CallToolAsync("echo", """{"msg":"one","context":"hook determinism"}""");
        var two = await e2e.CallToolAsync("echo", """{"msg":"two","context":"hook determinism"}""");
        Assert.Null(ToolResults.MintBackOf(one));
        Assert.Null(ToolResults.MintBackOf(two));
        var expected = 2;
        if (e2e.Lane.IsHttp)
        {
            var other = await e2e.ConnectClientAsync();
            var three = await e2e.CallToolAsync("echo", """{"msg":"three","context":"hook determinism"}""", other);
            Assert.Null(ToolResults.MintBackOf(three));
            expected = 3;
        }

        await e2e.AssertEventCountAsync(expected);
        var events = e2e.Events;
        E2EAssert.SessionId(events[0].SessionId);
        Assert.All(events, e =>
        {
            Assert.Equal(events[0].SessionId, e.SessionId);
            Assert.Equal("hook", e.Tag(WireNames.TagSessionSource));
        });
    }

    // A hook that returns null mints a session per call and never announces it: each event gets its own ses_ ID.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task task_hook_null(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.TaskHookNull);

        Assert.Null(Properties(await ListAsync(e2e, "echo"))["session_id"]);
        var one = await e2e.CallToolAsync("echo", """{"msg":"one","context":"hook null"}""");
        var two = await e2e.CallToolAsync("echo", """{"msg":"two","context":"hook null"}""");
        Assert.Null(ToolResults.MintBackOf(one));
        Assert.Null(ToolResults.MintBackOf(two));

        await e2e.AssertEventCountAsync(2);
        var events = e2e.Events;
        Assert.All(events, e =>
        {
            E2EAssert.SessionId(e.SessionId);
            Assert.Equal("minted", e.Tag(WireNames.TagSessionSource));
        });
        Assert.NotEqual(events[0].SessionId, events[1].SessionId);
    }

    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task task_supplied_continuity(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.TaskSuppliedContinuity);

        await e2e.ListToolsAsync();
        var first = await e2e.CallToolAsync("echo", """{"msg":"one","context":"continuity: mint","session_id":"start"}""");
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(ToolResults.MintBackOf(first)!, "session_id"));
        var second = await e2e.CallToolAsync("echo", """{"msg":"two","context":"continuity: supply","session_id":""" + "\"" + sessionId + "\"}");
        Assert.Null(ToolResults.MintBackOf(second));
        Assert.Equal("two", ToolResults.ToolText(second));

        await e2e.AssertEventCountAsync(2);
        var mintedEvent = Assert.Single(e2e.Events, e => e.Argument("msg") == "one");
        Assert.Equal(sessionId, mintedEvent.SessionId);
        Assert.Equal("minted", mintedEvent.Tag(WireNames.TagSessionSource));
        var suppliedEvent = Assert.Single(e2e.Events, e => e.Argument("msg") == "two");
        Assert.Equal(sessionId, suppliedEvent.SessionId);
        Assert.Equal("supplied", suppliedEvent.Tag(WireNames.TagSessionSource));
    }

    // The mint-back reaches the agent twice, as the text block and as the mcp_session mirror at the head of the
    // structured content, and neither reaches the event.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task mint_back_structured_mirror(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.MintBackStructuredMirror);

        var outputSchema = ToolResults.OutputSchema(await ListAsync(e2e, "structured"));
        Assert.NotNull(outputSchema?["properties"]?["mcp_session"]);
        var result = await e2e.CallToolAsync("structured", """{"msg":"mirrored","context":"structured mirror","session_id":"start"}""");

        E2EAssert.Succeeded(result);
        var mintBack = ToolResults.MintBackOf(result);
        Assert.NotNull(mintBack);
        Assert.Contains(AgentFacingText.MintBackHeaderIssued, mintBack, StringComparison.Ordinal);
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(mintBack, "session_id"));
        var structured = ToolResults.StructuredContent(result)!;
        Assert.Equal("mirrored", (string?)structured["echoed"]);
        Assert.Equal("mcp_session", structured.First().Key);
        var mirror = structured["mcp_session"]!.AsObject();
        Assert.Equal(sessionId, (string?)mirror["session_id"]);
        Assert.Equal("issued", (string?)mirror["status"]);
        Assert.False(mirror.ContainsKey("agent_id"));

        await e2e.AssertEventCountAsync(1);
        var e = Assert.Single(e2e.Events);
        Assert.Equal(sessionId, e.SessionId);
        Assert.NotNull(e.Response);
        var response = e.Response.ToJsonString();
        Assert.Contains("mirrored", response, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp_session", response, StringComparison.Ordinal);
        Assert.DoesNotContain("[session_id", response, StringComparison.Ordinal);
    }

    // The listing drops additionalProperties: false so the injected arguments can ride along, and the strict tool, which
    // fails on any key but msg, still succeeds because they are stripped before it runs.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task strict_schema_injection(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.StrictSchemaInjection);
        var sessionId = ToolResults.Sid("strict");

        var schema = ToolResults.InputSchema(await ListAsync(e2e, "strict"));
        Assert.False(schema.ContainsKey("additionalProperties"));
        Assert.NotNull(schema["properties"]!["session_id"]);
        Assert.NotNull(schema["properties"]!["context"]);
        var result = await e2e.CallToolAsync("strict", """{"msg":"strictly","context":"strict schema test","session_id":""" + "\"" + sessionId + "\"}");

        E2EAssert.Succeeded(result);
        Assert.Equal("strictly", Assert.Single(ToolResults.Texts(result)));

        await e2e.AssertEventCountAsync(1);
        var e = Assert.Single(e2e.Events);
        Assert.Equal("strict", e.ResourceName);
        Assert.Equal(sessionId, e.SessionId);
        Assert.Equal("supplied", e.Tag(WireNames.TagSessionSource));
        Assert.Equal("strict schema test", e.UserIntent);
        Assert.Equal(sessionId, e.Argument("session_id"));
    }

    // loose keeps unknown keys, so its output is what reached the tool: the customer's key, and no injected one. The
    // event keeps the raw arguments.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task strip_proof_loose(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.StripProofLoose);
        var sessionId = ToolResults.Sid("loose");

        await e2e.ListToolsAsync();
        var result = await e2e.CallToolAsync(
            "loose",
            """{"msg":"hi","customer_extra":"keep-me","context":"strip proof","session_id":""" + "\"" + sessionId + "\"}");

        E2EAssert.Succeeded(result);
        Assert.Null(ToolResults.MintBackOf(result));
        Assert.Equal("""{"msg":"hi","customer_extra":"keep-me"}""", ToolResults.ToolText(result));

        await e2e.AssertEventCountAsync(1);
        var e = Assert.Single(e2e.Events);
        Assert.Equal(sessionId, e.SessionId);
        Assert.Equal("strip proof", e.UserIntent);
        Assert.Equal(
            """{"msg":"hi","customer_extra":"keep-me","context":"strip proof","session_id":""" + "\"" + sessionId + "\"}",
            e.Arguments!.ToJsonString());
    }

    // A tool that throws: the SDK answers isError, the agent still gets its session, the event carries the real
    // exception, and the server keeps serving.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task error_path(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.ErrorPath);

        await e2e.ListToolsAsync();
        var result = await e2e.CallToolAsync("boom", """{"context":"error path intent"}""");

        Assert.True(result.IsError);
        var mintBack = ToolResults.MintBackOf(result);
        Assert.NotNull(mintBack);
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(mintBack, "session_id"));

        var failed = Assert.Single(await e2e.WaitForEventsAsync(1));
        Assert.Equal("boom", failed.ResourceName);
        Assert.True(failed.IsError);
        Assert.Contains(E2EToolkit.BoomMessage, (string?)failed.Error?["message"] ?? "", StringComparison.Ordinal);
        Assert.Equal("error path intent", failed.UserIntent);
        Assert.Equal(sessionId, failed.SessionId);
        Assert.Equal("minted", failed.Tag(WireNames.TagSessionSource));
        Assert.NotNull(failed.Response);
        Assert.DoesNotContain("[session_id", failed.Response.ToJsonString(), StringComparison.Ordinal);

        var followUp = await e2e.CallToolAsync("echo", """{"msg":"after","context":"error recovery"}""");
        E2EAssert.Succeeded(followUp);
        Assert.Equal("after", ToolResults.ToolText(followUp));

        await e2e.AssertEventCountAsync(2);
        var recovered = Assert.Single(e2e.Events, e => e.ResourceName == "echo");
        Assert.False(recovered.Has("is_error"));
        Assert.False(recovered.Has("error"));
    }

    // Identity, customer tags and properties land on the tool-call event; on a tag key AgentCat owns, AgentCat's value
    // wins.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task hooks_identity_tags_props(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.HooksIdentityTagsProps);

        await e2e.ListToolsAsync();
        await e2e.CallToolAsync("echo", """{"msg":"hooks","context":"hooks combo"}""");

        await e2e.AssertEventCountAsync(1);
        var e = Assert.Single(e2e.Events);
        Assert.Equal(Scenarios.IdentityUserId, e.IdentifyActorGivenId);
        Assert.Equal(Scenarios.IdentityUserName, e.IdentifyActorName);
        Assert.Equal("""{"plan":"enterprise"}""", e.IdentifyData?.ToJsonString());
        Assert.Equal(Scenarios.CustomerTagValue, e.Tag(Scenarios.CustomerTagKey));
        Assert.Equal("minted", e.Tag(WireNames.TagSessionSource));
        Assert.Equal(Scenarios.CustomerPropValue, (string?)e.Properties?[Scenarios.CustomerPropKey]);
    }

    // The redactor rewrites the secret everywhere it may: the response, the raw arguments and the intent. It would also
    // rewrite every ses_ and echo it saw, and the raw arguments show it did; session_id and resource_name kept theirs.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task redaction(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.Redaction);
        var sessionId = ToolResults.Sid("redact");
        var secret = Scenarios.RedactionSecret;
        var replacement = Scenarios.RedactionReplacement;

        await e2e.ListToolsAsync();
        var result = await e2e.CallToolAsync(
            "echo",
            "{\"msg\":\"payload " + secret + " inside\",\"context\":\"intent mentioning " + secret + "\",\"session_id\":\"" + sessionId + "\"}");
        Assert.Equal("payload " + secret + " inside", ToolResults.ToolText(result));

        await e2e.AssertEventCountAsync(1);
        var e = Assert.Single(e2e.Events);
        Assert.NotNull(e.Response);
        var response = e.Response.ToJsonString();
        Assert.Contains(replacement, response, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, response, StringComparison.Ordinal);
        Assert.Equal("payload " + replacement + " inside", e.Argument("msg"));
        Assert.Equal("intent mentioning " + replacement, e.Argument("context"));
        Assert.Equal("intent mentioning " + replacement, e.UserIntent);
        Assert.Equal(sessionId.Replace(Scenarios.RedactionSessionPrefix, replacement, StringComparison.Ordinal), e.Argument("session_id"));
        Assert.Equal(replacement, (string?)e.Parameters?["request"]?["params"]?["name"]);
        Assert.Equal(sessionId, e.SessionId);
        Assert.Equal(Scenarios.RedactionToolName, e.ResourceName);
    }

    // Where the client identity comes from on each lane: the per-request envelope (2026-07-28 clients), the handshake
    // of a stateful server, or nothing on a stateless server that never saw the handshake.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task client_identity_ladder(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.ClientIdentityLadder);
        var known = e2e.Lane.KnowsClientIdentity;

        await e2e.ListToolsAsync();
        await e2e.CallToolAsync("echo", """{"msg":"who am I","context":"identity ladder"}""");
        var e = Assert.Single(await e2e.WaitForEventsAsync(1));
        Assert.Equal(known ? Lanes.ClientName : null, e.ClientName);
        Assert.Equal(known ? Lanes.ClientVersion : null, e.ClientVersion);
        Assert.Equal(e2e.Lane.Envelope ? Lanes.EnvelopeProtocol : null, e.Tag(WireNames.TagProtocolVersion));

        var expected = 1;
        if (Lanes.McpMajor == 1)
        {
            // A protocol version in the request's _meta is read per request. A 2.x server rejects a 2026-07-28 _meta
            // version on a 2025-11-25 connection, and the 2.x envelope lanes already send it on every request.
            await e2e.CallToolParamsAsync(
                """{"name":"echo","arguments":{"msg":"again","context":"identity ladder meta"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}""");
            expected = 2;
        }

        await e2e.AssertEventCountAsync(expected);
        if (expected == 2)
        {
            var meta = Assert.Single(e2e.Events, x => x.Argument("msg") == "again");
            Assert.Equal(Lanes.EnvelopeProtocol, meta.Tag(WireNames.TagProtocolVersion));
        }
    }

    // tools/call before any tools/list, as a raw request: AgentCat rebuilds its registries from the server's own listing,
    // mints, and strips by the rebuilt registry. The loose call proves the registry: agent tracking is off, so loose
    // never had agent_id injected and keeps the one it is sent, while the strip for a tool no registry holds removes
    // every name AgentCat may inject.
    [Theory]
    [MemberData(nameof(LaneData.RebuildLanes), MemberType = typeof(LaneData))]
    public async Task rebuild_on_demand(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.RebuildOnDemand);

        var result = await e2e.CallToolRawAsync("""{"name":"echo","arguments":{"msg":"solo","context":"rebuild on demand"}}""");
        Assert.Contains("solo", ToolResults.Texts(result));
        var mintBack = ToolResults.MintBackOf(result);
        Assert.NotNull(mintBack);
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(mintBack, "session_id"));
        var loose = await e2e.CallToolRawAsync(
            """{"name":"loose","arguments":{"msg":"solo","customer_extra":"keep-me","agent_id":"not-injected","context":"rebuild strip","session_id":""" + "\"" + sessionId + "\"}}");
        Assert.Equal("""{"msg":"solo","customer_extra":"keep-me","agent_id":"not-injected"}""", ToolResults.ToolText(loose));

        await e2e.AssertEventCountAsync(2);
        var e = Assert.Single(e2e.Events, x => x.ResourceName == "echo");
        Assert.Equal(sessionId, e.SessionId);
        Assert.Equal("rebuild on demand", e.UserIntent);
        Assert.Equal("minted", e.Tag(WireNames.TagSessionSource));
        if (e2e.Lane.IsHttp)
        {
            Assert.Single(e2e.Log, l => l == ExpectedLog.Rebuilt);
            Assert.DoesNotContain(e2e.Log, l => l.StartsWith("Warning", StringComparison.Ordinal));
        }
    }

    // One Mcp-Session-Id spans the whole exchange, and the AgentCat session moves from minted to supplied through the
    // session_id argument alone.
    [Theory]
    [MemberData(nameof(LaneData.StatefulHttpLanes), MemberType = typeof(LaneData))]
    public async Task session_continuity_stateful(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.SessionContinuityStateful);
        var transportSession = e2e.Client.SessionId;
        Assert.False(string.IsNullOrEmpty(transportSession));

        await e2e.ListToolsAsync();
        var first = await e2e.CallToolAsync("echo", """{"msg":"one","context":"stateful continuity","session_id":"start"}""");
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(ToolResults.MintBackOf(first)!, "session_id"));
        var second = await e2e.CallToolAsync("echo", """{"msg":"two","context":"stateful continuity","session_id":""" + "\"" + sessionId + "\"}");
        Assert.Null(ToolResults.MintBackOf(second));
        Assert.Equal(transportSession, e2e.Client.SessionId);

        await e2e.AssertEventCountAsync(2);
        var mintedEvent = Assert.Single(e2e.Events, e => e.Argument("msg") == "one");
        Assert.Equal(sessionId, mintedEvent.SessionId);
        Assert.Equal("minted", mintedEvent.Tag(WireNames.TagSessionSource));
        var suppliedEvent = Assert.Single(e2e.Events, e => e.Argument("msg") == "two");
        Assert.Equal(sessionId, suppliedEvent.SessionId);
        Assert.Equal("supplied", suppliedEvent.Tag(WireNames.TagSessionSource));
    }

    private static async Task<Tool> ListAsync(LaneInstance e2e, string name)
        => (await e2e.ListToolsAsync()).Tools.Single(t => t.Name == name);

    private static JsonObject Properties(Tool tool) => ToolResults.InputSchema(tool)["properties"]!.AsObject();

    private static async Task AssertBaselineAsync(LaneInstance e2e)
    {
        var tools = (await e2e.ListToolsAsync()).Tools;
        Assert.Contains(tools, t => t.Name == "echo");
        Assert.Contains(tools, t => t.Name == WireNames.GetMoreToolsName);
        var echoSchema = ToolResults.InputSchema(tools.Single(t => t.Name == "echo"));
        var properties = echoSchema["properties"]!.AsObject();
        Assert.Equal(AgentFacingText.DefaultContextParameterDescription, (string?)properties["context"]?["description"]);
        Assert.NotNull(properties["session_id"]);
        Assert.Equal(AgentFacingText.SessionIdParamPattern, (string?)properties["session_id"]!["pattern"]);
        Assert.Contains("session_id", ToolResults.Required(echoSchema));

        var echo = await e2e.CallToolAsync("echo", """{"msg":"hi","context":"e2e smoke intent"}""");
        E2EAssert.Succeeded(echo);
        var mintBack = ToolResults.MintBackOf(echo);
        Assert.NotNull(mintBack);
        Assert.Contains(AgentFacingText.MintBackHeaderIssued, mintBack, StringComparison.Ordinal);
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(mintBack, "session_id"));
        Assert.Equal("hi", ToolResults.ToolText(echo));

        var e = Assert.Single(await e2e.WaitForEventsAsync(1));
        Assert.Equal(WireNames.ToolCallEventType, e.EventType);
        Assert.Equal("echo", e.ResourceName);
        Assert.Equal(E2EToolkit.ProjectId, e.ProjectId);
        Assert.Equal(sessionId, e.SessionId);
        Assert.Equal("e2e smoke intent", e.UserIntent);
        Assert.Equal("minted", e.Tag(WireNames.TagSessionSource));
        Assert.Equal("hi", e.Argument("msg"));
        Assert.Equal("e2e smoke intent", e.Argument("context"));
        Assert.Equal(e2e.Lane.Envelope ? Lanes.EnvelopeProtocol : null, e.Tag(WireNames.TagProtocolVersion));
        Assert.Equal(E2EToolkit.ServerName, e.ServerName);

        // The C# strip proof: loose returns the arguments it received, strict fails on any key but msg.
        var loose = await e2e.CallToolAsync(
            "loose",
            """{"msg":"hi","customer_extra":"keep-me","context":"loose strip proof","session_id":""" + "\"" + sessionId + "\"}");
        E2EAssert.Succeeded(loose);
        Assert.Null(ToolResults.MintBackOf(loose));
        Assert.Equal("""{"msg":"hi","customer_extra":"keep-me"}""", ToolResults.ToolText(loose));
        var strict = await e2e.CallToolAsync(
            "strict",
            """{"msg":"strictly","context":"strict strip proof","session_id":""" + "\"" + sessionId + "\"}");
        E2EAssert.Succeeded(strict);
        Assert.Equal("strictly", ToolResults.ToolText(strict));

        await e2e.AssertEventCountAsync(3);
        var events = e2e.Events;
        Assert.Single(events, x => x.ResourceName == "echo");
        var looseEvent = Assert.Single(events, x => x.ResourceName == "loose");
        Assert.Equal(sessionId, looseEvent.SessionId);
        Assert.Equal("supplied", looseEvent.Tag(WireNames.TagSessionSource));
        Assert.Equal("loose strip proof", looseEvent.UserIntent);
        Assert.Equal(LooseArguments, looseEvent.Arguments!.Select(p => p.Key));
        Assert.Equal("keep-me", looseEvent.Argument("customer_extra"));
        Assert.Equal(sessionId, looseEvent.Argument("session_id"));
        var strictEvent = Assert.Single(events, x => x.ResourceName == "strict");
        Assert.Equal(sessionId, strictEvent.SessionId);
        Assert.Equal("strict strip proof", strictEvent.UserIntent);
        Assert.Equal("strict strip proof", strictEvent.Argument("context"));
        Assert.Null(strictEvent.IsError);

        if (e2e.Lane.IsHttp)
        {
            // One POST to connect (initialize or server/discover), one listing, three calls.
            Assert.Equal(1 + 1 + 3, e2e.SsePosts);
            Assert.Equal(0, e2e.JsonPosts);

            // Setup lines once per WithAgentCat call, however many options instances the server built.
            Assert.Single(e2e.Log, l => l.StartsWith(ExpectedLog.SetupStartedPrefix, StringComparison.Ordinal));
            Assert.Single(e2e.Log, l => l.StartsWith(ExpectedLog.SetupCompletePrefix, StringComparison.Ordinal));
            Assert.DoesNotContain(e2e.Log, l => l.StartsWith("Warning", StringComparison.Ordinal));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(List<(string Label, WeakReference Reference)> References, AgentCatTracker Tracker, object[] HostSingletons)> ServeStatelessAndDropAsync()
    {
        var recorder = new OptionsRecorder();
        object[] hostSingletons;
        await using (var e2e = await Lanes.Get(Lanes.HttpStateless).StartAsync(
            Scenarios.BaselineDefaults,
            new LaneSetup { Server = b => b.Services.AddSingleton<IPostConfigureOptions<McpServerOptions>>(recorder) }))
        {
            for (var i = 0; i < 2; i++)
            {
                Assert.Contains((await e2e.ListToolsAsync()).Tools, t => t.Name == WireNames.GetMoreToolsName);
            }

            for (var i = 0; i < 18; i++)
            {
                var result = await e2e.CallToolAsync("loose", "{\"msg\":\"m" + i + "\",\"context\":\"retention " + i + "\"}");
                Assert.Equal("{\"msg\":\"m" + i + "\"}", ToolResults.ToolText(result));
            }

            for (var i = 0; i < 2; i++)
            {
                var result = await e2e.CallToolAsync("custom", "{\"sid\":\"" + ToolResults.Sid("retention" + i) + "\",\"context\":\"custom " + i + "\"}");
                Assert.Equal(E2EToolkit.CustomReply, ToolResults.ToolText(result));
            }

            await e2e.AssertEventCountAsync(18 + (2 * 2));
            hostSingletons = e2e.Services.GetServices<IPostConfigureOptions<McpServerOptions>>()
                .Cast<object>()
                .Append(e2e.Services.GetRequiredService<IAgentCat>())
                .ToArray();
        }

        Assert.NotNull(recorder.Tracker);
        return (recorder.References, recorder.Tracker, hostSingletons);
    }

    // Registered after WithAgentCat, so it sees every options instance after AgentCat installed on it.
    private sealed class OptionsRecorder : IPostConfigureOptions<McpServerOptions>
    {
        private readonly object _gate = new();

        internal List<(string Label, WeakReference Reference)> References { get; } = new();

        internal AgentCatTracker? Tracker { get; private set; }

        public void PostConfigure(string? name, McpServerOptions options)
        {
            lock (_gate)
            {
                References.Add(("options #" + References.Count, new WeakReference(options)));
                if (Tracker is null && EngineInstaller.TryGetTracker(options, out var tracker))
                {
                    Tracker = tracker;
                }
            }
        }
    }
}
