using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentCat.Internal.Text;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.E2E;

// End-to-end cases with no TS scenario: what the C# transports and the stateless topology add.
public class E2ECaseTests
{
    private const string ToolSetHeader = "X-AgentCat-E2E-Tool-Set";
    private const string NotesToolSet = "notes";
    private const string NotesContextDescription = "What the note is about";
    private const string HostAProject = "proj_host_a";
    private const string HostBProject = "proj_host_b";

    private static readonly string[] NotesStructuredKeys = { "mcp_session", "received" };

    private static readonly string[] CustomCallIntents = { "publish with a sid", "publish without a sid" };

    // How long a concurrent test's calls wait inside a tool for each other.
    private static readonly TimeSpan OverlapBound = TimeSpan.FromSeconds(5);

    // A session_id that is neither the start sentinel nor a well-formed ses_ value: the agent is told at content[0] and
    // in the mcp_session mirror, the event has no session, and the tool never sees the argument.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task Malformed_session_id_is_unrecognized_on_every_lane(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults);
        const string malformed = "ses_not-a-real-session";
        var unrecognized = AgentFacingText.MintBackHeaderUnrecognized + "\n" + AgentFacingText.MintBackUnrecognizedBody;

        await e2e.ListToolsAsync();
        var structured = await e2e.CallToolAsync("structured", """{"msg":"malformed","context":"malformed structured","session_id":"ses_not-a-real-session"}""");
        var strict = await e2e.CallToolAsync("strict", """{"msg":"malformed","context":"malformed strict","session_id":"ses_not-a-real-session"}""");

        E2EAssert.Succeeded(structured);
        Assert.Equal(unrecognized, ToolResults.MintBackOf(structured));
        Assert.Equal(unrecognized, ToolResults.Texts(structured)[0]);
        var content = ToolResults.StructuredContent(structured)!;
        Assert.Equal("mcp_session", content.First().Key);
        Assert.Equal("""{"status":"unrecognized"}""", content["mcp_session"]!.ToJsonString());
        Assert.Equal("malformed", (string?)content["echoed"]);
        E2EAssert.Succeeded(strict);
        Assert.Equal(new[] { unrecognized, "malformed" }, ToolResults.Texts(strict));

