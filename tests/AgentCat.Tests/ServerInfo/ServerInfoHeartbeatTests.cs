#pragma warning disable CA1861
using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentCat.Internal.Runtime;
using AgentCat.Tests.TestSupport;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentCat.Tests.ServerInfo;

public class ServerInfoHeartbeatTests
{
    private const string Boom = "heartbeat-test-boom";

    private static string[] Names(JsonNode? list, string key = "name")
        => list!.AsArray().Select(item => (string)item![key]!).ToArray();

    // The text of the tool itself: AgentCat can put its session text in front of it.
    private static string TextOf(CallToolResult result)
        => (string)HeartbeatLab.Wire(result)["content"]!.AsArray().Last()!["text"]!;

    [Fact]
    public async Task V1_one_tool_call_sends_the_six_keys_and_every_list()
    {
        await using var lab = new HeartbeatLab();
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        await server.CallToolAsync("alpha");

        var body = await lab.SingleBodyAsync();
        Assert.Equal(
            new[] { "project_id", "server_name", "server_version", "server_title", "instructions", "features" },
            body.Select(p => p.Key).ToArray());
        Assert.Equal(HeartbeatLab.Project, (string)body["project_id"]!);
        Assert.Equal("demo-server", (string)body["server_name"]!);
        Assert.Equal("1.2.3", (string)body["server_version"]!);
        Assert.Equal("Demo Title", (string)body["server_title"]!);
        Assert.Equal("Use the demo tools.", (string)body["instructions"]!);

        var features = body["features"]!.AsObject();
        Assert.Equal(new[] { "tools", "resources", "resourceTemplates", "prompts" }, features.Select(p => p.Key).ToArray());

        var client = await server.ListToolsAsync();
        var seen = new JsonArray(client.Tools.Select(t => HeartbeatLab.Wire(t)).ToArray());
        Assert.Equal(new[] { "alpha", "beta", "gamma", "get_more_tools" }, Names(seen));
        Assert.True(JsonNode.DeepEquals(seen, features["tools"]), features["tools"]!.ToJsonString());

        var beta = features["tools"]![1]!;
        Assert.Equal(HeartbeatLab.LongDescription, (string)beta["description"]!);
        Assert.Equal("Beta Tool", (string)beta["title"]!);
        Assert.True((bool)beta["annotations"]!["readOnlyHint"]!);
        Assert.Equal("The query.", (string)beta["inputSchema"]!["properties"]!["q"]!["description"]!);
        Assert.Equal("The count.", (string)beta["outputSchema"]!["properties"]!["n"]!["description"]!);
        Assert.NotNull(beta["inputSchema"]!["properties"]!["context"]);

        Assert.Equal(new[] { "file:///one.txt", "file:///two.txt" }, Names(features["resources"], "uri"));
        Assert.Equal(new[] { "file:///{path}" }, Names(features["resourceTemplates"], "uriTemplate"));
        Assert.Equal(new[] { "greet", "bye" }, Names(features["prompts"]));
        Assert.Equal("who", (string)features["prompts"]![0]!["arguments"]![0]!["name"]!);

        Assert.Equal(
            "Sent server info heartbeat | 4 tools | " + lab.Api.RawBodies[0].Length + " bytes",
            Assert.Single(lab.HeartbeatLines()));
    }

    [Fact]
    public async Task V1_collection_items_follow_the_handler_items_without_a_duplicate()
    {
        await using var lab = new HeartbeatLab();
        var harness = HeartbeatLab.Describe(new McpHarness(), "collections")
            .AddHandlerTool(HeartbeatLab.ToolJson("from_handler"), (_, _) => HeartbeatLab.Ok())
            .AddHandlerTool(HeartbeatLab.ToolJson("twice", "The handler's."), (_, _) => HeartbeatLab.Ok())
            .Configure(o =>
            {
                o.ToolCollection = new McpServerPrimitiveCollection<McpServerTool>
                {
                    McpServerTool.Create(() => "x", new McpServerToolCreateOptions { Name = "from_collection" }),
                    McpServerTool.Create(() => "x", new McpServerToolCreateOptions { Name = "twice", Description = "The collection's." }),
                };
                o.ResourceCollection = new McpServerResourceCollection
                {
                    McpServerResource.Create(() => "x", new McpServerResourceCreateOptions { UriTemplate = "file:///fixed.txt", Name = "fixed" }),
                    McpServerResource.Create((string id) => id, new McpServerResourceCreateOptions { UriTemplate = "item:///{id}", Name = "item" }),
                };
                o.PromptCollection = new McpServerPrimitiveCollection<McpServerPrompt>
                {
                    McpServerPrompt.Create(() => "hello", new McpServerPromptCreateOptions { Name = "hello" }),
                };
            });
        var server = await lab.StartAsync(harness);

        var client = await server.ListToolsAsync();

        var features = (await lab.SingleBodyAsync())["features"]!;
        Assert.Equal(new[] { "from_handler", "twice", "from_collection", "get_more_tools" }, Names(features["tools"]));
        Assert.Equal("The handler's.", (string)features["tools"]![1]!["description"]!);
        Assert.Equal(Names(features["tools"]), client.Tools.Select(t => t.Name).Distinct().ToArray());
        Assert.Equal(new[] { "file:///fixed.txt" }, Names(features["resources"], "uri"));
        Assert.Equal(new[] { "item:///{id}" }, Names(features["resourceTemplates"], "uriTemplate"));
        Assert.Equal(new[] { "hello" }, Names(features["prompts"]));
    }