        await e2e.AssertEventCountAsync(2);
        Assert.All(e2e.Events, e =>
        {
            Assert.True(e.Has("session_id"));
            Assert.Null(e.SessionId);
            Assert.Equal("invalid", e.Tag(WireNames.TagSessionSource));
            Assert.Equal(malformed, e.Argument("session_id"));
            Assert.NotNull(e.Response);
            Assert.DoesNotContain("[session_id", e.Response.ToJsonString(), StringComparison.Ordinal);
        });
        Assert.Equal("malformed structured", Assert.Single(e2e.Events, e => e.ResourceName == "structured").UserIntent);
        Assert.Equal("malformed strict", Assert.Single(e2e.Events, e => e.ResourceName == "strict").UserIntent);
    }

    // The Mcp-Session-Id of a stateful HTTP session is the transport's: the event carries it as parameters.extra.sessionId
    // and takes its own session from the session_id argument only.
    [Theory]
    [MemberData(nameof(LaneData.StatefulHttpLanes), MemberType = typeof(LaneData))]
    public async Task Event_session_never_comes_from_the_transport_session(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults);
        var transportSession = e2e.Client.SessionId;
        Assert.False(string.IsNullOrEmpty(transportSession));

        await e2e.ListToolsAsync();
        var first = await e2e.CallToolAsync("transport", """{"context":"transport session","session_id":"start"}""");
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(ToolResults.MintBackOf(first)!, "session_id"));
        Assert.Equal(transportSession, ToolResults.ToolText(first));
        var second = await e2e.CallToolAsync("transport", """{"context":"transport session again","session_id":""" + "\"" + sessionId + "\"}");
        Assert.Equal(transportSession, ToolResults.ToolText(second));
        var unhandled = await e2e.CallToolAsync("echo", """{"msg":"no handle","context":"no session argument"}""");
        var unhandledSession = E2EAssert.SessionId(ToolResults.HandleFrom(ToolResults.MintBackOf(unhandled)!, "session_id"));

        await e2e.AssertEventCountAsync(3);
        Assert.All(e2e.Events, e =>
        {
            E2EAssert.SessionId(e.SessionId);
            Assert.NotEqual(transportSession, e.SessionId);
            Assert.Equal(transportSession, (string?)e.Extra?["sessionId"]);
        });
        Assert.Equal(2, e2e.Events.Count(e => e.SessionId == sessionId));
        Assert.Equal(unhandledSession, Assert.Single(e2e.Events, e => e.ResourceName == "echo").SessionId);
        Assert.NotEqual(sessionId, unhandledSession);
    }

    // One stateful server, three sessions whose calls interleave: every event carries the client of its own session,
    // its own AgentCat session and its own transport session. All nine loose calls are inside the tool at once.
    [Theory]
    [MemberData(nameof(LaneData.StatefulHttpLanes), MemberType = typeof(LaneData))]
    public async Task Concurrent_sessions_keep_their_own_identity(string lane)
    {
        const int rounds = 4;
        var names = new[] { "agent-alpha", "agent-beta", "agent-gamma" };
        var gate = new E2EToolkit.OverlapGate(names.Length * (rounds - 1), OverlapBound);
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup
        {
            Server = b => b.Services.AddSingleton(gate),
        });
        var clients = new McpClient[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var info = ClientInfo(names[i], "2." + i);
            clients[i] = await e2e.ConnectClientAsync(o => o.ClientInfo = info);
        }

        var transportSessions = clients.Select(c => c.SessionId).ToArray();
        Assert.All(transportSessions, s => Assert.False(string.IsNullOrEmpty(s)));
        Assert.Equal(names.Length, transportSessions.Distinct().Count());

        var minted = await Task.WhenAll(names.Select(async (name, i) =>
        {
            var result = await e2e.CallToolAsync("echo", Arguments(name + "-0", name + " opens", "start"), clients[i]);
            return E2EAssert.SessionId(ToolResults.HandleFrom(ToolResults.MintBackOf(result)!, "session_id"));
        }));
        Assert.Equal(names.Length, minted.Distinct().Count());
        var calls = new List<Task<CallToolResult>>();
        for (var round = 1; round < rounds; round++)
        {
            for (var i = 0; i < names.Length; i++)
            {
                calls.Add(e2e.CallToolAsync("loose", Arguments(names[i] + "-" + round, names[i] + " round " + round, minted[i]), clients[i]));
            }
        }

        var results = await Task.WhenAll(calls);
        Assert.True(gate.Reached, "The " + gate.Participants + " loose calls were never inside the tool at once.");
        Assert.All(results, E2EAssert.Succeeded);
        Assert.All(results, r => Assert.Null(ToolResults.MintBackOf(r)));

        await e2e.AssertEventCountAsync(names.Length * rounds);
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            var own = e2e.Events.Where(e => e.Argument("msg")!.StartsWith(name + "-", StringComparison.Ordinal)).ToArray();
            Assert.Equal(rounds, own.Length);
            Assert.All(own, e =>
            {
                Assert.Equal(name, e.ClientName);
                Assert.Equal("2." + i, e.ClientVersion);
                Assert.Equal(minted[i], e.SessionId);
                Assert.Equal(transportSessions[i], (string?)e.Extra?["sessionId"]);
                Assert.StartsWith(name + " ", e.UserIntent, StringComparison.Ordinal);
            });
        }
    }

    // ConfigureSessionOptions adds a tool that declares its own context to the requests of some clients only. The
    // listings the tracker has seen lack it, so its first calls miss the registry: AgentCat rebuilds from that request's
    // options, the tool keeps its context, the injected session_id is stripped, and its result gets the mcp_session
    // mirror its output schema declares. The calls of several clients are all inside the tools at once.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task Per_request_tool_sets_keep_their_own_arguments(string lane)
    {
        const int noteTakerCount = 3;
        const int callsPerClient = 2;
        var notes = E2EToolkit.CreateTool(typeof(NotesTools).GetMethod(nameof(NotesTools.Notes))!);
        var gate = new E2EToolkit.OverlapGate((noteTakerCount + 1) * callsPerClient, OverlapBound);
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup
        {
            Server = b => b.Services.AddSingleton(gate),
            Transport = o => o.ConfigureSessionOptions = (http, options, _) =>
            {
                if (http.Request.Headers[ToolSetHeader] == NotesToolSet)
                {
                    options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
                    options.ToolCollection.Add(notes);
                }

                return Task.CompletedTask;
            },
        });
        var plain = e2e.Client;
        var noteTakers = new McpClient[noteTakerCount];
        for (var i = 0; i < noteTakers.Length; i++)
        {
            noteTakers[i] = await e2e.ConnectClientAsync(headers: new Dictionary<string, string> { [ToolSetHeader] = NotesToolSet });
        }

        Assert.DoesNotContain((await e2e.ListToolsAsync(plain)).Tools, t => t.Name == NotesTools.Name);

        var noteCalls = new List<(string Msg, string Context, Task<CallToolResult> Call)>();
        var looseCalls = new List<(string Msg, Task<CallToolResult> Call)>();
        for (var j = 0; j < callsPerClient; j++)
        {
            for (var i = 0; i < noteTakers.Length; i++)
            {
                var msg = "note-" + i + "-" + j;
                var context = "notes intent " + i + "-" + j;
                noteCalls.Add((msg, context, e2e.CallToolAsync(NotesTools.Name, Arguments(msg, context, "start"), noteTakers[i])));
            }

            var looseMsg = "plain-" + j;
            looseCalls.Add((looseMsg, e2e.CallToolAsync("loose", Arguments(looseMsg, "plain intent " + j, "start"), plain)));
        }

        await Task.WhenAll(noteCalls.Select(c => c.Call).Concat(looseCalls.Select(c => c.Call)));
        Assert.True(gate.Reached, "The " + gate.Participants + " calls were never inside the tools at once.");

        var minted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (msg, context, call) in noteCalls)
        {
            var result = await call;
            E2EAssert.Succeeded(result);
            var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(ToolResults.MintBackOf(result)!, "session_id"));
            minted[msg] = sessionId;
            var received = "{\"msg\":\"" + msg + "\",\"context\":\"" + context + "\"}";
            Assert.Equal(received, (string?)JsonNode.Parse(ToolResults.ToolText(result))?["received"]);
            var structured = ToolResults.StructuredContent(result)!;
            Assert.Equal(NotesStructuredKeys, structured.Select(p => p.Key));
            Assert.Equal(sessionId, (string?)structured["mcp_session"]!["session_id"]);
            Assert.Equal("issued", (string?)structured["mcp_session"]!["status"]);
            Assert.Equal(received, (string?)structured["received"]);
        }

        foreach (var (msg, call) in looseCalls)
        {
            var result = await call;
            E2EAssert.Succeeded(result);
            Assert.Equal("{\"msg\":\"" + msg + "\"}", ToolResults.ToolText(result));
        }

        Assert.Equal(noteCalls.Count, minted.Values.Distinct().Count());
        await e2e.AssertEventCountAsync(noteCalls.Count + looseCalls.Count);
        foreach (var (msg, context, _) in noteCalls)
        {
            var e = Assert.Single(e2e.Events, x => x.Argument("msg") == msg);
            Assert.Equal(NotesTools.Name, e.ResourceName);
            Assert.Equal(context, e.UserIntent);
            Assert.Equal(minted[msg], e.SessionId);
            Assert.Equal("minted", e.Tag(WireNames.TagSessionSource));
            Assert.Equal("start", e.Argument("session_id"));
            Assert.NotNull(e.Response);
            Assert.DoesNotContain("mcp_session", e.Response.ToJsonString(), StringComparison.Ordinal);
        }

        // Concurrent misses rebuild at once, and only the first adds the tool.
        Assert.Single(e2e.Log, l => l == ExpectedLog.Rebuilt);
        Assert.DoesNotContain(e2e.Log, l => l.StartsWith("Warning", StringComparison.Ordinal));

        var listed = (await e2e.ListToolsAsync(noteTakers[0])).Tools.Single(t => t.Name == NotesTools.Name);
        var properties = ToolResults.InputSchema(listed)["properties"]!.AsObject();
        Assert.Equal(NotesContextDescription, (string?)properties["context"]?["description"]);
        Assert.NotNull(properties["session_id"]);
        Assert.NotNull(ToolResults.OutputSchema(listed)?["properties"]?["mcp_session"]);
    }

    // A tool publishes a custom event through the McpServer it was given. On the stateless lanes that server's options
    // were built for this POST alone, and the event must still find the tracker: the tracked project, the server
    // identity and the session, the caller's sid or else the session of the tool call.
    [Theory]
    [MemberData(nameof(LaneData.AllLanes), MemberType = typeof(LaneData))]
    public async Task Custom_event_from_a_tool_reaches_the_tracker_on_every_lane(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults);
        var sid = ToolResults.Sid("customtool");
        var known = e2e.Lane.KnowsClientIdentity;

        var first = await e2e.CallToolAsync("custom", "{\"sid\":\"" + sid + "\",\"context\":\"publish with a sid\"}");
        var other = e2e.Lane.IsHttp ? await e2e.ConnectClientAsync() : e2e.Client;
        var second = await e2e.CallToolAsync("custom", """{"context":"publish without a sid"}""", other);
        Assert.Equal(E2EToolkit.CustomReply, ToolResults.ToolText(first));
        Assert.Equal(E2EToolkit.CustomReply, ToolResults.ToolText(second));
        var minted = E2EAssert.SessionId(ToolResults.HandleFrom(ToolResults.MintBackOf(second)!, "session_id"));

        await e2e.AssertEventCountAsync(4);
        var custom = e2e.Events.Where(e => e.EventType == WireNames.CustomEventType).ToArray();
        Assert.Equal(2, custom.Length);
        Assert.All(custom, e =>
        {
            Assert.Equal(E2EToolkit.ProjectId, e.ProjectId);
            Assert.Equal(E2EToolkit.CustomResourceName, e.ResourceName);
            Assert.Equal(E2EToolkit.CustomMessage, e.UserIntent);
            Assert.Equal(E2EToolkit.ServerName, e.ServerName);
            Assert.Equal(E2EToolkit.ServerVersion, e.ServerVersion);
            Assert.Equal(known ? Lanes.ClientName : null, e.ClientName);
        });
        Assert.Single(custom, e => e.SessionId == sid);
        Assert.Single(custom, e => e.SessionId == minted);

        var calls = e2e.Events.Where(e => e.EventType == WireNames.ToolCallEventType).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, e =>
        {
            Assert.Equal("custom", e.ResourceName);
            Assert.Equal(E2EToolkit.ProjectId, e.ProjectId);
        });
        Assert.Equal(CustomCallIntents, calls.Select(e => e.UserIntent).Order(StringComparer.Ordinal));
        Assert.Equal(minted, Assert.Single(calls, e => e.UserIntent == "publish without a sid").SessionId);

        if (e2e.Lane.IsHttp)
        {
            Assert.Single(e2e.Log, l => l == "Published custom event for session " + sid + " with type 'agentcat:custom'");
            Assert.Single(e2e.Log, l => l == "Published custom event for session " + minted + " with type 'agentcat:custom'");
            Assert.DoesNotContain(e2e.Log, l => l.StartsWith("Warning", StringComparison.Ordinal));
            Assert.DoesNotContain(e2e.Log, l => l.StartsWith("publishCustomEvent:", StringComparison.Ordinal));
        }
    }

    // A stateless server builds a new server for every POST. The agent opens a task, then sends the issued session to a
    // tool that publishes a custom event without a sid: the two tool calls and the custom event share that session.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task Custom_event_from_a_tool_joins_the_session_of_its_tool_call_on_a_stateless_server(string lane)
    {
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults);

        var opened = await e2e.CallToolAsync("echo", """{"msg":"open","context":"open the task","session_id":"start"}""");
        var sessionId = E2EAssert.SessionId(ToolResults.HandleFrom(ToolResults.MintBackOf(opened)!, "session_id"));
        var completed = await e2e.CallToolAsync("custom", "{\"context\":\"complete the task\",\"session_id\":\"" + sessionId + "\"}");
        Assert.Equal(E2EToolkit.CustomReply, ToolResults.ToolText(completed));
        Assert.Null(ToolResults.MintBackOf(completed));

        await e2e.AssertEventCountAsync(3);
        Assert.All(e2e.Events, e => Assert.Equal(sessionId, e.SessionId));
        Assert.Equal("open the task", Assert.Single(e2e.Events, e => e.ResourceName == "echo").UserIntent);
        Assert.Equal("complete the task", Assert.Single(e2e.Events, e => e.EventType == WireNames.ToolCallEventType && e.ResourceName == "custom").UserIntent);
        var custom = Assert.Single(e2e.Events, e => e.EventType == WireNames.CustomEventType);
        Assert.Equal(E2EToolkit.CustomResourceName, custom.ResourceName);
        Assert.Equal(E2EToolkit.ProjectId, custom.ProjectId);
        Assert.Single(e2e.Log, l => l == "Published custom event for session " + sessionId + " with type 'agentcat:custom'");
        Assert.DoesNotContain(e2e.Log, l => l.StartsWith("publishCustomEvent:", StringComparison.Ordinal));
    }

    // Two servers in one process, each tracked under its own project: a custom event from a tool reaches the tracker of
    // the server that ran the tool, alone or with the other server's tool running at the same time.
    [Theory]
    [MemberData(nameof(LaneData.HttpLanes), MemberType = typeof(LaneData))]
    public async Task Custom_event_from_a_tool_reaches_the_tracker_of_its_own_server(string lane)
    {
        await using var a = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup { ProjectId = HostAProject });
        await using var b = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup { ProjectId = HostBProject });
        var sidA = ToolResults.Sid("hosta");
        var sidB = ToolResults.Sid("hostb");

        var alone = await b.CallToolAsync("custom", "{\"sid\":\"" + sidB + "\",\"context\":\"host b alone\"}");
        Assert.Equal(E2EToolkit.CustomReply, ToolResults.ToolText(alone));
        await b.AssertEventCountAsync(2);
        await a.AssertEventCountAsync(0);
        Assert.All(b.Events, e => Assert.Equal(HostBProject, e.ProjectId));
        Assert.Equal(sidB, Assert.Single(b.Events, e => e.EventType == WireNames.CustomEventType).SessionId);

        var together = await Task.WhenAll(
            a.CallToolAsync("custom", "{\"sid\":\"" + sidA + "\",\"context\":\"host a together\"}"),
            b.CallToolAsync("custom", "{\"sid\":\"" + sidB + "\",\"context\":\"host b together\"}"));
        Assert.All(together, r => Assert.Equal(E2EToolkit.CustomReply, ToolResults.ToolText(r)));
        await a.AssertEventCountAsync(2);
        await b.AssertEventCountAsync(4);
        Assert.All(a.Events, e => Assert.Equal(HostAProject, e.ProjectId));
        Assert.Equal(sidA, Assert.Single(a.Events, e => e.EventType == WireNames.CustomEventType).SessionId);
        Assert.Equal("host a together", Assert.Single(a.Events, e => e.EventType == WireNames.ToolCallEventType).UserIntent);
        Assert.All(b.Events, e => Assert.Equal(HostBProject, e.ProjectId));
        Assert.Equal(2, b.Events.Count(e => e.EventType == WireNames.CustomEventType && e.SessionId == sidB));
    }

    // Five clients, no listing, fifty calls at once on a stateless server: one event per call with its own intent and
    // session, the tool never sees an injected argument, and the setup runs once however many options instances the
    // server builds. At least ten calls are inside the tool at once.
    [Theory]
    [MemberData(nameof(LaneData.StatelessLanes), MemberType = typeof(LaneData))]
    public async Task Concurrent_stateless_calls_each_publish_one_event(string lane)
    {
        var gate = new E2EToolkit.OverlapGate(10, OverlapBound);
        await using var e2e = await Lanes.Get(lane).StartAsync(Scenarios.BaselineDefaults, new LaneSetup
        {
            Server = b => b.Services.AddSingleton(gate),
        });
        var clients = new List<McpClient> { e2e.Client };
        for (var i = 1; i < 5; i++)
        {
            clients.Add(await e2e.ConnectClientAsync());
        }

        const int count = 50;
        var calls = Enumerable.Range(0, count).Select(i =>
        {
            var msg = "m" + i;
            var intent = "concurrent intent " + i;
            var session = i % 2 == 0 ? ToolResults.Sid("concurrent" + i.ToString("D2", CultureInfo.InvariantCulture)) : "start";
            return (Msg: msg, Intent: intent, Session: session, Call: e2e.CallToolAsync("loose", Arguments(msg, intent, session), clients[i % clients.Count]));
        }).ToArray();
        await Task.WhenAll(calls.Select(c => c.Call));
        Assert.True(gate.Reached, "No " + gate.Participants + " loose calls were inside the tool at once.");

        var sessions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (msg, _, session, call) in calls)
        {
            var result = await call;
            E2EAssert.Succeeded(result);
            Assert.Equal("{\"msg\":\"" + msg + "\"}", ToolResults.ToolText(result));
            if (session == "start")
            {
                sessions[msg] = E2EAssert.SessionId(ToolResults.HandleFrom(ToolResults.MintBackOf(result)!, "session_id"));
            }
            else
            {
                Assert.Null(ToolResults.MintBackOf(result));
                sessions[msg] = session;
            }
        }

        Assert.Equal(count, sessions.Values.Distinct().Count());
        await e2e.AssertEventCountAsync(count);
        Assert.Equal(count, e2e.Events.Select(e => e.UserIntent).Distinct().Count());
        foreach (var (msg, intent, session, _) in calls)
        {
            var e = Assert.Single(e2e.Events, x => x.Argument("msg") == msg);
            Assert.Equal(intent, e.UserIntent);
            Assert.Equal(sessions[msg], e.SessionId);
            Assert.Equal(session == "start" ? "minted" : "supplied", e.Tag(WireNames.TagSessionSource));
        }

        Assert.Single(e2e.Log, l => l.StartsWith(ExpectedLog.SetupStartedPrefix, StringComparison.Ordinal));
        Assert.Single(e2e.Log, l => l.StartsWith(ExpectedLog.SetupCompletePrefix, StringComparison.Ordinal));
        Assert.Single(e2e.Log, l => l == ExpectedLog.Rebuilt);
        Assert.DoesNotContain(e2e.Log, l => l.StartsWith("Warning", StringComparison.Ordinal));
    }

    private static string Arguments(string msg, string context, string sessionId)
        => new JsonObject { ["msg"] = msg, ["context"] = context, ["session_id"] = sessionId }.ToJsonString();

    private static Implementation ClientInfo(string name, string version)
        => (Implementation)JsonSerializer.Deserialize(
            new JsonObject { ["name"] = name, ["version"] = version }.ToJsonString(),
            McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Implementation)))!;

    internal static class NotesTools
    {
        internal const string Name = "notes";

        [McpServerTool(Name = Name, UseStructuredContent = true)]
        [Description("Takes a note; only some requests have this tool")]
        public static async Task<NotesReceived> Notes(
            RequestContext<CallToolRequestParams> request,
            [Description(NotesContextDescription)] string context,
            CancellationToken cancellationToken,
            string? msg = null)
        {
            _ = context;
            _ = msg;
            await E2EToolkit.OverlapGate.PassAsync(request.Services, cancellationToken);
            return new NotesReceived(E2EToolkit.ReceivedJson(request.Params?.Arguments));
        }
    }

    internal sealed class NotesReceived(string received)
    {
        [JsonPropertyName("received")]
        [Description("The arguments the tool received, as JSON")]
        public string Received { get; } = received;
    }
}