    [Fact]
    public async Task V2_five_tools_on_three_pages_are_listed_with_get_more_tools_one_time()
    {
        await using var lab = new HeartbeatLab();
        var pages = new Dictionary<string, string>
        {
            [""] = "{\"tools\":[" + HeartbeatLab.ToolJson("t1") + "," + HeartbeatLab.ToolJson("t2") + "],\"nextCursor\":\"p2\"}",
            ["p2"] = "{\"tools\":[" + HeartbeatLab.ToolJson("t3") + "," + HeartbeatLab.ToolJson("t4") + "],\"nextCursor\":\"p3\"}",
            ["p3"] = "{\"tools\":[" + HeartbeatLab.ToolJson("t5") + "]}",
        };
        var harness = HeartbeatLab.Describe(new McpHarness(), "paged").Configure(o =>
        {
            o.Handlers.ListToolsHandler = (context, _) => new ValueTask<ListToolsResult>(Mcp.ListResult(pages[context.Params?.Cursor ?? ""]));
            o.Handlers.CallToolHandler = (_, _) => HeartbeatLab.Ok();
        });
        var server = await lab.StartAsync(harness);

        await server.CallToolAsync("t1");

        var tools = (await lab.SingleBodyAsync())["features"]!["tools"];
        Assert.Equal(new[] { "t1", "t2", "get_more_tools", "t3", "t4", "t5" }, Names(tools));
        var second = await server.ListToolsAsync("p2");
        Assert.Contains("get_more_tools", second.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task V3_a_server_with_tools_only_has_the_tools_key_and_no_other()
    {
        await using var lab = new HeartbeatLab();
        var harness = new McpHarness().AddTool(() => "ok", "alpha");
        var server = await lab.StartAsync(harness);

        await server.CallToolAsync("alpha");

        var body = await lab.SingleBodyAsync();
        Assert.Equal(new[] { "tools" }, body["features"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "alpha", "get_more_tools" }, Names(body["features"]!["tools"]));
        Assert.Equal("", (string)body["server_title"]!);
        Assert.Equal("", (string)body["instructions"]!);
    }

    [Fact]
    public async Task V4_a_failing_prompt_list_is_left_out_and_the_tools_are_complete()
    {
        await using var lab = new HeartbeatLab();
        var harness = HeartbeatLab.DemoServer()
            .Configure(o => HeartbeatLab.SetPrompts(o, (_, _) => throw new InvalidOperationException(Boom)));
        var server = await lab.StartAsync(harness);

        await server.CallToolAsync("alpha");

        var features = (await lab.SingleBodyAsync())["features"]!.AsObject();
        Assert.Equal(new[] { "tools", "resources", "resourceTemplates" }, features.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "alpha", "beta", "gamma", "get_more_tools" }, Names(features["tools"]));
        Assert.Contains("Server info heartbeat leaves out the prompts list: InvalidOperationException: " + Boom, lab.Log.Lines);
    }

    [Fact]
    public async Task V4_a_failing_resource_list_and_a_template_list_with_no_result_are_left_out()
    {
        await using var lab = new HeartbeatLab();
        var harness = HeartbeatLab.DemoServer().Configure(o =>
        {
            o.Handlers.ListResourcesHandler = (_, _) => throw new InvalidOperationException(Boom);
            o.Handlers.ListResourceTemplatesHandler = (_, _) => new ValueTask<ListResourceTemplatesResult>((ListResourceTemplatesResult)null!);
        });
        var server = await lab.StartAsync(harness);

        await server.CallToolAsync("alpha");

        var features = (await lab.SingleBodyAsync())["features"]!.AsObject();
        Assert.Equal(new[] { "tools", "prompts" }, features.Select(p => p.Key).ToArray());
        Assert.Contains("Server info heartbeat leaves out the resources list: InvalidOperationException: " + Boom, lab.Log.Lines);
        Assert.Contains(
            "Server info heartbeat leaves out the resourceTemplates list: InvalidOperationException: resources/templates/list returned no list",
            lab.Log.Lines);
    }

    // After a first tool list request the call needs no list of its own, so only the heartbeat reads the list.
    private static async Task FirstHeartbeatAsync(HeartbeatLab lab, McpHarness server)
    {
        await server.ListToolsAsync();
        await lab.SingleBodyAsync();
        lab.Advance(3600);
    }

    [Fact]
    public async Task V5_a_failing_tool_list_sends_nothing()
    {
        await using var lab = new HeartbeatLab();
        var failing = false;
        var harness = HeartbeatLab.DemoServer().Configure(o =>
        {
            var list = o.Handlers.ListToolsHandler!;
            o.Handlers.ListToolsHandler = (context, token)
                => Volatile.Read(ref failing) ? throw new InvalidOperationException(Boom) : list(context, token);
        });
        var server = await lab.StartAsync(harness);
        await FirstHeartbeatAsync(lab, server);

        Volatile.Write(ref failing, true);
        var result = await server.CallToolAsync("alpha");
        await lab.IdleAsync();

        Assert.Equal("ok", TextOf(result));
        Assert.Equal(2, lab.Heartbeat.Guard.Begun);
        Assert.Equal(1, lab.Api.Count);
        Assert.Equal("Server info heartbeat not sent: InvalidOperationException: " + Boom, lab.HeartbeatLines().Last());
    }

    [Fact]
    public async Task V6_a_list_whose_cursor_repeats_fails_within_100_pages()
    {
        await using var lab = new HeartbeatLab();
        var promptCalls = 0;
        var harness = HeartbeatLab.DemoServer().Configure(o => HeartbeatLab.SetPrompts(o, (_, _) =>
        {
            Interlocked.Increment(ref promptCalls);
            return new ValueTask<ListPromptsResult>(HeartbeatLab.Json<ListPromptsResult>("{\"prompts\":[{\"name\":\"again\"}],\"nextCursor\":\"same\"}"));
        }));
        var server = await lab.StartAsync(harness);
        var watch = Stopwatch.StartNew();

        await server.CallToolAsync("alpha");

        var features = (await lab.SingleBodyAsync())["features"]!.AsObject();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.False(features.ContainsKey("prompts"));
        Assert.Equal(4, features["tools"]!.AsArray().Count);
        Assert.InRange(promptCalls, 1, 100);
        Assert.Contains("Server info heartbeat leaves out the prompts list: InvalidOperationException: prompts/list returned the same cursor again", lab.Log.Lines);
    }

    [Fact]
    public async Task V6_a_list_with_more_than_100_pages_fails_after_100_calls()
    {
        await using var lab = new HeartbeatLab();
        var promptCalls = 0;
        var harness = HeartbeatLab.DemoServer().Configure(o => HeartbeatLab.SetPrompts(o, (_, _) =>
        {
            var page = Interlocked.Increment(ref promptCalls);
            return new ValueTask<ListPromptsResult>(HeartbeatLab.Json<ListPromptsResult>("{\"prompts\":[{\"name\":\"p" + page + "\"}],\"nextCursor\":\"c" + page + "\"}"));
        }));
        var server = await lab.StartAsync(harness);

        await server.CallToolAsync("alpha");

        var features = (await lab.SingleBodyAsync())["features"]!.AsObject();
        Assert.False(features.ContainsKey("prompts"));
        Assert.Equal(100, promptCalls);
        Assert.Contains("Server info heartbeat leaves out the prompts list: InvalidOperationException: prompts/list has more than 100 pages", lab.Log.Lines);
    }

    [Fact]
    public async Task V7_three_requests_send_one_heartbeat_and_the_next_is_due_after_3600_seconds()
    {
        await using var lab = new HeartbeatLab();
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        await server.CallToolAsync("alpha");
        await server.ListToolsAsync();
        await server.CallToolAsync("gamma");
        Assert.Single(await lab.Api.WaitForAsync(1));
        Assert.Equal(1, lab.Heartbeat.Guard.Begun);

        lab.Advance(3599);
        await server.CallToolAsync("alpha");
        Assert.Equal(1, lab.Heartbeat.Guard.Begun);

        lab.Advance(1);
        await server.CallToolAsync("alpha");
        Assert.Equal(2, lab.Heartbeat.Guard.Begun);
        Assert.Equal(2, (await lab.Api.WaitForAsync(2)).Count);
        await lab.IdleAsync();
    }

    [Fact]
    public async Task V8_two_servers_with_one_project_and_name_send_one_heartbeat_and_another_name_sends_a_second()
    {
        await using var lab = new HeartbeatLab();
        var first = await lab.StartAsync(HeartbeatLab.DemoServer());
        var second = await lab.StartAsync(HeartbeatLab.DemoServer());
        var other = await lab.StartAsync(HeartbeatLab.DemoServer("other-server"));

        await first.CallToolAsync("alpha");
        await second.CallToolAsync("alpha");
        await second.ListToolsAsync();
        Assert.Equal(1, lab.Heartbeat.Guard.Begun);

        await other.CallToolAsync("alpha");
        Assert.Equal(2, lab.Heartbeat.Guard.Begun);
        var bodies = await lab.Api.WaitForAsync(2);
        await lab.IdleAsync();
        Assert.Equal(new[] { "demo-server", "other-server" }, bodies.Select(b => (string)b["server_name"]!).Order().ToArray());
    }

    [Fact]
    public async Task V9_an_empty_project_id_sends_nothing()
    {
        await using var lab = new HeartbeatLab();
        var server = await lab.StartAsync(HeartbeatLab.DemoServer(), project: "");

        await server.CallToolAsync("alpha");
        await server.ListToolsAsync();

        Assert.Equal(0, lab.Heartbeat.Guard.Begun);
        Assert.Equal(0, lab.Api.Count);
    }

    [Fact]
    public async Task V9_tracing_off_sends_nothing()
    {
        await using var lab = new HeartbeatLab();
        var server = await lab.StartAsync(HeartbeatLab.DemoServer(), options: new AgentCatOptions { EnableTracing = false });

        await server.CallToolAsync("alpha");
        await server.ListToolsAsync();
        await server.CallToolAsync("get_more_tools", "{\"context\":\"I need a tool\"}");

        Assert.Equal(0, lab.Heartbeat.Guard.Begun);
        Assert.Equal(0, lab.Api.Count);
    }

    [Fact]
    public async Task V9_agentcat_turned_off_after_the_install_sends_nothing()
    {
        var enabled = "true";
        var log = new RecordingLog();
        await using var lab = new HeartbeatLab(gate: new EnabledGate(_ => Volatile.Read(ref enabled), static () => false, log));
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        Volatile.Write(ref enabled, "false");
        var result = await server.CallToolAsync("alpha");
        await lab.IdleAsync();

        Assert.Equal("ok", TextOf(result));
        Assert.Equal(0, lab.Api.Count);
        Assert.Equal("Server info heartbeat not sent: AgentCat is off", Assert.Single(lab.HeartbeatLines()));
    }

    [Fact]
    public async Task V10_an_api_that_never_answers_does_not_hold_the_tool_call()
    {
        await using var lab = new HeartbeatLab();
        lab.Api.Respond(RecordingServerInfoSender.Hang);
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        var result = await server.CallToolAsync("alpha");
        await lab.Api.WaitForAsync(1);
        var next = await server.CallToolAsync("gamma");

        Assert.Equal("ok", TextOf(result));
        Assert.Equal("ok", TextOf(next));
        Assert.Empty(lab.HeartbeatLines());

        lab.Heartbeat.SendTimeout = TimeSpan.FromMilliseconds(50);
        lab.Advance(3600);
        lab.Api.Respond(RecordingServerInfoSender.Hang);
        await server.CallToolAsync("alpha");
        await lab.Api.WaitForAsync(2);
        await WaitForLineAsync(lab, "Server info heartbeat not sent: TimeoutException: the API did not answer within 50 ms");
        Assert.Equal(2, lab.Api.Count);
    }

    [Fact]
    public async Task V10_a_list_handler_that_never_returns_does_not_hold_the_tool_call()
    {
        await using var lab = new HeartbeatLab();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<ListPromptsResult>();
        var harness = HeartbeatLab.DemoServer().Configure(o => HeartbeatLab.SetPrompts(o, (_, _) =>
        {
            entered.TrySetResult(true);
            return new ValueTask<ListPromptsResult>(never.Task);
        }));
        lab.Heartbeat.ListTimeout = TimeSpan.FromMilliseconds(200);
        var server = await lab.StartAsync(harness);

        var result = await server.CallToolAsync("alpha");

        Assert.Equal("ok", TextOf(result));
        await FakeAgentCatApi.WithinAsync(entered.Task, FakeAgentCatApi.WaitBound);
        var features = (await lab.SingleBodyAsync())["features"]!.AsObject();
        Assert.False(features.ContainsKey("prompts"));
        Assert.Equal(4, features["tools"]!.AsArray().Count);
        Assert.Contains("Server info heartbeat leaves out the prompts list: TimeoutException: prompts/list did not finish within 200 ms", lab.Log.Lines);
    }

    [Fact]
    public async Task V10_the_tool_call_returns_while_the_tool_list_of_the_heartbeat_is_still_running()
    {
        await using var lab = new HeartbeatLab();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = false;
        var harness = HeartbeatLab.DemoServer().Configure(o =>
        {
            var list = o.Handlers.ListToolsHandler!;
            o.Handlers.ListToolsHandler = async (context, token) =>
            {
                if (Volatile.Read(ref holding))
                {
                    entered.TrySetResult(true);
                    await release.Task;
                }

                return await list(context, token);
            };
        });
        var server = await lab.StartAsync(harness);
        await FirstHeartbeatAsync(lab, server);

        Volatile.Write(ref holding, true);
        var result = await server.CallToolAsync("alpha");

        Assert.Equal("ok", TextOf(result));
        await FakeAgentCatApi.WithinAsync(entered.Task, FakeAgentCatApi.WaitBound);
        Assert.Equal(2, lab.Heartbeat.Guard.Begun);
        Assert.Equal(1, lab.Api.Count);
        release.SetResult(true);
        Assert.Equal(2, (await lab.Api.WaitForAsync(2)).Count);
        await lab.IdleAsync();
    }

    [Fact]
    public async Task V10_a_500_ends_the_attempt_without_a_second_post_and_no_error_escapes()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.ToString().Contains(Boom, StringComparison.Ordinal))
            {
                lock (unobserved)
                {
                    unobserved.Add(e.Exception);
                }
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await using var lab = new HeartbeatLab();
            lab.Api.Respond(500);
            var harness = HeartbeatLab.DemoServer().Configure(o =>
            {
                HeartbeatLab.SetPrompts(o, (_, _) => new ValueTask<ListPromptsResult>(Task.FromException<ListPromptsResult>(new InvalidOperationException(Boom))));
                o.Handlers.ListResourcesHandler = (_, _) => throw new InvalidOperationException(Boom);
            });
            var server = await lab.StartAsync(harness);

            var result = await server.CallToolAsync("alpha");
            await lab.IdleAsync();
            var next = await server.CallToolAsync("gamma");
            await server.ListToolsAsync();

            Assert.Equal("ok", TextOf(result));
            Assert.Equal("ok", TextOf(next));
            Assert.Equal(1, lab.Api.Count);
            Assert.Equal(1, lab.Heartbeat.Guard.Begun);
            Assert.Contains("Server info heartbeat not sent: HTTP 500", lab.Log.Lines);
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task V10_the_whole_build_has_a_time_limit()
    {
        await using var lab = new HeartbeatLab();
        var never = new TaskCompletionSource<string>();
        lab.Heartbeat.BuildTimeout = TimeSpan.FromMilliseconds(100);
        var options = new AgentCatOptions { RedactSensitiveInformation = (_, _) => new ValueTask<string>(never.Task) };
        var server = await lab.StartAsync(HeartbeatLab.DemoServer(), options: options);

        var result = await server.ListToolsAsync();
        await lab.IdleAsync();

        Assert.Equal(4, result.Tools.Count);
        Assert.Equal(0, lab.Api.Count);
        Assert.Equal(
            "Server info heartbeat not sent: TimeoutException: the lists and the redaction did not finish within 100 ms",
            Assert.Single(lab.HeartbeatLines()));
    }

    [Fact]
    public async Task V11_the_redaction_function_runs_on_the_text_and_not_on_names_and_uris()
    {
        await using var lab = new HeartbeatLab();
        var harness = HeartbeatLab.Describe(new McpHarness(), "secret-server", "the secret plan", title: "A secret title")
            .AddHandlerTool(
                "{\"name\":\"secret_tool\",\"title\":\"secret title\",\"description\":\"secret tool\","
                + "\"inputSchema\":{\"type\":\"object\",\"properties\":{\"secret\":{\"type\":\"string\",\"description\":\"a secret\"}}}}",
                (_, _) => HeartbeatLab.Ok())
            .Configure(o =>
            {
                HeartbeatLab.SetResources(
                    o,
                    "{\"resources\":[{\"uri\":\"file:///secret.txt\",\"name\":\"secret_file\",\"description\":\"secret file\"}]}",
                    "{\"resourceTemplates\":[{\"uriTemplate\":\"secret:///{id}\",\"name\":\"secret_items\",\"description\":\"secret items\"}]}");
                HeartbeatLab.SetPrompts(
                    o,
                    "{\"prompts\":[{\"name\":\"secret_prompt\",\"description\":\"secret prompt\",\"arguments\":[{\"name\":\"secret\",\"description\":\"secret argument\"}]}]}");
            });
        var options = new AgentCatOptions
        {
            RedactSensitiveInformation = (text, _) => new ValueTask<string>(text.Replace("secret", "[x]", StringComparison.Ordinal)),
        };
        var server = await lab.StartAsync(harness, options: options);

        await server.CallToolAsync("secret_tool");

        var body = await lab.SingleBodyAsync();
        Assert.Equal("secret-server", (string)body["server_name"]!);
        Assert.Equal("A [x] title", (string)body["server_title"]!);
        Assert.Equal("the [x] plan", (string)body["instructions"]!);

        var tool = body["features"]!["tools"]![0]!;
        Assert.Equal("secret_tool", (string)tool["name"]!);
        Assert.Equal("[x] title", (string)tool["title"]!);
        Assert.Equal("[x] tool", (string)tool["description"]!);
        var property = tool["inputSchema"]!["properties"]!["secret"]!;
        Assert.Equal("a [x]", (string)property["description"]!);
        Assert.Equal("string", (string)property["type"]!);

        var resource = body["features"]!["resources"]![0]!;
        Assert.Equal("file:///secret.txt", (string)resource["uri"]!);
        Assert.Equal("secret_file", (string)resource["name"]!);
        Assert.Equal("[x] file", (string)resource["description"]!);

        var template = body["features"]!["resourceTemplates"]![0]!;
        Assert.Equal("secret:///{id}", (string)template["uriTemplate"]!);
        Assert.Equal("[x] items", (string)template["description"]!);

        var prompt = body["features"]!["prompts"]![0]!;
        Assert.Equal("secret_prompt", (string)prompt["name"]!);
        Assert.Equal("[x] prompt", (string)prompt["description"]!);
        Assert.Equal("[x]", (string)prompt["arguments"]![0]!["name"]!);
        Assert.Equal("[x] argument", (string)prompt["arguments"]![0]!["description"]!);
    }

    [Fact]
    public async Task V12_a_failing_redaction_function_sends_nothing()
    {
        await using var lab = new HeartbeatLab();
        var options = new AgentCatOptions { RedactSensitiveInformation = (_, _) => throw new InvalidOperationException(Boom) };
        var server = await lab.StartAsync(HeartbeatLab.DemoServer(), options: options);

        var result = await server.ListToolsAsync();
        await lab.IdleAsync();

        Assert.Equal(4, result.Tools.Count);
        Assert.Equal(0, lab.Api.Count);
        Assert.Equal("Server info heartbeat not sent: InvalidOperationException: " + Boom, Assert.Single(lab.HeartbeatLines()));
    }

    [Fact]
    public async Task V12_a_redaction_function_that_returns_no_string_sends_nothing()
    {
        await using var lab = new HeartbeatLab();
        var options = new AgentCatOptions { RedactSensitiveInformation = (_, _) => new ValueTask<string>((string)null!) };
        var server = await lab.StartAsync(HeartbeatLab.DemoServer(), options: options);

        await server.ListToolsAsync();
        await lab.IdleAsync();

        Assert.Equal(0, lab.Api.Count);
        Assert.Equal(
            "Server info heartbeat not sent: InvalidOperationException: the redaction function returned no string",
            Assert.Single(lab.HeartbeatLines()));
    }

    [Fact]
    public async Task V13_a_413_sends_one_more_time_without_the_tool_schemas()
    {
        await using var lab = new HeartbeatLab();
        lab.Api.Respond(413, 202);
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        await server.CallToolAsync("alpha");

        var bodies = await lab.Api.WaitForAsync(2);
        await lab.IdleAsync();
        Assert.Equal(2, lab.Api.Count);
        var full = bodies[0]["features"]!["tools"]!.AsArray();
        var small = bodies[1]["features"]!["tools"]!.AsArray();
        Assert.Equal(full.Count, small.Count);
        for (var i = 0; i < full.Count; i++)
        {
            Assert.NotNull(full[i]!["inputSchema"]);
            var stripped = full[i]!.DeepClone().AsObject();
            stripped.Remove("inputSchema");
            stripped.Remove("outputSchema");
            Assert.True(JsonNode.DeepEquals(stripped, small[i]), small[i]!.ToJsonString());
        }

        Assert.Equal(HeartbeatLab.LongDescription, (string)small[1]!["description"]!);
        Assert.Equal("Beta Tool", (string)small[1]!["title"]!);
        foreach (var key in new[] { "project_id", "server_name", "server_version", "server_title", "instructions" })
        {
            Assert.Equal((string)bodies[0][key]!, (string)bodies[1][key]!);
        }

        foreach (var list in new[] { "resources", "resourceTemplates", "prompts" })
        {
            Assert.True(JsonNode.DeepEquals(bodies[0]["features"]![list], bodies[1]["features"]![list]));
        }

        Assert.Equal(
            "Sent server info heartbeat | 4 tools | " + lab.Api.RawBodies[1].Length + " bytes",
            Assert.Single(lab.HeartbeatLines()));
    }

    [Fact]
    public async Task V13_a_second_413_ends_the_attempt()
    {
        await using var lab = new HeartbeatLab();
        lab.Api.Respond(413, 413);
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        await server.CallToolAsync("alpha");
        await lab.IdleAsync();
        await server.CallToolAsync("alpha");

        Assert.Equal(2, lab.Api.Count);
        Assert.Equal(1, lab.Heartbeat.Guard.Begun);
        Assert.Equal("Server info heartbeat not sent: HTTP 413", Assert.Single(lab.HeartbeatLines()));
    }

    [Fact]
    public async Task V15_the_heartbeat_has_the_server_name_and_version_of_the_event()
    {
        await using var lab = new HeartbeatLab();
        var server = await lab.StartAsync(HeartbeatLab.DemoServer("named-like-the-event"));

        await server.CallToolAsync("alpha");

        var body = await lab.SingleBodyAsync();
        Assert.True(await lab.Runtime.Pipeline.FlushAsync(FakeAgentCatApi.WaitBound, TestContext.Current.CancellationToken));
        var e = Assert.Single(lab.Events.Sent);
        Assert.Equal("named-like-the-event", e.ServerName);
        Assert.Equal(e.ServerName, (string)body["server_name"]!);
        Assert.Equal(e.ServerVersion, (string)body["server_version"]!);
        Assert.Equal(e.ProjectId, (string)body["project_id"]!);
    }

    [Fact]
    public async Task V17_a_new_memory_sends_for_a_tool_list_and_for_a_feedback_note_and_not_for_a_tool_call()
    {
        await using var lab = new HeartbeatLab();
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        lab.NewMemory();
        await server.CallToolAsync("alpha");
        Assert.Equal(0, lab.Heartbeat.Guard.Begun);

        await server.ListToolsAsync();
        Assert.Equal(1, lab.Heartbeat.Guard.Begun);
        Assert.Single(await lab.Api.WaitForAsync(1));

        lab.NewMemory();
        var note = await server.CallToolAsync("get_more_tools", "{\"context\":\"I need a tool that sends mail\"}");
        Assert.Equal(2, lab.Heartbeat.Guard.Begun);
        Assert.Equal(2, (await lab.Api.WaitForAsync(2)).Count);
        await lab.IdleAsync();
        Assert.NotEmpty(TextOf(note));
    }

    [Fact]
    public async Task V18_with_no_memory_between_requests_only_the_tool_list_requests_send()
    {
        await using var lab = new HeartbeatLab();
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());

        for (var i = 0; i < 3; i++)
        {
            lab.NewMemory();
            Assert.Equal("ok", TextOf(await server.CallToolAsync("alpha")));
            Assert.Equal(i, lab.Heartbeat.Guard.Begun);

            lab.NewMemory();
            Assert.Equal(4, (await server.ListToolsAsync()).Tools.Count);
            Assert.Equal(i + 1, lab.Heartbeat.Guard.Begun);
        }

        Assert.Equal(3, (await lab.Api.WaitForAsync(3)).Count);
        await lab.IdleAsync();
        Assert.Equal(3, lab.Api.Count);
    }

    [Fact]
    public async Task V19_a_tool_call_sends_once_the_memory_is_60_seconds_old()
    {
        await using var lab = new HeartbeatLab();
        var server = await lab.StartAsync(HeartbeatLab.DemoServer());
        lab.NewMemory();

        await server.CallToolAsync("alpha");
        Assert.Equal(0, lab.Heartbeat.Guard.Begun);

        lab.Advance(59);
        await server.CallToolAsync("alpha");
        Assert.Equal(0, lab.Heartbeat.Guard.Begun);

        lab.Advance(1);
        await server.CallToolAsync("alpha");
        Assert.Equal(1, lab.Heartbeat.Guard.Begun);
        await lab.SingleBodyAsync();
    }

    [Fact]
    public async Task The_list_handlers_get_a_context_of_the_live_server()
    {
        await using var lab = new HeartbeatLab();
        McpServerOptions? seenOptions = null;
        string? seenMethod = null;
        var harness = HeartbeatLab.DemoServer().Configure(o => HeartbeatLab.SetPrompts(o, (context, _) =>
        {
            seenOptions = context.Server.ServerOptions;
            seenMethod = context.JsonRpcRequest.Method;
            return new ValueTask<ListPromptsResult>(HeartbeatLab.Json<ListPromptsResult>("{\"prompts\":[]}"));
        }));
        var server = await lab.StartAsync(harness);

        await server.CallToolAsync("alpha");

        var features = (await lab.SingleBodyAsync())["features"]!;
        Assert.Empty(features["prompts"]!.AsArray());
        Assert.Same(server.ServerOptions, seenOptions);
        Assert.Equal("prompts/list", seenMethod);
    }

    private static async Task WaitForLineAsync(HeartbeatLab lab, string line)
    {
        var deadline = DateTime.UtcNow + FakeAgentCatApi.WaitBound;
        while (!lab.Log.Lines.Contains(line))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("No log line \"" + line + "\". Log: " + string.Join(" / ", lab.Log.Lines));
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
